namespace WalletWasabi.Blockchain.Analysis;

/// <summary>
/// The rules of the anonymity points.
/// <para>
/// An output of a transaction is matched if another output of that transaction has the same amount. What it is worth
/// depends on how many outputs have that amount: a point if they are three or more, half a point if they are a pair,
/// and nothing if the output is alone with its amount.
/// </para>
/// <para>
/// A transaction is worth what its outputs are worth together, but not more than its inputs: an input counts what it
/// was worth as an output of the transaction that created it. The lower of the two sums counts. A pair is worth one
/// point in all, so the outputs of a transaction are always worth a whole number, while its inputs, and with them the
/// transaction, can be worth half a point more than a whole number.
/// </para>
/// <para>
/// A coin has the points of the transaction that created it if it is a matched output of that transaction, and none
/// otherwise. It has them from its creation until it is spent. Nothing is carried over from a transaction to the next
/// one: the points of a coin never depend on the points of the coins that paid for it.
/// </para>
/// </summary>
public static class AnonymityPoints
{
	/// <summary>What an output is worth that shares its amount with two or more other outputs of its transaction.</summary>
	public const decimal WorthInSetOfThree = 1m;

	/// <summary>What an output is worth that shares its amount with exactly one other output of its transaction.</summary>
	public const decimal WorthInPair = 0.5m;

	/// <summary>What an output is worth, in half points, by the number of outputs of its transaction that have its amount.</summary>
	/// <remarks>Half points are whole numbers, which is what the sums of a calculation are kept in.</remarks>
	public static int GetHalfPoints(int outputsWithTheAmount) =>
		outputsWithTheAmount switch
		{
			>= 3 => 2,
			2 => 1,
			_ => 0
		};

	/// <summary>What an output is worth by the number of outputs of its transaction that have its amount.</summary>
	public static decimal GetWorth(int outputsWithTheAmount) => FromHalfPoints(GetHalfPoints(outputsWithTheAmount));

	/// <summary>A number of half points as points: a whole number, or half a point more than one.</summary>
	public static decimal FromHalfPoints(int halfPoints) =>
		halfPoints % 2 == 0 ? halfPoints / 2 : halfPoints / 2 + 0.5m;

	/// <summary>Whether another output of the transaction has the same amount as the output at the given index.</summary>
	public static bool IsMatchedOutput(Transaction transaction, uint index) =>
		index < transaction.Outputs.Count && IsMatched(GetAmounts(transaction), index);

	/// <summary>Whether another one of the amounts is the same as the one at the given index.</summary>
	public static bool IsMatched(IReadOnlyList<long> outputAmounts, uint index) =>
		CountOutputsWithTheAmount(outputAmounts, index) > 1;

	/// <summary>What the output at the given index is worth, in half points. An output that the transaction does not have is worth nothing.</summary>
	public static int GetHalfPointsOfOutput(IReadOnlyList<long> outputAmounts, uint index) =>
		index < outputAmounts.Count ? GetHalfPoints(CountOutputsWithTheAmount(outputAmounts, index)) : 0;

	/// <summary>What the output at the given index is worth as an output of the transaction, and so as an input of the transaction that spends it.</summary>
	public static decimal GetWorthOfOutput(Transaction transaction, uint index) =>
		FromHalfPoints(GetHalfPointsOfOutput(GetAmounts(transaction), index));

	/// <summary>What every output of a transaction is worth, in half points, by the amounts of its outputs.</summary>
	public static int[] GetHalfPointsOfOutputs(IReadOnlyList<long> outputAmounts)
	{
		var counts = new Dictionary<long, int>();

		foreach (var amount in outputAmounts)
		{
			counts[amount] = counts.GetValueOrDefault(amount) + 1;
		}

		var halfPoints = new int[outputAmounts.Count];

		for (var i = 0; i < halfPoints.Length; i++)
		{
			halfPoints[i] = GetHalfPoints(counts[outputAmounts[i]]);
		}

		return halfPoints;
	}

	/// <summary>What the outputs of a transaction are worth together, in half points. It is always an even number.</summary>
	public static int SumHalfPointsOfOutputs(IReadOnlyList<long> outputAmounts) =>
		GetHalfPointsOfOutputs(outputAmounts).Sum();

	/// <summary>What the outputs of a transaction are worth together. It is always a whole number.</summary>
	public static decimal SumOutputPoints(IReadOnlyList<long> outputAmounts) =>
		FromHalfPoints(SumHalfPointsOfOutputs(outputAmounts));

	/// <summary>What the outputs of a transaction are worth together. It is always a whole number.</summary>
	public static decimal SumOutputPoints(Transaction transaction) =>
		SumOutputPoints(GetAmounts(transaction));

	/// <summary>Whether a number can be the points of a transaction: a whole number of half points, and not negative.</summary>
	public static bool IsValid(decimal points) =>
		points >= 0 && points * 2 <= int.MaxValue && points * 2 == decimal.Truncate(points * 2);

	/// <summary>Points as a number of half points.</summary>
	public static int ToHalfPoints(decimal points) =>
		IsValid(points)
			? (int)(points * 2)
			: throw new ArgumentOutOfRangeException(nameof(points), points, "Anonymity points are a whole number of half points, and not negative.");

	/// <summary>The points of a transaction.</summary>
	/// <param name="inputPoints">What its inputs were worth together as outputs of the transactions that created them.</param>
	/// <param name="outputPoints">What its outputs are worth together.</param>
	public static decimal Calculate(decimal inputPoints, decimal outputPoints) =>
		Math.Min(inputPoints, outputPoints);

	private static long[] GetAmounts(Transaction transaction) =>
		transaction.Outputs.Select(output => output.Value.Satoshi).ToArray();

	private static int CountOutputsWithTheAmount(IReadOnlyList<long> outputAmounts, uint index)
	{
		var amount = outputAmounts[(int)index];
		var count = 0;

		for (var i = 0; i < outputAmounts.Count; i++)
		{
			if (outputAmounts[i] == amount)
			{
				count++;
			}
		}

		return count;
	}
}
