using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// An output that pays to a bond script of the wallet: coins that are locked until the chain reaches a block height.
/// </summary>
/// <remarks>
/// The coins of a bond belong to the wallet and count for its balance, but a bond is not one of its coins: no payment
/// and no coinjoin spends it. Only the transaction that redeems it does, once the chain has reached the unlock height.
/// </remarks>
/// <param name="UnlockHeight">The height that the chain must have reached before the bond can be spent.</param>
/// <param name="Transaction">The transaction that created the bond.</param>
/// <param name="SpenderTransaction">The transaction that redeemed the bond, if there is one.</param>
public record FidelityBond(OutPoint OutPoint, Money Amount, uint UnlockHeight, SmartTransaction Transaction, SmartTransaction? SpenderTransaction)
{
	public TxOut TxOut => Transaction.Transaction.Outputs[OutPoint.N];

	public Coin Coin => new(OutPoint, TxOut);

	public bool IsConfirmed => Transaction.Confirmed;

	public bool IsSpent => SpenderTransaction is not null;

	/// <summary>
	/// Tells if the timelock of the bond is over: the chain has reached the unlock height. The transaction that spends the
	/// bond is valid in the next block then, so the node takes it.
	/// </summary>
	/// <param name="chainHeight">The height of the last block of the chain.</param>
	public bool IsUnlocked(uint chainHeight) => chainHeight >= UnlockHeight;

	/// <summary>Tells if the bond can be redeemed now.</summary>
	public bool IsRedeemable(uint chainHeight) => IsConfirmed && !IsSpent && IsUnlocked(chainHeight);
}
