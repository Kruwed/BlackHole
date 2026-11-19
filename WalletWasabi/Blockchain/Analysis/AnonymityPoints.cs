namespace WalletWasabi.Blockchain.Analysis;

/// <summary>
/// The rules of the anonymity points.
/// <para>
/// An output of a transaction is matched if another output of that transaction has the same amount. A transaction is
/// worth as many points as it has matched outputs, but not more than it has inputs that were matched outputs of the
/// transactions that created them: the lower of the two numbers counts.
/// </para>
/// <para>
/// A coin has the points of the transaction that created it if it is a matched output of that transaction, and none
/// otherwise. It has them from its creation until it is spent. Nothing is carried over from a transaction to the next
/// one: the points of a coin never depend on the points of the coins that paid for it.
/// </para>
/// </summary>
public static class AnonymityPoints
{
	/// <summary>Whether another output of the transaction has the same amount as the output at the given index.</summary>
	public static bool IsMatchedOutput(Transaction transaction, uint index) =>
		index < transaction.Outputs.Count && IsMatched(transaction.Outputs.Select(output => output.Value.Satoshi).ToArray(), index);

	/// <summary>Whether another one of the amounts is the same as the one at the given index.</summary>
	public static bool IsMatched(IReadOnlyList<long> outputAmounts, uint index)
	{
		var amount = outputAmounts[(int)index];

		for (var i = 0; i < outputAmounts.Count; i++)
		{
			if (i != index && outputAmounts[i] == amount)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The number of outputs of the transaction that are matched.</summary>
	public static int CountMatchedOutputs(Transaction transaction) =>
		transaction.Outputs
			.GroupBy(output => output.Value.Satoshi)
			.Where(sameAmount => sameAmount.Count() > 1)
			.Sum(sameAmount => sameAmount.Count());

	/// <summary>The points of a transaction.</summary>
	/// <param name="matchedInputs">How many of its inputs were matched outputs of the transactions that created them.</param>
	/// <param name="matchedOutputs">How many of its outputs are matched.</param>
	public static int Calculate(int matchedInputs, int matchedOutputs) =>
		Math.Min(matchedInputs, matchedOutputs);
}
