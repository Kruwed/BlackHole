using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// Finds the bonds of a wallet in the transactions that it knows.
/// </summary>
/// <remarks>
/// The bonds are not kept anywhere. They are read from the transactions, so what happens to a transaction, a
/// replacement or a reorganization of the chain for example, cannot leave a wrong bond behind.
/// </remarks>
public static class FidelityBondFinder
{
	/// <summary>Tells if the transaction creates a bond of the wallet or spends one.</summary>
	/// <remarks>Cheap enough to be asked about every transaction of the chain.</remarks>
	public static bool Concerns(Transaction transaction, FidelityBondKeys keys)
	{
		foreach (var output in transaction.Outputs)
		{
			if (keys.TryGetUnlockHeight(output.ScriptPubKey, out _))
			{
				return true;
			}
		}

		if (!transaction.IsCoinBase)
		{
			foreach (var input in transaction.Inputs)
			{
				if (SpendsBond(input, keys))
				{
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>The coins that the transaction puts into bonds of the wallet.</summary>
	public static Money GetLockedAmount(Transaction transaction, FidelityBondKeys keys) =>
		transaction.Outputs.Where(output => keys.TryGetUnlockHeight(output.ScriptPubKey, out _)).Sum(output => output.Value);

	/// <summary>
	/// Tells if the input spends a bond of the wallet. The input shows that itself: next to the signature it reveals the
	/// script of the bond, with the unlock height and the key in it.
	/// </summary>
	public static bool SpendsBond(TxIn input, FidelityBondKeys keys)
	{
		var witness = input.WitScript;

		if (witness is null || witness.PushCount < 3)
		{
			return false;
		}

		// A Taproot input may end with an annex, which starts with this byte.
		var count = witness.PushCount;
		if (witness.GetUnsafePush(count - 1) is [0x50, ..])
		{
			count--;
		}

		// The signature, the script, and the proof of the script: nothing but the internal key, as the bond has one script only.
		if (count != 3
			|| witness.GetUnsafePush(2) is not { Length: 33 } controlBlock
			|| !controlBlock.AsSpan(1).SequenceEqual(InternalKey))
		{
			return false;
		}

		return BondScript.TryParseLeaf(new Script(witness.GetUnsafePush(1)), out var unlockHeight, out var key)
			&& keys.GetPubKey(unlockHeight).TaprootInternalKey.ToBytes().AsSpan().SequenceEqual(key);
	}

	private static readonly byte[] InternalKey = BondScript.UnspendableKey.ToBytes();

	/// <summary>The bonds that the transactions create, the spent ones included, in the order of the transactions.</summary>
	public static IReadOnlyList<FidelityBond> Find(IEnumerable<SmartTransaction> transactions, FidelityBondKeys keys)
	{
		// Without a watched address there is nothing to find, and most wallets have none.
		if (keys.KnownHeights.Count == 0 && !keys.IsSearching)
		{
			return [];
		}

		var all = transactions as IReadOnlyCollection<SmartTransaction> ?? transactions.ToList();
		var found = new List<(OutPoint OutPoint, TxOut TxOut, uint UnlockHeight, SmartTransaction Transaction)>();

		foreach (var tx in all)
		{
			var outputs = tx.Transaction.Outputs;
			for (var i = 0; i < outputs.Count; i++)
			{
				if (keys.TryGetUnlockHeight(outputs[i].ScriptPubKey, out var unlockHeight))
				{
					found.Add((new OutPoint(tx.GetHash(), i), outputs[i], unlockHeight, tx));
				}
			}
		}

		if (found.Count == 0)
		{
			return [];
		}

		var spenders = new Dictionary<OutPoint, SmartTransaction>();
		var outPoints = found.Select(x => x.OutPoint).ToHashSet();

		foreach (var tx in all)
		{
			foreach (var input in tx.Transaction.Inputs)
			{
				// A confirmed spender counts before an unconfirmed one: the transactions come in the order of the chain.
				if (outPoints.Contains(input.PrevOut))
				{
					spenders.TryAdd(input.PrevOut, tx);
				}
			}
		}

		return found
			.Select(x => new FidelityBond(x.OutPoint, x.TxOut.Value, x.UnlockHeight, x.Transaction, spenders.GetValueOrDefault(x.OutPoint)))
			.ToList();
	}
}
