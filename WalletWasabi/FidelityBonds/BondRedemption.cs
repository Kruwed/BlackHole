namespace WalletWasabi.FidelityBonds;

/// <summary>
/// Builds the transaction that takes the coins of a bond back out of its timelock.
/// </summary>
public static class BondRedemption
{
	/// <summary>
	/// The sequence number of the input. The highest one would switch the locktime of the transaction off, and the script
	/// of the bond does not accept that.
	/// </summary>
	private static readonly Sequence InputSequence = new(0xFFFFFFFE);

	/// <summary>The signature in the transaction that the fee is calculated with. A signature of a Taproot script has 64 bytes.</summary>
	private static readonly byte[] SignaturePlaceholder = new byte[64];

	/// <summary>
	/// Builds and signs the transaction that pays the whole bond, less the mining fee, to the destination.
	/// </summary>
	/// <param name="key">The private key of the bond.</param>
	/// <param name="chainHeight">The height of the last block of the chain. It decides whether the timelock is over.</param>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	public static Transaction Create(FidelityBond bond, Key key, Script destination, FeeRate feeRate, uint chainHeight, Network network)
	{
		if (bond.IsSpent)
		{
			throw new InvalidOperationException("The fidelity bond was redeemed already.");
		}

		if (!bond.IsConfirmed)
		{
			throw new InvalidOperationException("The fidelity bond is not confirmed yet.");
		}

		if (!bond.IsUnlocked(chainHeight))
		{
			throw new InvalidOperationException($"The fidelity bond is locked until block {bond.UnlockHeight}.");
		}

		var leaf = BondScript.CreateLeaf(key.PubKey, bond.UnlockHeight);

		if (BondScript.GetScriptPubKey(leaf) != bond.TxOut.ScriptPubKey)
		{
			throw new ArgumentException("This is not the key of the fidelity bond.", nameof(key));
		}

		var tapScript = BondScript.ToTapScript(leaf);
		var controlBlock = BondScript.GetSpendInfo(leaf).GetControlBlock(tapScript);

		var transaction = network.CreateTransaction();
		transaction.Version = 2;

		// The script demands a locktime that is not below its own. With it the transaction is valid in the blocks above
		// the unlock height, and the node takes it once the chain has reached that height.
		transaction.LockTime = new LockTime(bond.UnlockHeight);
		transaction.Inputs.Add(new TxIn(bond.OutPoint) { Sequence = InputSequence });
		transaction.Outputs.Add(new TxOut(bond.Amount, destination));

		transaction.Inputs[0].WitScript = CreateWitness(SignaturePlaceholder, leaf, controlBlock);
		var fee = feeRate.GetFee(transaction.GetVirtualSize());
		var output = transaction.Outputs[0];
		output.Value = bond.Amount - fee;

		if (output.Value <= Money.Zero || output.IsDust())
		{
			throw new InvalidOperationException($"The fidelity bond holds {bond.Amount} BTC, which is too little to pay the mining fee of {fee} BTC for its redemption.");
		}

		// The same guard that a payment has: a fee that takes more than it leaves is a slip of the hand.
		if (fee > output.Value)
		{
			throw new InvalidOperationException($"The mining fee of {fee} BTC is more than the {output.Value} BTC that it leaves of the fidelity bond.");
		}

		TxOut[] spentOutputs = [bond.TxOut];
		var executionData = new TaprootExecutionData(0, tapScript.LeafHash) { SigHash = TaprootSigHash.Default };
		var hash = transaction.GetSignatureHashTaproot(spentOutputs, executionData);
		var signature = key.SignTaprootScriptSpend(hash, TaprootSigHash.Default);
		transaction.Inputs[0].WitScript = CreateWitness(signature.ToBytes(), leaf, controlBlock);

		if (!transaction.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, transaction.PrecomputeTransactionData(spentOutputs), out var error))
		{
			throw new InvalidOperationException($"The transaction that redeems the fidelity bond does not satisfy the script of the bond: {error}.");
		}

		return transaction;
	}

	/// <summary>What spends a Taproot output by a script: the signature that the script asks for, the script, and the proof that the output commits to it.</summary>
	private static WitScript CreateWitness(byte[] signature, Script leaf, ControlBlock controlBlock) =>
		new(Op.GetPushOp(signature), Op.GetPushOp(leaf.ToBytes()), Op.GetPushOp(controlBlock.ToBytes()));
}
