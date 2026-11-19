namespace WalletWasabi.WabiSabi.Client.StatusChangedEvents;

public enum StopReason
{
	WalletUnloaded
}

public enum CompletionStatus
{
	Success,
	Canceled,
	Failed,
	Unknown
}

public enum CoinjoinError
{
	NoCoinsEligibleToMix,
	UserInSendWorkflow,
	UserWasntInRound,
	NoConfirmedCoinsEligibleToMix,
	CoinsRejected,
	OnlyImmatureCoinsAvailable,

	/// <summary>The wallet has coins, but the user chose none of them for coinjoins.</summary>
	NoCoinsSelected,

	MiningFeeRateTooHigh,
	MinInputCountTooLow,
	CoordinatorLiedAboutInputs,

	/// <summary>The coordinator had no round to register to for as long as the client waits for one.</summary>
	NoRoundAvailable
}

public class StatusChangedEventArgs : EventArgs
{
	public StatusChangedEventArgs(Wallet wallet)
	{
		Wallet = wallet;
	}

	public Wallet Wallet { get; }
}
