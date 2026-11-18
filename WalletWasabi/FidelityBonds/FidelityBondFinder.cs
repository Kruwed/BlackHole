using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// Finds the bonds of a wallet in the transactions that it knows.
/// </summary>
/// <remarks>
/// The bonds are not kept anywhere. They are read from the transactions every time, so what happens to a transaction, a
/// replacement or a reorganization of the chain for example, cannot leave a wrong bond behind.
/// </remarks>
public static class FidelityBondFinder
{
	/// <summary>Tells if the transaction creates a bond of the wallet or spends an output that looks like one.</summary>
	/// <remarks>Cheap enough to be asked about every transaction of the chain.</remarks>
	public static bool Concerns(Transaction transaction, FidelityBondKeys keys)
	{
		foreach (var output in transaction.Outputs)
		{
			if (keys.TryGetLocktime(output.ScriptPubKey, out _))
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

	/// <summary>
	/// Tells if the input spends a bond of the wallet. The input shows that itself: next to the signature it reveals the
	/// script of the bond.
	/// </summary>
	private static bool SpendsBond(TxIn input, FidelityBondKeys keys)
	{
		var witness = input.WitScript;

		if (witness is null || witness.PushCount != 2)
		{
			return false;
		}

		// A locktime of four or five bytes, two opcodes, the key and one more opcode, with a length byte for each push.
		var script = witness.GetUnsafePush(1);
		return script.Length is 42 or 43 && keys.TryGetLocktime(BondScript.GetScriptPubKey(new Script(script)), out _);
	}

	/// <summary>The bonds that the transactions create, the spent ones included, in the order of the transactions.</summary>
	public static IReadOnlyList<FidelityBond> Find(IEnumerable<SmartTransaction> transactions, FidelityBondKeys keys)
	{
		var all = transactions as IReadOnlyCollection<SmartTransaction> ?? transactions.ToList();
		var found = new List<(OutPoint OutPoint, TxOut TxOut, BondLocktime Locktime, SmartTransaction Transaction)>();

		foreach (var tx in all)
		{
			var outputs = tx.Transaction.Outputs;
			for (var i = 0; i < outputs.Count; i++)
			{
				if (keys.TryGetLocktime(outputs[i].ScriptPubKey, out var locktime))
				{
					found.Add((new OutPoint(tx.GetHash(), i), outputs[i], locktime.Value, tx));
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
			.Select(x => new FidelityBond(x.OutPoint, x.TxOut.Value, x.Locktime, x.Transaction, spenders.GetValueOrDefault(x.OutPoint)))
			.ToList();
	}
}
