namespace WalletWasabi.Fluent.Models.Wallets;

public enum TransactionType
{
	Unknown,
	IncomingTransaction,
	OutgoingTransaction,
	SelfTransferTransaction,
	Coinjoin,
	CoinjoinGroup,
	Cancellation,
	CPFP,

	/// <summary>The transaction puts coins into a fidelity bond of the wallet.</summary>
	Lock,

	/// <summary>The transaction takes the coins of a fidelity bond of the wallet out again.</summary>
	Unlock
}
