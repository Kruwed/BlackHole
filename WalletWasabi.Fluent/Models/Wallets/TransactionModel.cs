using NBitcoin;
using ReactiveUI;

namespace WalletWasabi.Fluent.Models.Wallets;

public abstract partial class TransactionModel : ReactiveObject
{
	public required int OrderIndex { get; init; }

	public required uint256 Id { get; init; }

	public required DateTimeOffset Date { get; init; }

	public required string DateString { get; set; }

	public required string DateToolTipString { get; init; }

	public required string ConfirmedTooltip { get; init; }

	public abstract TransactionType Type { get; }

	public required TransactionStatus Status { get; init; }

	public bool IsChild { get; set; }

	/// <summary>How the transaction changed what the wallet owns, its coins and the coins in its fidelity bonds together.</summary>
	public required Money Amount { get; init; }

	public Amount AmountAmount => new (Amount);

	/// <summary>The coins that the transaction puts into fidelity bonds of the wallet.</summary>
	public Money LockedInBonds { get; init; } = Money.Zero;

	/// <summary>What the fidelity bonds of the wallet held that the transaction redeems.</summary>
	public Money UnlockedFromBonds { get; init; } = Money.Zero;

	public bool IsConfirmed => Status == TransactionStatus.Confirmed;

	public override string ToString()
	{
		return $"{Type} {Status} {DateString} {Amount}";
	}
}
