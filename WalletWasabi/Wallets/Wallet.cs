using Microsoft.Extensions.Hosting;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Userfacing;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using static WalletWasabi.Logging.LoggerTools;

namespace WalletWasabi.Wallets;

public delegate Wallet WalletFactory(KeyManager keyManager);

public class Wallet : BackgroundService
{
	private readonly ComposedDisposable _disposables = new();
	private readonly AnonymityPointsCalculator _anonymityPointsCalculator;

	/// <summary>
	/// Released when the coins of the wallet changed: there may be coins whose anonymity points are not known. It starts
	/// released, because the coins that the wallet has when it starts were not looked at either.
	/// </summary>
	private readonly SemaphoreSlim _coinsChanged = new(initialCount: 1, maxCount: 1);

	/// <param name="rpcClient">The RPC interface of the user's node. The wallet learns everything about the chain from it.</param>
	/// <param name="watchMempool">Whether the wallet looks at the unconfirmed transactions of the node too, or only at its blocks.</param>
	public static WalletFactory CreateFactory(
		Network network, IRPCClient rpcClient, AllTransactionStore transactionStore,
		ServiceConfiguration serviceConfiguration, EventBus eventBus, CpfpInfoProvider cpfpInfoProvider, bool watchMempool = true) =>
		keyManager => new Wallet(network, keyManager, rpcClient, transactionStore, serviceConfiguration, cpfpInfoProvider, eventBus, watchMempool);

	private Wallet(
		Network network,
		KeyManager keyManager,
		IRPCClient rpcClient,
		AllTransactionStore transactionStore,
		ServiceConfiguration serviceConfiguration,
		CpfpInfoProvider cpfpInfoProvider,
		EventBus eventBus,
		bool watchMempool)
	{
		Password = "";
		Network = network;
		KeyManager = keyManager;
		ServiceConfiguration = serviceConfiguration;
		CpfpInfoProvider = cpfpInfoProvider;
		DestinationProvider = new InternalDestinationProvider(KeyManager);
		TransactionStore = transactionStore;

		TransactionProcessor = new TransactionProcessor(TransactionStore, keyManager, eventBus);
		Synchronizer = new WalletSynchronizer(
			rpcClient,
			keyManager,
			TransactionStore,
			TransactionProcessor,
			eventBus,
			onSynchronizedFirst: LoadDummyMempool,
			watchMempool: watchMempool,
			fidelityBondSearchBlocks: serviceConfiguration.FidelityBondSearchBlocks);
		Coins = TransactionProcessor.Coins;
		_anonymityPointsCalculator = new AnonymityPointsCalculator(rpcClient, TransactionStore);
		BatchedPayments = new PaymentBatch(Coins);
		OutputProvider = new PaymentAwareOutputProvider(DestinationProvider, BatchedPayments, RandomnessProviders.Secure, () => KeyManager.MaxCoinjoinLeftover);
		_eventBus = eventBus;
		WalletId = new WalletId(Guid.NewGuid());

		_eventBus.Subscribe<MiningFeeRatesChanged>(e => FeeRateEstimations = e.AllFeeEstimate)
			.DisposeUsing(_disposables);

		_eventBus.Subscribe<WalletRelevantTransactionProcessed>(e => WalletRelevantTransactionProcessed(e.Result))
			.DisposeUsing(_disposables);

		_eventBus.Subscribe<NewTransactionInMempool>(e => Mempool_TransactionReceived(e.Transaction))
			.DisposeUsing(_disposables);
	}

	private readonly EventBus _eventBus;
	public AllTransactionStore TransactionStore { get; }

	public WalletId WalletId { get; }
	public bool Loaded { get; private set; }
	public KeyManager KeyManager { get; }
	public ServiceConfiguration ServiceConfiguration { get; }
	public FeeRateEstimations? FeeRateEstimations { get; private set; }

	public CoinsRegistry Coins { get; }

	public Network Network { get; }
	public TransactionProcessor TransactionProcessor { get; }

	public CpfpInfoProvider CpfpInfoProvider { get; }

	/// <summary>Keeps the wallet up to date with the user's node.</summary>
	public WalletSynchronizer Synchronizer { get; }

	public bool IsLoggedIn { get; private set; }
	public string Password { get; set; }

	public IKeyChain? KeyChain { get; private set; }

	public IDestinationProvider DestinationProvider { get; }

	public OutputProvider OutputProvider { get; }
	public PaymentBatch BatchedPayments { get; }

	/// <summary>
	/// How long the wallet waits before it asks the node again about transactions whose anonymity points it could not
	/// find out: after the first attempt, after the second one, and after every later one. Tests wait less long.
	/// </summary>
	internal IReadOnlyList<TimeSpan> AnonymityPointsRetryDelays { get; set; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

	public ICoinsView GetAllCoins() => Coins.AsAllCoinsView();

	public IEnumerable<SmartCoin> GetCoinjoinCoinCandidates() => Coins;

	/// <summary>
	/// Get all the transactions associated to the wallet ordered by blockchain.
	/// </summary>
	public IEnumerable<SmartTransaction> GetTransactions()
	{
		var walletTransactions = new HashSet<SmartTransaction>();

		foreach (SmartCoin coin in GetAllCoins())
		{
			walletTransactions.Add(coin.Transaction);
			if (coin.SpenderTransaction is not null)
			{
				walletTransactions.Add(coin.SpenderTransaction);
			}
		}

		return walletTransactions.OrderByBlockchain().ToList();
	}

	/// <summary>
	/// Get all wallet transactions along with corresponding amounts ordered by blockchain.
	/// </summary>
	/// <param name="sortForUi"><c>true</c> to sort by "first seen", "height", and "block index", <c>false</c> to sort by "height", "block index", and "first seen".</param>
	/// <remarks>Transaction amount specifies how it affected your final wallet balance (spend some bitcoin, received some bitcoin, or no change).</remarks>
	public async Task<List<TransactionSummary>> BuildHistorySummaryAsync(bool sortForUi = false, CancellationToken cancellationToken = default)
	{
		var cpfpInfos = await CpfpInfoProvider.GetCachedCpfpInfoAsync(cancellationToken).ConfigureAwait(false);

		Dictionary<uint256, TransactionSummary> mapByTxid = new();

		TransactionSummary GetSummary(SmartTransaction transaction, SmartTransaction cpfpSource)
		{
			var txid = transaction.GetHash();

			if (!mapByTxid.TryGetValue(txid, out var summary))
			{
				var effectiveFeeRate = cpfpInfos.FirstOrDefault(x => x.Transaction == cpfpSource) is { } cachedCpfpInfo
					? cachedCpfpInfo.CpfpInfo.EffectiveFeeRate
					: null;

				summary = new TransactionSummary(transaction, Money.Zero, effectiveFeeRate);
				mapByTxid.Add(txid, summary);
			}

			return summary;
		}

		foreach (SmartCoin coin in GetAllCoins())
		{
			GetSummary(coin.Transaction, coin.Transaction).Amount += coin.Amount;

			if (coin.SpenderTransaction is { } spenderTransaction)
			{
				GetSummary(spenderTransaction, coin.Transaction).Amount -= coin.Amount;
			}
		}

		// The coins of a fidelity bond belong to the wallet too. Locking coins of the wallet and getting them back costs
		// it nothing but the mining fees, and a bond that is paid from elsewhere is money that came in.
		foreach (var bond in GetFidelityBonds())
		{
			GetSummary(bond.Transaction, bond.Transaction).AddLockedBond(bond);

			if (bond.SpenderTransaction is { } spenderTransaction)
			{
				GetSummary(spenderTransaction, bond.Transaction).AddUnlockedBond(bond);
			}
		}

		return sortForUi
			? mapByTxid.Values.OrderBy(x => x.FirstSeen).ThenBy(x => x.Height).ThenBy(x => x.BlockIndex).ToList()
			: mapByTxid.Values.OrderByBlockchain().ToList();
	}

	public HdPubKey GetNextReceiveAddress()
	{
		return KeyManager.GetNextReceiveKey();
	}

	/// <summary>
	/// The fidelity bonds of the wallet, the redeemed ones included. The coins of a bond belong to the wallet, but a bond
	/// is not one of its coins: nothing spends it but the transaction that redeems it.
	/// </summary>
	public IReadOnlyList<FidelityBond> GetFidelityBonds() => TransactionProcessor.GetFidelityBonds();

	/// <summary>What the bonds of the wallet hold that were not redeemed yet. It is part of what the wallet owns.</summary>
	public Money GetFidelityBondBalance() => GetFidelityBonds().Where(bond => !bond.IsSpent).Sum(bond => bond.Amount);

	/// <summary>
	/// Builds the transaction that pays a bond whose timelock is over back to the wallet, to an address of its own.
	/// </summary>
	/// <param name="chainHeight">The height of the last block of the chain, which decides whether the timelock is over.</param>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	/// <exception cref="System.Security.SecurityException">The password is wrong.</exception>
	public SmartTransaction BuildFidelityBondRedemption(FidelityBond bond, string password, FeeRate feeRate, uint chainHeight)
	{
		using var key = KeyManager.GetFidelityBondSecret(password, bond.UnlockHeight);
		var destination = KeyManager.GetNextChangeKey().GetAssumedScriptPubKey();
		var transaction = BondRedemption.Create(bond, key, destination, feeRate, chainHeight, Network);

		return new SmartTransaction(transaction, Height.Unknown);
	}

	/// <summary>
	/// The fidelity bond that stands behind the offers of the wallet, with the proof of it for the offer of the given
	/// key: the largest bond that is confirmed and still locked. Null if the wallet has no such bond, or is not open.
	/// </summary>
	/// <param name="offerKey">The public key that signs the offer, by its x coordinate: 32 bytes.</param>
	public BondBacking? GetBondBacking(byte[] offerKey)
	{
		if (!IsLoggedIn || KeyManager.GetBestHeight() is not { } bestHeight)
		{
			return null;
		}

		var bond = GetFidelityBonds()
			.Where(bond => !bond.IsSpent && bond.IsConfirmed && !bond.IsUnlocked(bestHeight.Height))
			.OrderByDescending(bond => bond.Amount)
			.ThenBy(bond => bond.OutPoint.ToString(), StringComparer.Ordinal)
			.FirstOrDefault();

		if (bond is null)
		{
			return null;
		}

		using var key = KeyManager.GetFidelityBondSecret(Password, bond.UnlockHeight);

		return new BondBacking(BondProof.Create(key, bond.OutPoint, bond.UnlockHeight, offerKey), bond.Amount.Satoshi);
	}

	public bool TryLogin(string password)
	{
		if (PasswordHelper.TryPassword(KeyManager, password))
		{
			IsLoggedIn = true;
			Password = Guard.Correct(password);
			KeyChain = new KeyChain(KeyManager, Password);
		}

		return IsLoggedIn;
	}

	public void Logout()
	{
		IsLoggedIn = false;
	}

	/// <inheritdoc/>
	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		LoadWalletState();
		Logger.LogTrace(FormatLog("State is loaded.", this));

		// The blocks that the wallet did not see yet come from the node. The unconfirmed transactions
		// that the wallet remembers are loaded when that is done, see the synchronizer.
		await Synchronizer.StartAsync(cancellationToken).ConfigureAwait(false);
		Logger.LogTrace(FormatLog("Waiting for the synchronization with the node to finish.", this));
		await Synchronizer.InitialSynchronizationFinished.WaitAsync(cancellationToken).ConfigureAwait(false);

		LoadCoinsSelectedForCoinJoin();

		// The wallet has all of its transactions now, so it can tell which change addresses were paid to. The gap limit is
		// counted from the last of them, and the wallet file may be wrong about some.
		if (TransactionProcessor.ForgetPaymentsThatNeverCame() is var unpaid and > 0)
		{
			Logger.LogInfo(FormatLog($"Nothing was paid to {unpaid} change addresses that the wallet file had as paid to. They count as given out.", this));
		}

		// For the same reason the wallet knows now where its addresses go on, if it came back from its backup and did not
		// know: a distance after the last ones that were paid to, see KeyManager.RecoveryAddressSkip.
		if (KeyManager.EndAddressSearch() is { } first)
		{
			Logger.LogInfo(FormatLog($"went through the chain. It gives out receive addresses from position {first.Receive} on and change addresses from position {first.Change} on.", this));
		}

		await base.StartAsync(cancellationToken).ConfigureAwait(false);

		Loaded = true;
		_eventBus.Publish(new WalletLoaded(this));
	}

	/// <summary>
	/// Puts the choices of the user that the wallet file holds on the coins. The wallet has all of its coins now, so a
	/// choice without a coin is one whose coin was spent: it is forgotten.
	/// </summary>
	private void LoadCoinsSelectedForCoinJoin()
	{
		var coins = Coins.ToDictionary(coin => coin.Outpoint);
		var selected = KeyManager.CoinsSelectedForCoinJoin.Where(coins.ContainsKey).ToArray();

		foreach (var outpoint in selected)
		{
			coins[outpoint].IsSelectedForCoinJoin = true;
		}

		KeyManager.SetCoinsSelectedForCoinJoin(selected);
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Logger.LogInfo(FormatLog("is fully synchronized.", this));

		// From here on the wallet has all of its coins, and the node has time for other questions than blocks.
		await Task.Yield();
		await KeepAnonymityPointsKnownAsync(stoppingToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Finds out the anonymity points of the coins of the wallet, again whenever its coins changed, until it is stopped.
	/// What the node cannot tell at once is asked for again later.
	/// </summary>
	internal async Task KeepAnonymityPointsKnownAsync(CancellationToken cancellationToken)
	{
		// How often the wallet waited and then asked in vain again.
		var retriesInVain = 0;

		// Nothing is waited for the first time: the semaphore starts released.
		var retryDelay = Timeout.InfiniteTimeSpan;

		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				var coinsChanged = await _coinsChanged.WaitAsync(retryDelay, cancellationToken).ConfigureAwait(false);

				if (await FindOutAnonymityPointsAsync(cancellationToken).ConfigureAwait(false))
				{
					retriesInVain = 0;
					retryDelay = Timeout.InfiniteTimeSpan;
					continue;
				}

				// Only having waited in vain makes the next wait longer. When changed coins made the wallet look,
				// that tells nothing about how long the node has been failing to answer.
				if (!coinsChanged)
				{
					retriesInVain++;
				}

				retryDelay = AnonymityPointsRetryDelays[Math.Min(retriesInVain, AnonymityPointsRetryDelays.Count - 1)];
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				Logger.LogError(FormatLog(ex.ToString(), this));
				retryDelay = AnonymityPointsRetryDelays[^1];
			}
		}
	}

	/// <summary>
	/// Finds out the anonymity points of the transactions that created unspent coins of the wallet, as far as they are
	/// not known yet. Only matched outputs get the points of their transaction, so only their transactions are looked at.
	/// </summary>
	/// <returns>Whether the points of all these transactions are known now.</returns>
	internal async Task<bool> FindOutAnonymityPointsAsync(CancellationToken cancellationToken)
	{
		var transactions = Coins
			.Where(coin => coin.IsMatchedOutput)
			.Select(coin => coin.Transaction)
			.Distinct()
			.ToArray();

		foreach (var transaction in transactions)
		{
			if (RestoreAnonymityPoints(transaction))
			{
				_eventBus.Publish(new AnonymityPointsChanged());
			}
		}

		// The points of a coin are gone once it is spent, so only those of the transactions above are kept.
		void Remember() =>
			KeyManager.SetAnonymityPoints(
				transactions
					.Where(transaction => transaction.AnonymityPoints is not null)
					.ToDictionary(transaction => transaction.GetHash(), transaction => transaction.AnonymityPoints!.Value));

		Remember();

		foreach (var transaction in transactions.Where(transaction => transaction.AnonymityPoints is null))
		{
			try
			{
				if (await _anonymityPointsCalculator.CalculateAsync(transaction, cancellationToken).ConfigureAwait(false) is { } points)
				{
					transaction.AnonymityPoints = points;
					Logger.LogDebug(FormatLog($"Transaction {transaction.GetHash()} is worth {points} anonymity points.", this));

					// The result is kept and shown at once: the node may take long for the next transaction, and the
					// application may be closed before it answered.
					Remember();
					_eventBus.Publish(new AnonymityPointsChanged());
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				if (WalletSynchronizer.IsNodeFailure(ex))
				{
					// The node cannot be reached or did not answer in time. Asking it about the next transaction would
					// end the same way, so everything that is left is tried again later.
					Logger.LogDebug(FormatLog($"The anonymity points of {transaction.GetHash()} could not be found out: {ex.Message}", this));
					break;
				}

				// This transaction is the trouble, not the node: the others are not held up by it.
				Logger.LogWarning(FormatLog($"The anonymity points of {transaction.GetHash()} could not be worked out: {ex}", this));
			}
		}

		return transactions.All(transaction => transaction.AnonymityPoints is not null);
	}

	/// <summary>Gives a transaction the anonymity points that the wallet found out for it when it ran before.</summary>
	/// <returns>Whether the transaction got its points by that.</returns>
	private bool RestoreAnonymityPoints(SmartTransaction transaction)
	{
		if (transaction.AnonymityPoints is null && KeyManager.GetAnonymityPoints(transaction.GetHash()) is { } points)
		{
			transaction.AnonymityPoints = points;
			return true;
		}

		return false;
	}

	public string AddCoinJoinPayment(IDestination destination, Money amount)
	{
		var paymentId = BatchedPayments.AddPayment(destination, amount);
		_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
		return paymentId.ToString();
	}

	public void CancelCoinJoinPayment(Guid paymentId)
	{
		BatchedPayments.AbortPayment(paymentId);
		_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
	}

	public void AddCoinjoinCosts(uint256 transactionId, CoinjoinCosts coinjoinCosts)
	{
		KeyManager.AddCoinjoinCosts(transactionId, coinjoinCosts);
		_eventBus.Publish(new CoinjoinCostsAdded(transactionId, coinjoinCosts));
	}

	/// <inheritdoc/>
	public override async Task StopAsync(CancellationToken cancel)
	{
		await base.StopAsync(cancel).ConfigureAwait(false);
		await Synchronizer.StopAsync(cancel).ConfigureAwait(false);
		Synchronizer.Dispose();

		_disposables.Dispose();
	}

	private void WalletRelevantTransactionProcessed(ProcessedResult e)
	{
		try
		{
			// What was found out before shows at once. What is not known is found out in the background.
			RestoreAnonymityPoints(e.Transaction);

			if (_coinsChanged.CurrentCount == 0)
			{
				_coinsChanged.Release();
			}
		}
		catch (SemaphoreFullException)
		{
			// Somebody else said so in the same moment.
		}
		catch (Exception ex)
		{
			Logger.LogError(FormatLog(ex.ToString(), this));
		}

		try
		{
			if (e.Transaction.CanBeSpeedUpUsingCpfp())
			{
				CpfpInfoProvider.ScheduleRequest(e.Transaction);
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(FormatLog(ex.ToString(), this));
		}

		try
		{
			if (BatchedPayments.TryResolvePaymentsWithTransaction(e.Transaction))
			{
				_eventBus.Publish(new PaymentBatchChanged(BatchedPayments.GetPayments()));
			}
		}
		catch (Exception ex)
		{
			Logger.LogError(FormatLog(ex.ToString(), this));
		}
	}

	private void Mempool_TransactionReceived(SmartTransaction tx)
	{
		try
		{
			if (!TransactionProcessor.IsAware(tx.GetHash()))
			{
				TransactionProcessor.Process(tx);
			}
		}
		catch (Exception ex)
		{
			Logger.LogWarning(FormatLog(ex.ToString(), this));
		}
	}

	/// <summary>
	/// Loads what the wallet knew about the chain when it ran the last time.
	/// </summary>
	private void LoadWalletState()
	{
		// Make sure that the keys are asserted in case of an empty HdPubKeys array.
		KeyManager.GetKeys();

		TransactionProcessor.Process(TransactionStore.ConfirmedStore.GetTransactions());
	}

	/// <summary>
	/// Loads the unconfirmed transactions that the wallet remembers. This is done once the wallet processed every block,
	/// which tells the transactions that got confirmed in the meantime from those that are still waiting.
	/// </summary>
	private void LoadDummyMempool()
	{
		if (TransactionStore.MempoolStore.IsEmpty())
		{
			return;
		}

		var txsToProcess = new List<SmartTransaction>();
		foreach (var tx in TransactionStore.MempoolStore.GetTransactions())
		{
			var txid = tx.GetHash();
			if (DateTimeOffset.UtcNow - tx.FirstSeen < TimeSpan.FromDays(ServiceConfiguration.DropUnconfirmedTransactionsAfterDays))
			{
				txsToProcess.Add(tx);
			}
			else
			{
				if (TransactionStore.MempoolStore.TryRemove(txid, out _))
				{
					Logger.LogInfo(FormatLog($"Transaction {txid} dropped after {ServiceConfiguration.DropUnconfirmedTransactionsAfterDays} days being unconfirmed.", this));
				}
			}
		}

		TransactionProcessor.Process(txsToProcess);
	}

	/// <summary>Chooses a coin of the wallet for coinjoins, or takes it out of them again.</summary>
	public void SelectCoinForCoinJoin(OutPoint outpoint, bool select = true)
	{
		if (!Coins.TryGetByOutPoint(outpoint, out _))
		{
			throw new InvalidOperationException($"Coin '{outpoint}' doesn't belong to the wallet or is spent.");
		}

		SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [outpoint] = select });
	}

	/// <summary>
	/// Chooses the given coins for coinjoins or takes them out of them again, all of them at once. The coins that are
	/// not named stay as they are, and so does a coin that the wallet does not have.
	/// </summary>
	/// <param name="selections">For every coin that is to change, whether it is chosen.</param>
	public void SelectCoinsForCoinJoin(IReadOnlyDictionary<OutPoint, bool> selections)
	{
		// The wallet file is the memory of the choices. While the wallet still reads the blocks of the node, the file
		// knows of choices whose coins the wallet does not have yet: the changes are made to what the file holds, so
		// that none of those is lost.
		var selected = KeyManager.CoinsSelectedForCoinJoin.ToHashSet();

		// A choice whose coin was spent in the meantime, in a round for example, is of no use any more.
		var allCoins = Coins.AsAllCoinsView();
		selected.RemoveWhere(outpoint => allCoins.TryGetByOutPoint(outpoint, out var coin) && coin.IsSpent());

		foreach (var coin in Coins)
		{
			if (selections.TryGetValue(coin.Outpoint, out var select))
			{
				coin.IsSelectedForCoinJoin = select;

				if (select)
				{
					selected.Add(coin.Outpoint);
				}
				else
				{
					selected.Remove(coin.Outpoint);
				}
			}
		}

		KeyManager.SetCoinsSelectedForCoinJoin(selected);
	}
}
