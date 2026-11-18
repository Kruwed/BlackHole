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
	AutoConjoinDisabled,
	UserInSendWorkflow,
	AllCoinsPrivate,
	UserWasntInRound,
	NoConfirmedCoinsEligibleToMix,
	CoinsRejected,
	OnlyImmatureCoinsAvailable,
	OnlyExcludedCoinsAvailable,
	MiningFeeRateTooHigh,
	MinInputCountTooLow,
	CoordinatorLiedAboutInputs
}

public class StatusChangedEventArgs : EventArgs
{
	public StatusChangedEventArgs(Wallet wallet)
	{
		Wallet = wallet;
	}

	public Wallet Wallet { get; }
}
