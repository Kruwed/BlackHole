namespace WalletWasabi.JoinMarket.Planning;

/// <summary>What a search of the <see cref="Decomposer"/> found: the outputs of one participant.</summary>
/// <param name="Picks">The values of the outputs that were chosen, in the order in which they were chosen.</param>
/// <param name="Change">The value of the remainder, an output of a value of its own. Zero if there is none.</param>
/// <param name="Forfeit">The sats that are left to the miners because an output for them would cost more.</param>
/// <param name="Cost">What the plan costs in sats, as the search weighs it. It only serves to compare plans.</param>
public sealed record Decomposition(IReadOnlyList<long> Picks, long Change, long Forfeit, double Cost);

/// <summary>
/// How a search of the <see cref="Decomposer"/> weighs the plans that it compares. Sizes are in virtual bytes and
/// everything that is added or taken off is in sats.
/// </summary>
public sealed class DecomposeOptions
{
	/// <summary>The fee rate of the transaction, in sats per virtual byte.</summary>
	public required double FeeRate { get; init; }

	/// <summary>How many outputs the participant has at most, its remainder included.</summary>
	public required int MaxOutputs { get; init; }

	/// <summary>How many of the largest values that fit are tried for the first output, the second one and so on. Beyond the list it is one.</summary>
	public required IReadOnlyList<int> Branch { get; init; }

	/// <summary>Up to how many sats are left to the miners instead of put into a remainder, whatever the remainder would cost.</summary>
	public required double ForfeitMax { get; init; }

	/// <summary>The size that a remainder of the given value is weighed with.</summary>
	public required Func<long, double> ChangeVb { get; init; }

	/// <summary>What the output at the given place is worth to the plan.</summary>
	public Func<int, double>? Credit { get; init; }

	/// <summary>What an output of the given value is worth to the plan on top of that.</summary>
	public Func<long, double>? Bonus { get; init; }

	/// <summary>What breaks ties in favour of the values that many outputs have already.</summary>
	public Func<long, double>? Popularity { get; init; }

	/// <summary>The share of <see cref="Credit"/> that a remainder of the given value stands to earn.</summary>
	public Func<long, double>? CreditChange { get; init; }

	/// <summary>What a remainder of the given value is worth to the plan on top of that.</summary>
	public Func<long, double>? TailBonus { get; init; }

	/// <summary>Values that the participant may not take, neither as an output nor as its remainder.</summary>
	public IReadOnlySet<long>? Exclude { get; init; }

	/// <summary>The values that the transaction has already. With <see cref="FitWindow"/>, sums of them that come close to the amount are tried as well.</summary>
	public IReadOnlyList<long>? Existing { get; init; }

	/// <summary>How much such a sum may leave of the amount.</summary>
	public double? FitWindow { get; init; }

	/// <summary>How many such sums are tried at most.</summary>
	public int? FitLimit { get; init; }

	/// <summary>How many values such a sum has at most.</summary>
	public int? FitMax { get; init; }
}

/// <summary>
/// The three searches that the <see cref="TransactionPlanner"/> is made of. They split the amount of one participant
/// of a coinjoin into outputs of given values and, at most, one remainder of a value of its own.
/// </summary>
/// <remarks>
/// The searches are those of the design of the market for anonymity points, and they are kept as they are there: the
/// order of the candidates, the order of the sums and every comparison decide which of two plans of the same cost is
/// found, and the plans are compared with the ones of the design. Amounts are whole sats. Costs are fractions of a
/// sat, in the floating point arithmetic of the design.
/// </remarks>
public static class Decomposer
{
	/// <summary>The size that an output is billed with if another output of the transaction has its value.</summary>
	public const int OutputVb = MarketRules.OutputVb;

	/// <summary>No output is smaller than this.</summary>
	public const long Dust = MarketRules.Dust;

	/// <summary>What one plan has to be cheaper by than another one to take its place.</summary>
	private const double Margin = 1e-9;

	/// <summary>
	/// Splits an amount into outputs of distinct values out of the eligible ones, which are sorted from high to low,
	/// with or without a remainder, and returns the cheapest plan that it finds. Null if there is none.
	/// </summary>
	public static Decomposition? Decompose(long amount, IReadOnlyList<long> eligible, DecomposeOptions options) =>
		new Search(eligible, options).Run(amount);

	/// <summary>
	/// The sets of one to <paramref name="maxPicks"/> distinct values, out of values that are sorted from low to high,
	/// whose sum leaves between <paramref name="lo"/> and <paramref name="hi"/> of the target. Sets of one value come
	/// first, then pairs, then sets of three, each from the largest values down. Sets of four are only looked for if
	/// there is no smaller set. The search stops after <paramref name="limit"/> sets.
	/// </summary>
	public static List<long[]> FitSums(long target, IReadOnlyList<long> values, int maxPicks, double lo, double hi, int limit)
	{
		var found = new List<long[]>();
		var n = values.Count;

		// The first place whose value is not below x.
		int Lower(double x)
		{
			int a = 0, b = n;

			while (a < b)
			{
				var m = (a + b) >> 1;

				if (values[m] < x)
				{
					a = m + 1;
				}
				else
				{
					b = m;
				}
			}

			return a;
		}

		void Close(long need, long[] picks, int maxIndex)
		{
			var from = Lower(need - hi);
			var to = Math.Min(maxIndex, Lower(need - lo + 1));

			for (var i = to - 1; i >= from && found.Count < limit; i--)
			{
				found.Add([.. picks, values[i]]);
			}
		}

		Close(target, [], n);

		if (maxPicks >= 2)
		{
			for (var i = n - 1; i >= 0 && found.Count < limit; i--)
			{
				if (values[i] > target)
				{
					continue;
				}

				Close(target - values[i], [values[i]], i);
			}
		}

		if (maxPicks >= 3)
		{
			for (var i = n - 1; i >= 0 && found.Count < limit; i--)
			{
				if (values[i] > target)
				{
					continue;
				}

				for (var j = i - 1; j >= 0 && found.Count < limit; j--)
				{
					var sum = values[i] + values[j];

					if (sum > target)
					{
						continue;
					}

					Close(target - sum, [values[i], values[j]], j);
				}
			}
		}

		// Four values: the sums of two are sorted, and every sum looks for the sums that complete it.
		if (maxPicks >= 4 && found.Count == 0)
		{
			var m = Lower((double)target + 1);
			var unsorted = new List<(long Sum, int I, int J)>();

			for (var i = 0; i < m; i++)
			{
				for (var j = i + 1; j < m; j++)
				{
					var sum = values[i] + values[j];

					if (sum <= target)
					{
						unsorted.Add((sum, i, j));
					}
				}
			}

			// Pairs of the same sum stay in the order in which they were made.
			var pairs = unsorted.OrderBy(pair => pair.Sum).ToArray();

			int LowerPair(double x)
			{
				int a = 0, b = pairs.Length;

				while (a < b)
				{
					var mid = (a + b) >> 1;

					if (pairs[mid].Sum < x)
					{
						a = mid + 1;
					}
					else
					{
						b = mid;
					}
				}

				return a;
			}

			for (var p = pairs.Length - 1; p >= 0 && found.Count < limit; p--)
			{
				var (sum, i, j) = pairs[p];
				var need = target - sum;

				for (var q = LowerPair(need - hi); q < pairs.Length && pairs[q].Sum <= need - lo && found.Count < limit; q++)
				{
					var (_, a, b) = pairs[q];

					if (b < i && a != i && a != j && b != j)
					{
						found.Add([values[j], values[i], values[b], values[a]]);
					}
				}
			}
		}

		return found;
	}

	/// <summary>
	/// Like <see cref="Decompose"/>, but before the amount is split, outputs of values that are alone in the
	/// transaction so far are tried: such an output matches the output of somebody else, and what is left of the amount
	/// becomes the remainder that the next participant matches in turn. Sums of the values that the transaction has
	/// already are tried as well, where the options name them.
	/// </summary>
	/// <param name="lonely">The values that one output of the transaction has so far.</param>
	/// <param name="maxClones">How many of them may be taken together: two or three. One of them alone is always tried.</param>
	/// <param name="forced">Values that are taken in any case, in place of the search among the lonely ones. If no plan has them, the search is free after all.</param>
	public static Decomposition? DecomposeWith(
		long amount,
		IReadOnlyList<long> eligible,
		DecomposeOptions options,
		IReadOnlyList<long> lonely,
		int maxClones,
		IReadOnlyList<long>? forced = null)
	{
		var o = options;
		var outputCost = OutputVb * o.FeeRate;
		var isForced = forced is { Count: > 0 };

		double PickCost(long value, int place) =>
			outputCost - (o.Credit is { } credit ? credit(place) : 0) - (o.Bonus is { } bonus ? bonus(value) : 0) - (o.Popularity is { } popularity ? popularity(value) : 0);

		// What is left of the amount becomes a remainder or goes to the miners, whichever is cheaper. Null if neither can be.
		(double Cost, long Forfeit, long Change)? LeftCost(long left, long[] picks)
		{
			if (left == 0)
			{
				return (0, 0, 0);
			}

			var count = picks.Length;
			var canChange = left >= Dust && count < o.MaxOutputs && !picks.Contains(left) && !(o.Exclude is { } exclude && exclude.Contains(left));
			var forfeitCost = left < Dust || left <= o.ForfeitMax ? left : double.PositiveInfinity;
			var changeCost = canChange
				? o.ChangeVb(left) * o.FeeRate + 1e-9 * left - (o.Credit is { } credit && o.CreditChange is { } creditChange ? creditChange(left) * credit(count) : 0) - (o.TailBonus is { } tailBonus ? tailBonus(left) : 0)
				: double.PositiveInfinity;

			if (double.IsPositiveInfinity(forfeitCost) && double.IsPositiveInfinity(changeCost))
			{
				return null;
			}

			return forfeitCost <= changeCost ? (forfeitCost, left, 0) : (changeCost, 0, left);
		}

		var best = isForced ? null : Decompose(amount, eligible, o);

		void Offer(long[] picks)
		{
			double cost = 0;
			long sum = 0;

			for (var place = 0; place < picks.Length; place++)
			{
				cost += PickCost(picks[place], place);
				sum += picks[place];
			}

			if (sum > amount || LeftCost(amount - sum, picks) is not { } left)
			{
				return;
			}

			cost += left.Cost;

			if (best is null || cost < best.Cost - Margin)
			{
				best = new Decomposition(picks, left.Change, left.Forfeit, cost);
			}
		}

		// The small ones can be matched along a chain of participants. The large ones only a large participant can take.
		var candidates = lonely
			.Where(value => value <= amount - Dust && !(o.Exclude is { } exclude && exclude.Contains(value)))
			.Order()
			.ToArray();
		var clones = candidates.Take(6).Concat(candidates.Skip(Math.Max(0, candidates.Length - 4))).Distinct().ToArray();

		var combinations = new List<long[]>();

		if (isForced)
		{
			combinations.Add([.. forced!]);
		}
		else
		{
			combinations.Add([]);

			for (var i = 0; i < clones.Length; i++)
			{
				combinations.Add([clones[i]]);

				if (maxClones >= 2)
				{
					for (var j = i + 1; j < clones.Length; j++)
					{
						combinations.Add([clones[i], clones[j]]);

						if (maxClones >= 3)
						{
							for (var k = j + 1; k < clones.Length; k++)
							{
								combinations.Add([clones[i], clones[j], clones[k]]);
							}
						}
					}
				}
			}
		}

		var existing = o.Existing is { } existingValues && o.FitWindow is not null
			? existingValues.Where(value => !(o.Exclude is { } exclude && exclude.Contains(value))).Order().ToArray()
			: null;

		foreach (var combination in combinations)
		{
			var count = combination.Length;
			var rest = amount - combination.Sum();

			if (rest < 0)
			{
				continue;
			}

			var excluded = new HashSet<long>(o.Exclude ?? (IEnumerable<long>)[]);
			excluded.UnionWith(combination);

			if (count > 0)
			{
				IReadOnlyList<long>? restPicks;

				if (rest == 0)
				{
					restPicks = [];
				}
				else
				{
					var restOptions = new DecomposeOptions
					{
						FeeRate = o.FeeRate,
						MaxOutputs = o.MaxOutputs - count,
						Branch = o.Branch,
						ForfeitMax = o.ForfeitMax,
						ChangeVb = o.ChangeVb,
						Credit = place => o.Credit is { } credit ? credit(place + count) : 0,
						Bonus = o.Bonus,
						Popularity = o.Popularity,
						CreditChange = o.CreditChange,
						TailBonus = o.TailBonus,
						Exclude = excluded,
						Existing = o.Existing,
						FitWindow = o.FitWindow,
						FitLimit = o.FitLimit,
						FitMax = o.FitMax
					};

					restPicks = Decompose(rest, eligible.Where(value => !excluded.Contains(value)).ToArray(), restOptions)?.Picks;
				}

				if (restPicks is not null)
				{
					Offer([.. combination, .. restPicks]);
				}
			}

			if (existing is not null && o.MaxOutputs - count >= 1)
			{
				var maxPicks = Math.Min(o.MaxOutputs - count, count == 0 || isForced ? (o.FitMax is > 0 and var fitMax ? fitMax : 4) : 3);
				var sums = FitSums(rest, existing.Where(value => !excluded.Contains(value)).ToArray(), maxPicks, 0, o.FitWindow!.Value, o.FitLimit is > 0 and var fitLimit ? fitLimit : 24);

				foreach (var sum in sums)
				{
					Offer([.. combination, .. sum]);
				}
			}
		}

		// Values that no plan can take as they were pinned do not stand in the way of a plan.
		return best ?? (isForced ? DecomposeWith(amount, eligible, o, lonely, maxClones) : null);
	}

	/// <summary>One run of <see cref="Decompose"/>: the values are tried from the largest that fits down, to a depth of the outputs that the participant may have.</summary>
	private sealed class Search
	{
		private readonly IReadOnlyList<long> _eligible;
		private readonly DecomposeOptions _o;
		private readonly double _outputCost;
		private readonly long[] _popular;
		private readonly List<long> _picks = [];
		private Decomposition? _best;

		public Search(IReadOnlyList<long> eligible, DecomposeOptions options)
		{
			_eligible = eligible;
			_o = options;
			_outputCost = OutputVb * options.FeeRate;

			// The values that outputs have already, the most popular first. Joining one of them is what a tie is broken for.
			_popular = options.Popularity is { } popularity
				? eligible
					.Where(value => popularity(value) > 0)
					.OrderByDescending(popularity)
					.ThenByDescending(value => value)
					.ToArray()
				: [];
		}

		public Decomposition? Run(long amount)
		{
			Recurse(amount, maxValue: null, baseCost: 0);
			return _best;
		}

		/// <summary>The first place of the eligible values whose value is not above the bound.</summary>
		private int StartAt(long bound)
		{
			int lo = 0, hi = _eligible.Count;

			while (lo < hi)
			{
				var mid = (lo + hi) >> 1;

				if (_eligible[mid] > bound)
				{
					lo = mid + 1;
				}
				else
				{
					hi = mid;
				}
			}

			return lo;
		}

		/// <summary>The picks so far are a plan, with what is left as a remainder or with the miners.</summary>
		private void Consider(long left, double baseCost)
		{
			long change = 0, forfeit = 0;
			double extra = 0;

			if (left > 0)
			{
				var count = _picks.Count;
				var canChange = left >= Dust && count < _o.MaxOutputs && !_picks.Contains(left) && !(_o.Exclude is { } exclude && exclude.Contains(left));
				var forfeitCost = left < Dust || left <= _o.ForfeitMax ? left : double.PositiveInfinity;

				// The tiny part per sat only breaks ties toward the smaller remainder.
				var changeCost = canChange
					? _o.ChangeVb(left) * _o.FeeRate + 1e-9 * left - (_o.Credit is { } credit && _o.CreditChange is { } creditChange ? creditChange(left) * credit(count) : 0)
					: double.PositiveInfinity;
				var changeCostWithBonus = double.IsPositiveInfinity(changeCost) ? changeCost : changeCost - (_o.TailBonus is { } tailBonus ? tailBonus(left) : 0);

				if (double.IsPositiveInfinity(forfeitCost) && double.IsPositiveInfinity(changeCostWithBonus))
				{
					return;
				}

				if (forfeitCost <= changeCostWithBonus)
				{
					forfeit = left;
					extra = forfeitCost;
				}
				else
				{
					change = left;
					extra = changeCostWithBonus;
				}
			}

			var cost = baseCost + extra;

			if (_best is null || cost < _best.Cost - Margin)
			{
				_best = new Decomposition(_picks.ToArray(), change, forfeit, cost);
			}
		}

		/// <param name="maxValue">The value of the output before this one: the next one is smaller. Null for the first output.</param>
		private void Recurse(long rest, long? maxValue, double baseCost)
		{
			Consider(rest, baseCost);

			var count = _picks.Count;

			if (count >= _o.MaxOutputs || rest < Dust)
			{
				return;
			}

			var limit = count < _o.Branch.Count && _o.Branch[count] != 0 ? _o.Branch[count] : 1;
			var bound = maxValue is { } max ? Math.Min(rest, max - 1) : rest;
			var start = StartAt(bound);
			var candidates = new List<long>(limit + 4);

			for (var i = start; i < _eligible.Count && i < start + limit; i++)
			{
				candidates.Add(_eligible[i]);
			}

			// The most popular values that fit are tried for the first three outputs, whatever their size.
			var popularLimit = count < 3 ? Math.Max(2, limit >> 1) : 0;

			for (int i = 0, taken = 0; i < _popular.Length && taken < popularLimit; i++)
			{
				var value = _popular[i];

				if (value <= bound)
				{
					taken++;

					if (!candidates.Contains(value))
					{
						candidates.Add(value);
					}
				}
			}

			foreach (var value in candidates)
			{
				_picks.Add(value);

				Recurse(
					rest - value,
					value,
					baseCost + _outputCost - (_o.Credit is { } credit ? credit(count) : 0) - (_o.Bonus is { } bonus ? bonus(value) : 0) - (_o.Popularity is { } popularity ? popularity(value) : 0));

				_picks.RemoveAt(_picks.Count - 1);
			}
		}
	}
}
