namespace WalletWasabi.FeeRateEstimation;

public record FeeRateEstimations
{
	private static readonly int[] AllConfirmationTargets = Constants.ConfirmationTargets.Prepend(1).ToArray();
	public static readonly FeeRateEstimations Empty = new(new Dictionary<int, FeeRate> { { 0, FeeRate.Zero } });

	/// <summary>All allowed target confirmation ranges, i.e. 0-2, 2-3, 3-6, 6-18, ..., 432-1008.</summary>
	private static readonly IEnumerable<(int Start, int End)> TargetRanges = AllConfirmationTargets
		.Skip(1)
		.Zip(AllConfirmationTargets.Skip(1).Prepend(0), (x, y) => (Start: y, End: x));

	public FeeRateEstimations(IDictionary<int, FeeRate> estimations)
	{
		var filteredEstimations = estimations
			.Where(x => x.Key >= AllConfirmationTargets[0] && x.Key <= AllConfirmationTargets[^1])
			.OrderBy(x => x.Key)
			.Select(x => (ConfirmationTarget: x.Key, FeeRate: x.Value, Range: TargetRanges.First(y => y.Start < x.Key && x.Key <= y.End)))
			.GroupBy(x => x.Range, y => y, (x, y) => (Range: x, BestEstimation: y.Last()))
			.Select(x => (ConfirmationTarget: x.Range.End, FeeRate: FeeRate.Max(x.BestEstimation.FeeRate, Constants.MinRelayFeeRate)));

		// Make sure values are unique and in the correct order and fee rates are consistently decreasing.
		var builder = ImmutableSortedDictionary.CreateBuilder<int, FeeRate>();
		var lastFeeRate = new FeeRate(Constants.MaximumNumberOfBitcoinsMoney);
		foreach (var estimation in filteredEstimations)
		{
			// Otherwise it's inconsistent data.
			if (lastFeeRate > estimation.FeeRate)
			{
				lastFeeRate = estimation.FeeRate;

				if (!builder.TryGetKey(estimation.ConfirmationTarget, out _))
				{
					builder.Add(estimation.ConfirmationTarget, estimation.FeeRate);
				}
			}
		}

		Estimations = builder.ToImmutable();
	}

	/// <summary>Gets the fee estimations: int: fee target, int: satoshi/vByte</summary>
	public ImmutableSortedDictionary<int, FeeRate> Estimations { get; }

	public FeeRate GetFeeRate(int confirmationTarget)
	{
		// Where the target is still under or equal to the requested target.
		return Estimations
			.Last(x => x.Key <= confirmationTarget) // The last should be the largest confirmation target.
			.Value;
	}

	public virtual bool Equals(FeeRateEstimations? other) =>
		other is not null && Estimations.SequenceEqual(other.Estimations);

	public override int GetHashCode() =>
		Estimations.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value));

	public override string ToString() =>
		Estimations.Count == 0
			? "none"
			: string.Join(", ", Estimations.Select(x => $"{x.Key} blocks: {x.Value.SatoshiPerByte} sat/vB"));
}
