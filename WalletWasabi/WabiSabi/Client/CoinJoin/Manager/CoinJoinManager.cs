using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using WalletWasabi.Exceptions;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using static WalletWasabi.Logging.LoggerTools;

namespace WalletWasabi.WabiSabi.Client.CoinJoin.Manager;

public delegate Task<IEnumerable<Wallet>> WalletProvider();

public class CoinJoinManager : BackgroundService
{
	public CoinJoinManager(
		WalletProvider getWallets,
		RoundStateProvider roundStatusProvider,
		Func<string, IWabiSabiApiRequestHandler> arenaRequestHandlerFactory,
		CoinJoinConfiguration coinJoinConfiguration,
		CoinPrison coinPrison,
		InputVerifier inputVerifier,
		EventBus eventBus)
	{
		_state = new();
		_getWalletsAsync = getWallets;
		ArenaRequestHandlerFactory = arenaRequestHandlerFactory;
		_roundStatusProvider = roundStatusProvider;
		_coinJoinConfiguration = coinJoinConfiguration;
		_coinPrison = coinPrison;
		_inputVerifier = inputVerifier;
		_serverTipHeightChangeSubscription = eventBus.Subscribe<NetworkTipHeightChanged>(h => _serverTipHeight = h.Height);
		_mailboxProcessor = new MailboxProcessor<CoinJoinCommand>(nameof(CoinJoinManager), HandleCoinJoinCommandsAsync, cancellationToken: _stopCts.Token);
	}

	public event EventHandler<StatusChangedEventArgs>? StatusChanged;

	private readonly ManagerState _state;
	public ImmutableDictionary<WalletId, ImmutableList<SmartCoin>> CoinsInCriticalPhase => _state.CoinsInCriticalPhase;
	private readonly WalletProvider _getWalletsAsync;
	private Func<string, IWabiSabiApiRequestHandler> ArenaRequestHandlerFactory { get; }
	private readonly RoundStateProvider _roundStatusProvider;
	private readonly CoinPrison _coinPrison;
	private readonly InputVerifier _inputVerifier;
	private readonly CoinRefrigerator _coinRefrigerator = new();
	private readonly CoinJoinConfiguration _coinJoinConfiguration;
	private uint _serverTipHeight;
	private readonly MailboxProcessor<CoinJoinCommand> _mailboxProcessor;
	private readonly CancellationTokenSource _stopCts = new();

	public CoinJoinClientState HighestCoinJoinClientState => _state.CoinJoinClientStates.Values.Any()
		? _state.CoinJoinClientStates.Values.MaxBy(s => (int)s)
		: CoinJoinClientState.Idle;

	private readonly IDisposable _serverTipHeightChangeSubscription;

	#region Public API (Start | Stop | TryGetWalletStatus)

	/// <summary>
	/// Asks for one coinjoin of the wallet: the next round that suits its coins, with the blame rounds that follow if that
	/// round is disrupted. When it is over, however it ends, the wallet stops. Another round takes another request.
	/// </summary>
	public void RequestCoinJoinStart(Wallet wallet)
	{
		_mailboxProcessor.Post(new StartCoinJoinCommand(wallet));
	}

	public void RequestCoinJoinStop(Wallet wallet)
	{
		_mailboxProcessor.Post(new StopCoinJoinCommand(wallet));
	}

	public CoinJoinClientState GetCoinjoinClientState(WalletId walletId)
	{
		if (_state.CoinJoinClientStates.TryGetValue(walletId, out var coinJoinClientState))
		{
			return coinJoinClientState;
		}
		throw new ArgumentException($"Wallet {walletId} is not tracked.");
	}

	#endregion Public API (Start | Stop | TryGetWalletStatus)

	/// <summary>
	/// Tells whether a coinjoin that ended was taken down by a request to stop it, before the round that it was started for
	/// came to an end. The send workflow and the shutdown stop a coinjoin and start it again when they are over, and that
	/// is right for an interrupted one only. A coinjoin that ran to its end was the round that the user asked for, although
	/// it was asked to stop: in the critical phase it cannot. The next round is the user's to start.
	/// </summary>
	public static bool WasInterrupted(bool isStopped, TaskStatus coinJoinTaskStatus) =>
		isStopped && coinJoinTaskStatus != TaskStatus.RanToCompletion;

	public override Task StartAsync(CancellationToken cancellationToken)
	{
		_mailboxProcessor.Start();
		return base.StartAsync(cancellationToken);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		var monitorCoinJoinTask = Task.Run(() => MonitorAndHandlingCoinJoinFinalizationAsync(stoppingToken), stoppingToken);

		await WaitAndHandleResultOfTasksAsync(nameof(monitorCoinJoinTask), monitorCoinJoinTask).ConfigureAwait(false);
	}

	public override Task StopAsync(CancellationToken cancellationToken)
	{
		_stopCts.Cancel();
		return base.StopAsync(cancellationToken);
	}

	private async Task HandleCoinJoinCommandsAsync(Mailbox<CoinJoinCommand> mailbox, CancellationToken cancellationToken)
	{
		var coinJoinTrackerFactory = new CoinJoinTrackerFactory(ArenaRequestHandlerFactory, _roundStatusProvider, _coinJoinConfiguration, _inputVerifier, cancellationToken);

		// TODO: Use Workers.EventDriven once we get state ready for it.
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				var command = await mailbox.ReceiveAsync(cancellationToken).ConfigureAwait(false);

				switch (command)
				{
					case StartCoinJoinCommand startCommand:
						HandleStartCoinJoinCommand(startCommand, coinJoinTrackerFactory);
						break;

					case StopCoinJoinCommand stopCommand:
						HandleStopCoinJoinCommand(stopCommand);
						break;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				Logger.LogDebug("Handling of commands in CoinJoinManager was stopped.");
			}
			catch (Exception e)
			{
				Logger.LogError($"Error while handling CoinJoin command: {e}");
			}
		}
	}

	private void HandleStartCoinJoinCommand(StartCoinJoinCommand startCommand, CoinJoinTrackerFactory coinJoinTrackerFactory)
	{
		var walletToStart = startCommand.Wallet;

		if (_state.TrackedCoinJoins.TryGetValue(walletToStart.WalletId, out var tracker))
		{
			if (tracker.IsCancelled)
			{
				// A stop took this coinjoin down, and it is gone within a moment. Nothing starts another one by itself
				// then, so the start that is asked for now is kept for that moment: a Play that follows a Stop closely,
				// or a send dialog that is closed as soon as it opened.
				tracker.IsStartRequested = true;
				Logger.LogDebug(FormatLog("The coinjoin is being stopped. The one that is asked for starts when it is gone.", walletToStart));
				return;
			}

			Logger.LogDebug(FormatLog("Cannot start coinjoin, because it is already running.", walletToStart));

			// A stop that came in the critical phase could not end the coinjoin, it only marked it. This takes the mark back:
			// the coinjoin that is running is the one that was asked for.
			tracker.IsStopped = false;

			return;
		}

		IEnumerable<SmartCoin> SanityChecksAndGetCoinCandidatesFunc()
		{
			if (_state.WalletsBlockedByUi.ContainsKey(walletToStart.WalletId))
			{
				throw new CoinJoinClientException(CoinjoinError.UserInSendWorkflow);
			}

			var coinSelectionResult = SelectCandidateCoins(walletToStart);
			var coinCandidates = coinSelectionResult.CandidateCoins;

			// If there are pending payments, ignore already achieved privacy.
			if (!walletToStart.BatchedPayments.AreTherePendingPayments)
			{
				// If all coins are already private, then don't mix.
				if (walletToStart.IsWalletPrivate())
				{
					Logger.LogTrace(FormatLog("All mixed!", walletToStart));
					throw new CoinJoinClientException(CoinjoinError.AllCoinsPrivate);
				}

				// If all coin candidates are private it makes no sense to mix them.
				if (coinCandidates.All(x => x.IsPrivate(walletToStart.AnonScoreTarget)))
				{
					throw new CoinJoinClientException(
						GetUnavailableNonPrivateCoinsError(coinSelectionResult, walletToStart),
						"All coin candidates are already private.");
				}
			}

			NotifyWalletStartedCoinJoin(walletToStart);

			return coinCandidates;
		}

		var coinJoinTracker = coinJoinTrackerFactory.CreateAndStart(walletToStart, SanityChecksAndGetCoinCandidatesFunc);

		if (!_state.TrackedCoinJoins.TryAdd(walletToStart.WalletId, coinJoinTracker))
		{
			// This should never happen.
			Logger.LogError(FormatLog($"{nameof(CoinJoinTracker)} was already added.", walletToStart));
			coinJoinTracker.Stop();
			coinJoinTracker.Dispose();
			return;
		}

		coinJoinTracker.WalletCoinJoinProgressChanged += CoinJoinTracker_WalletCoinJoinProgressChanged;

		var registrationTimeout = TimeSpan.MaxValue;
		NotifyCoinJoinStarted(walletToStart, registrationTimeout);

		Logger.LogDebug(FormatLog($"{nameof(CoinJoinClient)} started.", walletToStart));
	}

	private void HandleStopCoinJoinCommand(StopCoinJoinCommand stopCommand)
	{
		var walletToStop = stopCommand.Wallet;

		if (_state.TrackedCoinJoins.TryGetValue(walletToStop.WalletId, out var coinJoinTrackerToStop))
		{
			coinJoinTrackerToStop.Stop();
			if (coinJoinTrackerToStop.InCriticalCoinJoinState)
			{
				Logger.LogWarning(FormatLog("Coinjoin is in critical phase, it cannot be stopped.", walletToStop));
			}
		}
	}

	private record CoinSelectionResult(SmartCoin[] CandidateCoins, SmartCoin[] BannedCoins, SmartCoin[] ImmatureCoins, SmartCoin[] UnconfirmedCoins, SmartCoin[] ExcludedCoins)
	{
		public CoinSelectionResult() : this([], [], [], [], []) { }
	}

	private CoinSelectionResult GetCoinSelection(Wallet wallet)
	{
		var coinCandidates = new CoinsView(wallet.GetCoinjoinCoinCandidates())
			.Available()
			.Where(x => !_coinRefrigerator.IsFrozen(x))
			.ToArray();

		if (coinCandidates.Length == 0)
		{
			return new CoinSelectionResult();
		}

		var bannedCoins = coinCandidates.Where(x => _coinPrison.IsBanned(x.Outpoint)).ToArray();
		var immatureCoins = _serverTipHeight > 0
			? coinCandidates.Where(x => x.Transaction.IsImmature(_serverTipHeight)).ToArray()
			: [];
		var unconfirmedCoins = coinCandidates.Where(x => !x.Confirmed).ToArray();
		var excludedCoins = coinCandidates.Where(x => x.IsExcludedFromCoinJoin).ToArray();

		var availableCoins = coinCandidates
			.Except(bannedCoins)
			.Except(immatureCoins)
			.Except(unconfirmedCoins)
			.Except(excludedCoins)
			.ToArray();

		return new CoinSelectionResult(
			availableCoins,
			bannedCoins,
			immatureCoins,
			unconfirmedCoins,
			excludedCoins);
	}

	private CoinSelectionResult SelectCandidateCoins(Wallet wallet)
	{
		var result = GetCoinSelection(wallet);

		if (result.CandidateCoins.Length > 0)
		{
			return result;
		}

		throw new CoinJoinClientException(GetUnavailableNonPrivateCoinsError(result, wallet), "No candidate coins available for coinjoin.");
	}


	private static CoinjoinError GetUnavailableNonPrivateCoinsError(CoinSelectionResult result, Wallet wallet)
	{
		bool AnyNonPrivate(SmartCoin[] coins) => coins.Any(x => !x.IsPrivate(wallet.AnonScoreTarget));

		if (AnyNonPrivate(result.UnconfirmedCoins))
		{
			return CoinjoinError.NoConfirmedCoinsEligibleToMix;
		}

		if (AnyNonPrivate(result.ImmatureCoins))
		{
			return CoinjoinError.OnlyImmatureCoinsAvailable;
		}

		if (AnyNonPrivate(result.ExcludedCoins))
		{
			return CoinjoinError.OnlyExcludedCoinsAvailable;
		}

		if (AnyNonPrivate(result.BannedCoins))
		{
			return CoinjoinError.CoinsRejected;
		}

		return CoinjoinError.NoCoinsEligibleToMix;
	}

	private async Task MonitorAndHandlingCoinJoinFinalizationAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			// Handles coinjoin finalization and notification.
			var finishedCoinJoins = _state.TrackedCoinJoins
				.Where(x => x.Value.IsCompleted)
				.Select(x => x.Value)
				.ToImmutableArray();

			foreach (var finishedCoinJoin in finishedCoinJoins)
			{
				await HandleCoinJoinFinalizationAsync(finishedCoinJoin, stoppingToken).ConfigureAwait(false);
			}

			// Updates coinjoin client states.
			var wallets = await _getWalletsAsync().ConfigureAwait(false);

			_state.CoinJoinClientStates = GetCoinJoinClientStates(wallets);
			_state.CoinsInCriticalPhase = GetCoinsInCriticalPhase(wallets);

			await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
		}
	}

	private ImmutableDictionary<WalletId, ImmutableList<SmartCoin>> GetCoinsInCriticalPhase(IEnumerable<Wallet> wallets)
	{
		var coinsUsedInCoinjoins = ImmutableDictionary.CreateBuilder<WalletId, ImmutableList<SmartCoin>>();

		foreach (var wallet in wallets)
		{
			ImmutableList<SmartCoin> coinsInCoinjoin = [];

			if (_state.TrackedCoinJoins.TryGetValue(wallet.WalletId, out var coinJoinTracker) && !coinJoinTracker.IsCompleted)
			{
				coinsInCoinjoin = coinJoinTracker.CoinsInCriticalPhase;
			}

			coinsUsedInCoinjoins.Add(wallet.WalletId, coinsInCoinjoin);
		}

		return coinsUsedInCoinjoins.ToImmutable();
	}

	private ImmutableDictionary<WalletId, CoinJoinClientState> GetCoinJoinClientStates(IEnumerable<Wallet> wallets)
	{
		var coinJoinClientStates = ImmutableDictionary.CreateBuilder<WalletId, CoinJoinClientState>();
		foreach (var wallet in wallets)
		{
			var clientState = CoinJoinClientState.Idle;

			if (_state.TrackedCoinJoins.TryGetValue(wallet.WalletId, out var coinJoinTracker) && !coinJoinTracker.IsCompleted)
			{
				clientState = coinJoinTracker.InCriticalCoinJoinState
					? CoinJoinClientState.InCriticalPhase
					: CoinJoinClientState.InProgress;
			}

			coinJoinClientStates.Add(wallet.WalletId, clientState);
		}

		return coinJoinClientStates.ToImmutable();
	}

	private async Task HandleCoinJoinFinalizationAsync(CoinJoinTracker finishedCoinJoin, CancellationToken cancellationToken)
	{
		var wallet = finishedCoinJoin.Wallet;
		var destinationProvider = wallet.OutputProvider.DestinationProvider;
		var batchedPayments = wallet.BatchedPayments;
		CoinJoinClientException? cjClientException = null;
		var forceStop = false;
		try
		{
			var result = await finishedCoinJoin.CoinJoinTask.ConfigureAwait(false);
			if (result is SuccessfulCoinJoinResult successfulCoinjoin)
			{
				var coinjoinTxId = successfulCoinjoin.UnsignedCoinJoin.GetHash();
				var paymentsTotal = Money.Satoshis(batchedPayments.GetPayments()
					.Where(p => p.State switch
					{
						SignedUnknownPayment signed => signed.TransactionId == coinjoinTxId,
						FinishedPayment finished => finished.TransactionId == coinjoinTxId,
						_ => false
					})
					.Sum(p => p.Amount));
				_coinRefrigerator.Freeze(successfulCoinjoin.Coins);
				batchedPayments.MovePaymentsToFinished(coinjoinTxId);
				MarkDestinationsUsed(destinationProvider, successfulCoinjoin.OutputScripts);
				wallet.AddCoinjoinCosts(coinjoinTxId, successfulCoinjoin.Costs with { PaymentsTotal = paymentsTotal });
				Logger.LogInfo(FormatLog($"{nameof(CoinJoinClient)} finished. Coinjoin transaction was broadcast.", wallet));
			}
			else
			{
				Logger.LogInfo(FormatLog($"{nameof(CoinJoinClient)} finished. Coinjoin transaction was not broadcast.", wallet));
			}
		}
		catch (UnknownRoundEndingException ex)
		{
			// The round ending is unknown - the transaction might have been broadcast.
			// Signed payments go back to pending, but they are only paid again in a transaction
			// that conflicts with this one.
			_coinRefrigerator.Freeze(ex.Coins);
			MarkDestinationsUsed(destinationProvider, ex.OutputScripts);
			Logger.LogWarning(FormatLog($"Round ending unknown: {ex.Message}", wallet));
		}
		catch (CoinJoinClientException clientException)
		{
			cjClientException = clientException;
			if (cjClientException.CoinjoinError is CoinjoinError.CoordinatorLiedAboutInputs)
			{
				Logger.LogError(cjClientException);
				forceStop = true;
			}
			else
			{
				Logger.LogDebug(cjClientException);
			}
		}
		catch (InvalidOperationException ioe)
		{
			Logger.LogWarning(ioe);
		}
		catch (OperationCanceledException)
		{
			if (finishedCoinJoin.IsStopped)
			{
				Logger.LogInfo($"{nameof(CoinJoinClient)} stopped.", wallet);
			}
			else
			{
				Logger.LogInfo($"{nameof(CoinJoinClient)} was cancelled.", wallet);
			}
		}
		catch (UnexpectedRoundPhaseException e)
		{
			// `UnexpectedRoundPhaseException` indicates an error in the protocol however,
			// temporarily we are shortening the circuit by aborting the rounds if
			// there are Alices that didn't confirm.
			// The fix is already done but the clients have to upgrade.
			Logger.LogInfo(FormatLog($"{nameof(CoinJoinClient)} failed with exception: '{e}'", wallet));
		}
		catch (WabiSabiProtocolException wpe) when (wpe.ErrorCode == WabiSabiProtocolErrorCode.WrongPhase)
		{
			// This can happen when the coordinator aborts the round in Signing phase because of detected double spend.
			Logger.LogInfo(FormatLog($"{nameof(CoinJoinClient)} failed with: '{wpe.Message}'", wallet));
		}
		catch (Exception e)
		{
			Logger.LogError(FormatLog($"{nameof(CoinJoinClient)} failed with exception: '{e}'", wallet));
		}
		finally
		{
			batchedPayments.MovePaymentsToPending();
		}

		// If any coins were marked for banning, store them to file
		if (finishedCoinJoin.BannedCoins.Count != 0)
		{
			foreach (var info in finishedCoinJoin.BannedCoins)
			{
				_coinPrison.Ban(info.Coin, info.BanUntilUtc);
			}
		}

		NotifyCoinJoinCompletion(finishedCoinJoin);

		// Why there was no coinjoin, unless it was stopped. A coordinator that lied is told in any case.
		if (cjClientException is not null && (forceStop || !finishedCoinJoin.IsStopped))
		{
			NotifyCoinJoinStartError(wallet, cjClientException.CoinjoinError);
		}
		else if (cjClientException is null
			&& !finishedCoinJoin.IsStopped
			&& !cancellationToken.IsCancellationRequested
			&& wallet.IsWalletPrivate()
			&& !wallet.BatchedPayments.AreTherePendingPayments)
		{
			// A fully private wallet is done mixing, unless it is allowed to fund a pending payment with private coins.
			NotifyCoinJoinStartError(wallet, CoinjoinError.AllCoinsPrivate);
		}

		// The send workflow and the shutdown start a coinjoin again that they stopped. This one may have come to its end
		// by itself instead, and then it is not theirs to start again.
		if (!WasInterrupted(finishedCoinJoin.IsStopped, finishedCoinJoin.CoinJoinTask.Status)
			&& _state.WalletsBlockedByUi.TryGetValue(wallet.WalletId, out var blocked)
			&& blocked.NeedRestart)
		{
			_state.WalletsBlockedByUi.TryUpdate(wallet.WalletId, new UiBlockedStateHolder(NeedRestart: false), blocked);
		}

		// A coinjoin is one round. However it ended, the wallet stops here: the next round starts when it is asked for.
		NotifyWalletStoppedCoinJoin(wallet);

		if (!_state.TrackedCoinJoins.TryRemove(wallet.WalletId, out _))
		{
			Logger.LogWarning(FormatLog("Was not removed from tracked wallet list. Will retry in a few seconds.", wallet));
		}
		else
		{
			finishedCoinJoin.WalletCoinJoinProgressChanged -= CoinJoinTracker_WalletCoinJoinProgressChanged;
			finishedCoinJoin.Dispose();

			if (finishedCoinJoin.IsStartRequested && !cancellationToken.IsCancellationRequested)
			{
				// A start was asked for while this coinjoin was being stopped. Its turn has come.
				RequestCoinJoinStart(wallet);
			}
		}
	}

	/// <summary>
	/// Mark all the outputs we had in any of our wallets used.
	/// </summary>
	private void MarkDestinationsUsed(IDestinationProvider destinationProvider, ImmutableList<Script> outputs)
	{
		destinationProvider.TrySetScriptStates(KeyState.Used, outputs);
	}

	private void NotifyWalletStartedCoinJoin(Wallet openedWallet) =>
		StatusChanged.SafeInvoke(this, new WalletStartedCoinJoinEventArgs(openedWallet));

	private void NotifyWalletStoppedCoinJoin(Wallet openedWallet) =>
	StatusChanged.SafeInvoke(this, new WalletStoppedCoinJoinEventArgs(openedWallet));

	private void NotifyCoinJoinStarted(Wallet openedWallet, TimeSpan registrationTimeout) =>
		StatusChanged.SafeInvoke(this, new StartedEventArgs(openedWallet, registrationTimeout));

	private void NotifyCoinJoinStartError(Wallet openedWallet, CoinjoinError error) =>
		StatusChanged.SafeInvoke(this, new StartErrorEventArgs(openedWallet, error));

	private void NotifyCoinJoinCompletion(CoinJoinTracker finishedCoinJoin)
	{
		CompletionStatus status = finishedCoinJoin.CoinJoinTask.Status switch
		{
			TaskStatus.RanToCompletion when finishedCoinJoin.CoinJoinTask.Result is SuccessfulCoinJoinResult => CompletionStatus.Success,
			TaskStatus.Canceled => CompletionStatus.Canceled,
			TaskStatus.Faulted => CompletionStatus.Failed,
			_ => CompletionStatus.Unknown
		};

		CompletedEventArgs e = new(finishedCoinJoin.Wallet, status);
		StatusChanged.SafeInvoke(this, e);
	}

	private void NotifyCoinJoinStatusChanged(Wallet wallet, CoinJoinProgressEventArgs coinJoinProgressEventArgs) =>
		StatusChanged.SafeInvoke(
			this,
			new CoinJoinStatusEventArgs(wallet, coinJoinProgressEventArgs));

	private static async Task WaitAndHandleResultOfTasksAsync(string logPrefix, params Task[] tasks)
	{
		foreach (var task in tasks)
		{
			try
			{
				await task.ConfigureAwait(false);
				Logger.LogInfo($"Task '{logPrefix}' finished successfully.");
			}
			catch (OperationCanceledException)
			{
				Logger.LogInfo($"Task '{logPrefix}' finished successfully by cancellation.");
			}
			catch (Exception ex)
			{
				Logger.LogInfo($"Task '{logPrefix}' finished but with an error: '{ex}'.");
			}
		}
	}

	public void WalletEnteredSendWorkflow(WalletId walletId)
	{
		if (_state.CoinJoinClientStates.ContainsKey(walletId))
		{
			_state.WalletsBlockedByUi.TryAdd(walletId, new UiBlockedStateHolder(NeedRestart: false));
		}
	}

	public void WalletLeftSendWorkflow(Wallet wallet)
	{
		if (!_state.WalletsBlockedByUi.TryRemove(wallet.WalletId, out var stateHolder))
		{
			Logger.LogDebug(FormatLog("Wallet was not in send workflow but left it.", wallet));
			return;
		}

		if (stateHolder.NeedRestart)
		{
			Task.Run(async () => RequestCoinJoinStart(wallet));
		}
	}

	public async Task WalletEnteredSendingAsync(Wallet wallet)
	{
		if (!_state.WalletsBlockedByUi.ContainsKey(wallet.WalletId))
		{
			Logger.LogDebug(FormatLog("Wallet tried to enter sending but it was not in the send workflow.", wallet));
			return;
		}

		// The coinjoin is started again after the send workflow, if one is running now.
		if (IsCoinJoinRunning(wallet.WalletId))
		{
			_state.WalletsBlockedByUi[wallet.WalletId] = new UiBlockedStateHolder(NeedRestart: true);
		}

		RequestCoinJoinStop(wallet);
	}

	public async Task SignalToStopCoinjoinsAsync()
	{
		foreach (var wallet in await _getWalletsAsync().ConfigureAwait(false))
		{
			if (IsCoinJoinRunning(wallet.WalletId))
			{
				_state.WalletsBlockedByUi[wallet.WalletId] = new UiBlockedStateHolder(NeedRestart: true);
				RequestCoinJoinStop(wallet);
			}
		}
	}

	public async Task RestartAbortedCoinjoinsAsync()
	{
		foreach (var wallet in await _getWalletsAsync().ConfigureAwait(false))
		{
			if (_state.WalletsBlockedByUi.TryRemove(wallet.WalletId, out var stateHolder) && stateHolder.NeedRestart)
			{
				RequestCoinJoinStart(wallet);
			}
		}
	}

	/// <summary>Whether a coinjoin of the wallet is under way, one that did not come to its end yet.</summary>
	private bool IsCoinJoinRunning(WalletId walletId) =>
		_state.TrackedCoinJoins.TryGetValue(walletId, out var tracker) && !tracker.IsCompleted;

	private void CoinJoinTracker_WalletCoinJoinProgressChanged(object? sender, CoinJoinProgressEventArgs e)
	{
		if (sender is not Wallet wallet)
		{
			throw new InvalidOperationException("Sender must be a wallet.");
		}

		NotifyCoinJoinStatusChanged(wallet, e);
	}

	public override void Dispose()
	{
		_mailboxProcessor.Dispose();
		_stopCts.Dispose();
		_serverTipHeightChangeSubscription.Dispose();
		base.Dispose();
	}

	private record CoinJoinCommand(Wallet Wallet);
	private record StartCoinJoinCommand(Wallet Wallet) : CoinJoinCommand(Wallet);
	private record StopCoinJoinCommand(Wallet Wallet) : CoinJoinCommand(Wallet);

	/// <param name="NeedRestart">Whether the coinjoin is to be started again when the dialog that blocks it is left.</param>
	private record UiBlockedStateHolder(bool NeedRestart);

	/// <param name="WalletsBlockedByUi">
	/// The wallets whose coinjoins the user interface blocks: the send workflow and the shutdown prevention do that.
	/// </param>
	private record ManagerState(
		ConcurrentDictionary<WalletId, CoinJoinTracker> TrackedCoinJoins,
		ConcurrentDictionary<WalletId, UiBlockedStateHolder> WalletsBlockedByUi)
	{
		public ImmutableDictionary<WalletId, CoinJoinClientState> CoinJoinClientStates { get; set; } = [];
		public ImmutableDictionary<WalletId, ImmutableList<SmartCoin>> CoinsInCriticalPhase { get; set; } = [];

		public ManagerState() : this(
			new ConcurrentDictionary<WalletId, CoinJoinTracker>(),
			new ConcurrentDictionary<WalletId, UiBlockedStateHolder>())
		{
		}
	}
}

public record CoinJoinConfiguration(string CoordinatorIdentifier,  decimal MaxCoinJoinMiningFeeRate, int AbsoluteMinInputCount, bool AllowSoloCoinjoining);
