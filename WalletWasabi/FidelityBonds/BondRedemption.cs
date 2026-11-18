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

	/// <summary>The signature in the transaction that the fee is calculated with: as large as a signature can get.</summary>
	private static readonly byte[] LargestSignature = new byte[73];

	/// <summary>
	/// Builds and signs the transaction that pays the whole bond, less the mining fee, to the destination.
	/// </summary>
	/// <param name="key">The private key of the bond.</param>
	/// <param name="medianTimePast">The median time of the last eleven blocks of the chain. It decides whether the timelock is over.</param>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	public static Transaction Create(FidelityBond bond, Key key, Script destination, FeeRate feeRate, DateTimeOffset medianTimePast, Network network)
	{
		if (bond.IsSpent)
		{
			throw new InvalidOperationException("The fidelity bond was redeemed already.");
		}

		if (!bond.IsConfirmed)
		{
			throw new InvalidOperationException("The fidelity bond is not confirmed yet.");
		}

		if (!bond.IsUnlocked(medianTimePast))
		{
			throw new InvalidOperationException($"The fidelity bond is locked until {bond.Locktime}.");
		}

		var bondScript = BondScript.Create(key.PubKey, bond.Locktime);

		if (BondScript.GetScriptPubKey(bondScript) != bond.TxOut.ScriptPubKey)
		{
			throw new ArgumentException("This is not the key of the fidelity bond.", nameof(key));
		}

		var transaction = network.CreateTransaction();
		transaction.Version = 2;

		// The script demands a locktime that is not before its own. With it the chain accepts the transaction only once
		// its median time is past the locktime.
		transaction.LockTime = bond.Locktime.LockTime;
		transaction.Inputs.Add(new TxIn(bond.OutPoint) { Sequence = InputSequence });
		transaction.Outputs.Add(new TxOut(bond.Amount, destination));

		transaction.Inputs[0].WitScript = new WitScript(Op.GetPushOp(LargestSignature), Op.GetPushOp(bondScript.ToBytes()));
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

		var hash = transaction.GetSignatureHash(bondScript, 0, SigHash.All, bond.TxOut, HashVersion.WitnessV0);
		var signature = new TransactionSignature(key.Sign(hash), SigHash.All);
		transaction.Inputs[0].WitScript = new WitScript(Op.GetPushOp(signature.ToBytes()), Op.GetPushOp(bondScript.ToBytes()));

		if (!transaction.Inputs.AsIndexedInputs().Single().VerifyScript(bond.Coin, ScriptVerify.Standard, out var error))
		{
			throw new InvalidOperationException($"The transaction that redeems the fidelity bond does not satisfy the script of the bond: {error}.");
		}

		return transaction;
	}
}
