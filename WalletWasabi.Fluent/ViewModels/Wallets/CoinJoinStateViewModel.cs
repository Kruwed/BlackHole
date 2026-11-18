using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.State;
using WalletWasabi.Fluent.ViewModels.Wallets.Settings;
using WalletWasabi.Services;
using WalletWasabi.WabiSabi.Client.CoinJoinProgressEvents;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>
/// The controls of the coinjoin and what they say about it. A coinjoin is one round: Play starts it, and when the round is
/// over, however it ended, the coinjoin is stopped again. The outcome stays on display until the next round is started.
/// </summary>
[AppLifetime]
public partial class CoinJoinStateViewModel : ViewModelBase
{
	private const string WaitingMessage = "Awaiting coinjoin";
	private const string CoinjoinMiningFeeRateTooHighMessage = "Mining fee rate was too high";
	private const string MinInputCountTooLowMessage = "Min input count was too low";
	private const string StoppedMessage = "Coinjoin is stopped";
	private const string PressPlayToStartMessage = "Press Play to start a round";
	private const string RoundSucceedMessage = "Coinjoin successful";
	private const string RoundFinishedMessage = "Round ended without a coinjoin";
	private const string AbortedNotEnoughAlicesMessage = "Insufficient participants";
	private const string CoinJoinInProgress = "Coinjoin in progress";
	private const string InputRegistrationMessage = "Awaiting other participants";
	private const string WaitingForBlameRoundMessage = "Awaiting the blame round";
	private const string WaitingRoundMessage = "Awaiting a round";
	private const string NoRoundMessage = "No round was found";
	private const string NoCoinsEligibleToMixMessage = "Insufficient funds eligible for coinjoin";
	private const string UserInSendWorkflowMessage = "The send dialog is open";
	private const string GeneralErrorMessage = "Coinjoin did not take place";
	private const string NoConfirmedCoinsMessage = "The funds are not confirmed yet";
	private const string CoinsRejectedMessage = "Some funds are rejected from coinjoining";
	private const string OnlyImmatureCoinsAvailableMessage = "Only immature funds are available";
	private const string OnlyExcludedCoinsAvailableMessage = "Only excluded funds are available";
	private const string CoordinatorLiedMessage = "Coordinator lied and might be malicious!";

	private readonly IWalletModel _wallet;
	private readonly Wallet _walletInstance;
	private readonly StateMachine<State, Trigger> _stateMachine;
	private readonly DispatcherTimer _countdownTimer;

	[AutoNotify] private bool _playVisible;
	[AutoNotify] private bool _isStopping;
	[AutoNotify] private bool _stopVisible;
	[AutoNotify] private string _currentStatus = "";
	[AutoNotify] private bool _isProgressReversed;
	[AutoNotify] private double _progressValue;
	[AutoNotify] private string _leftText = "";
	[AutoNotify] private string _rightText = "";
	[AutoNotify] private bool _isInCriticalPhase;
	[AutoNotify] private bool _isCountDownDelayHappening;
	[AutoNotify] private bool _isCoinjoinSupported;

	private DateTimeOffset _countDownStartTime;
	private DateTimeOffset _countDownEndTime;

	/// <summary>
	/// How the last coinjoin ended, unless it was stopped: what its round came to, or why there was no round. It is shown
	/// while the coinjoin is stopped, and forgotten when the next one starts.
	/// </summary>
	private string? _outcome;

	public CoinJoinStateViewModel(UiContext uiContext, IWalletModel wallet, Wallet walletInstance, WalletCoinjoinModel walletCoinjoinModel) : base(uiContext)
	{
		_wallet = wallet;
		_walletInstance = walletInstance;

		walletCoinjoinModel.StatusUpdated
					   .Do(ProcessStatusChange)
					   .Subscribe();

		// Nothing starts a coinjoin but the user.
		_stateMachine = new StateMachine<State, Trigger>(State.Stopped);

		ConfigureStateMachine();

		PlayCommand = ReactiveCommand.CreateFromTask(walletCoinjoinModel.StartAsync);

		// A coinjoin cannot be stopped in its critical phase, and not twice.
		var stopCommandCanExecute =
			this.WhenAnyValue(
				x => x.IsInCriticalPhase,
				x => x.IsStopping,
				(isInCriticalPhase, isStopping) => !isInCriticalPhase && !isStopping);

		StopCommand = ReactiveCommand.CreateFromTask(walletCoinjoinModel.StopAsync, stopCommandCanExecute);

		_countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_countdownTimer.Tick += (_, _) => _stateMachine.Fire(Trigger.Tick);

		_stateMachine.Start();

		CoinJoinPaymentsCommand = ReactiveCommand.Create(() => UiContext.Navigate(NavigationTarget.DialogScreen).To().CoinJoinPayments(_wallet, _walletInstance));

		IsCoinjoinSupported = _wallet.Coinjoin is not null;
	}

	private enum State
	{
		Invalid = 0,
		Stopped,
		Playing,
	}

	private enum Trigger
	{
		Invalid = 0,
		StartError,
		Tick,
		WalletStartedCoinJoin,
		WalletStoppedCoinJoin
	}

	private bool IsCountDownFinished => GetRemainingTime() <= TimeSpan.Zero;

	private bool IsCounting => _countdownTimer.IsEnabled;

	public ICommand PlayCommand { get; }

	public ICommand StopCommand { get; }

	public ICommand CoinJoinPaymentsCommand { get; }

	/// <summary>What the controls say about a round that came to its end and was not stopped.</summary>
	public static string DescribeRoundEnd(EndRoundState endRoundState) =>
		endRoundState switch
		{
			EndRoundState.TransactionBroadcasted => RoundSucceedMessage,
			EndRoundState.AbortedNotEnoughAlices => AbortedNotEnoughAlicesMessage,
			_ => RoundFinishedMessage
		};

	/// <summary>What the controls say about a coinjoin that did not take place.</summary>
	public static string DescribeError(CoinjoinError error) =>
		error switch
		{
			CoinjoinError.NoCoinsEligibleToMix => NoCoinsEligibleToMixMessage,
			CoinjoinError.NoConfirmedCoinsEligibleToMix => NoConfirmedCoinsMessage,
			CoinjoinError.UserInSendWorkflow => UserInSendWorkflowMessage,
			CoinjoinError.UserWasntInRound => RoundFinishedMessage,
			CoinjoinError.CoinsRejected => CoinsRejectedMessage,
			CoinjoinError.OnlyImmatureCoinsAvailable => OnlyImmatureCoinsAvailableMessage,
			CoinjoinError.OnlyExcludedCoinsAvailable => OnlyExcludedCoinsAvailableMessage,
			CoinjoinError.MiningFeeRateTooHigh => CoinjoinMiningFeeRateTooHighMessage,
			CoinjoinError.MinInputCountTooLow => MinInputCountTooLowMessage,
			CoinjoinError.CoordinatorLiedAboutInputs => CoordinatorLiedMessage,
			CoinjoinError.NoRoundAvailable => NoRoundMessage,
			_ => GeneralErrorMessage
		};

	private void ConfigureStateMachine()
	{
		_stateMachine.Configure(State.Stopped)
			.Permit(Trigger.WalletStartedCoinJoin, State.Playing)
			.OnEntry(() =>
			{
				StopCountDown();
				IsStopping = false;
				StopVisible = false;
				PlayVisible = true;

				RefreshStopped();
			})
			.OnTrigger(Trigger.StartError, RefreshStopped)
			.OnExit(() => LeftText = "");

		_stateMachine.Configure(State.Playing)
			.Permit(Trigger.WalletStoppedCoinJoin, State.Stopped)
			.OnEntry(() =>
			{
				PlayVisible = false;
				StopVisible = true;

				CurrentStatus = WaitingMessage;
			})
			.OnTrigger(Trigger.Tick, UpdateCountDown);
	}

	/// <summary>
	/// What the controls say while no coinjoin is under way: how the last one ended, if it was not stopped, and that Play
	/// starts a round.
	/// </summary>
	private void RefreshStopped()
	{
		CurrentStatus = _outcome ?? StoppedMessage;
		LeftText = PressPlayToStartMessage;
	}

	private void UpdateCountDown()
	{
		IsCountDownDelayHappening = IsCounting && IsCountDownFinished;

		// This case mostly happens when there is some delay between the client and the server,
		// and the countdown has finished but the client hasn't received any new phase changed message.
		if (IsCountDownDelayHappening)
		{
			LeftText = "Waiting for response";
			RightText = "";
			return;
		}

		var format = @"hh\:mm\:ss";
		LeftText = $"{GetElapsedTime().ToString(format)}";
		RightText = $"-{GetRemainingTime().ToString(format)}";
		ProgressValue = GetPercentage();
	}

	private TimeSpan GetElapsedTime() => DateTimeOffset.UtcNow - _countDownStartTime;

	private TimeSpan GetRemainingTime() => _countDownEndTime - DateTimeOffset.UtcNow;

	private TimeSpan GetTotalTime() => _countDownEndTime - _countDownStartTime;

	private double GetPercentage() => GetElapsedTime().TotalSeconds / GetTotalTime().TotalSeconds * 100;

	private void ProcessStatusChange(StatusChangedEventArgs e)
	{
		switch (e)
		{
			case WalletStartedCoinJoinEventArgs:
				_outcome = null;
				_stateMachine.Fire(Trigger.WalletStartedCoinJoin);
				break;

			case WalletStoppedCoinJoinEventArgs:
				_stateMachine.Fire(Trigger.WalletStoppedCoinJoin);
				break;

			case StartErrorEventArgs start:
				// The coinjoin is over. Its end is told right after this, and by then the controls know why.
				_outcome = DescribeError(start.Error);
				_stateMachine.Fire(Trigger.StartError);
				break;

			case CoinJoinStatusEventArgs coinJoinStatusEventArgs:
				OnCoinJoinPhaseChanged(coinJoinStatusEventArgs.CoinJoinProgressEventArgs);
				break;
		}
	}

	private void OnCoinJoinPhaseChanged(CoinJoinProgressEventArgs coinJoinProgress)
	{
		switch (coinJoinProgress)
		{
			case RoundEnded roundEnded:
				if (roundEnded.IsStopped)
				{
					IsStopping = true;
				}
				else
				{
					// A disrupted round is followed by its blame round, and then this is not the last word.
					_outcome = DescribeRoundEnd(roundEnded.LastRoundState.EndRoundState);
					CurrentStatus = _outcome;
					StopCountDown();
				}
				break;

			case EnteringOutputRegistrationPhase outputRegPhase:
				_countDownEndTime = outputRegPhase.TimeoutAt + outputRegPhase.RoundState.CoinjoinState.Parameters.TransactionSigningTimeout;
				break;

			case EnteringSigningPhase signingPhase:
				_countDownEndTime = signingPhase.TimeoutAt;
				break;

			case EnteringInputRegistrationPhase inputRegPhase:
				StartCountDown(
					message: InputRegistrationMessage,
					start: inputRegPhase.TimeoutAt - inputRegPhase.RoundState.InputRegistrationTimeout,
					end: inputRegPhase.TimeoutAt);
				break;

			case WaitingForBlameRound waitingForBlameRound:
				StartCountDown(message: WaitingForBlameRoundMessage, start: DateTimeOffset.UtcNow, end: waitingForBlameRound.TimeoutAt);
				break;

			case WaitingForRound:
				CurrentStatus = WaitingRoundMessage;
				StopCountDown();
				break;

			case EnteringConnectionConfirmationPhase confirmationPhase:

				var startTime = confirmationPhase.TimeoutAt - confirmationPhase.RoundState.CoinjoinState.Parameters.ConnectionConfirmationTimeout;
				var totalEndTime = confirmationPhase.TimeoutAt +
								   confirmationPhase.RoundState.CoinjoinState.Parameters.OutputRegistrationTimeout +
								   confirmationPhase.RoundState.CoinjoinState.Parameters.TransactionSigningTimeout;

				StartCountDown(
					message: CoinJoinInProgress,
					start: startTime,
					end: totalEndTime);

				break;

			case EnteringCriticalPhase:
				IsInCriticalPhase = true;
				break;

			case LeavingCriticalPhase:
				IsInCriticalPhase = false;
				break;
		}
	}

	private void StartCountDown(string message, DateTimeOffset start, DateTimeOffset end)
	{
		CurrentStatus = message;
		_countDownStartTime = start;
		_countDownEndTime = end;
		UpdateCountDown(); // force the UI to apply the changes at the same time.
		_countdownTimer.Start();
	}

	private void StopCountDown()
	{
		_countdownTimer.Stop();
		IsCountDownDelayHappening = false;
		_countDownStartTime = DateTimeOffset.MinValue;
		_countDownEndTime = DateTimeOffset.MinValue;
		LeftText = "";
		RightText = "";
		ProgressValue = 0;
	}
}
