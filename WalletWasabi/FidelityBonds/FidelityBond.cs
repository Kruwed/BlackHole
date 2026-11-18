using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// An output that pays to a bond script of the wallet: coins that are locked until a date.
/// </summary>
/// <remarks>
/// A bond is not a coin of the wallet. It is not part of the balance and no transaction or coinjoin of the wallet spends
/// it, except the one that redeems it after its locktime.
/// </remarks>
/// <param name="Transaction">The transaction that created the bond.</param>
/// <param name="SpenderTransaction">The transaction that redeemed the bond, if there is one.</param>
public record FidelityBond(OutPoint OutPoint, Money Amount, BondLocktime Locktime, SmartTransaction Transaction, SmartTransaction? SpenderTransaction)
{
	public TxOut TxOut => Transaction.Transaction.Outputs[OutPoint.N];

	public Coin Coin => new(OutPoint, TxOut);

	public bool IsConfirmed => Transaction.Confirmed;

	public bool IsSpent => SpenderTransaction is not null;

	/// <summary>
	/// Tells if the timelock of the bond is over. The chain decides that by the median time of its last eleven blocks, which
	/// is about an hour behind the clock: a transaction that spends the bond is valid once that time is past the locktime.
	/// </summary>
	public bool IsUnlocked(DateTimeOffset medianTimePast) => Locktime.Date < medianTimePast;

	/// <summary>Tells if the bond can be redeemed now.</summary>
	public bool IsRedeemable(DateTimeOffset medianTimePast) => IsConfirmed && !IsSpent && IsUnlocked(medianTimePast);
}
