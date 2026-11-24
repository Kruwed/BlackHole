namespace WalletWasabi.JoinMarket.Planning;

/// <summary>
/// Plans the coinjoin of a taker with the makers that it picked: it decides which output values every participant
/// gets, so that as many outputs as possible share their value with two others.
/// </summary>
/// <remarks>
/// <para>
/// Amounts are arbitrary to the sat, so every maker needs one value of its own to make its sum come out: its
/// remainder. A remainder is alone with its value until others take an output of exactly that value. So the plan is
/// a chain, the smallest maker first: every maker takes outputs of the remainders of the two makers before it and
/// leaves its own to the next two, and the taker takes what is left open and small enough for it.
/// </para>
/// <para>
/// The planner is the one of the design of the market for anonymity points, and it is kept as it is there, down to the
/// order of its steps: it is a search that stops at the first of several plans of the same cost, and the plans are
/// compared with the ones of the design. It does not claim the cheapest plan that there is, only a good one that is
/// found quickly and that is the same one every time. Amounts are whole sats, and the costs that plans are compared
/// by are fractions of a sat in the floating point arithmetic of the design.
/// </para>
/// <para>
/// The outputs of every maker add up to its whole liquidity, and those of the taker to its amount and what it
/// expects to gain from the settlement: fees and settlements are told by the plan and not taken out of anybody's
/// outputs. A plan is what a taker looks at, not a transaction that can be signed.
/// </para>
/// </remarks>
public static class TransactionPlanner
{
	/// <summary>The size that a remainder is weighed with while somebody can still match it.</summary>
	public const int TailVb = 57;

	/// <summary>
	/// The size that an output that is alone with its value is weighed with when plans are compared: what it is billed,
	/// and as much again for what it gives away about its owner. The fee table bills the first half only.
	/// </summary>
	public const int LoneVb = MarketRules.UnmatchedOutputVb + 100;

	/// <summary>What one plan has to be cheaper by than another one to take its place.</summary>
	private const double Margin = 1e-9;

	private const int OutputVb = MarketRules.OutputVb;
	private const long Dust = MarketRules.Dust;
	private const int MaxMakerOutputs = MarketRules.MaxMakerOutputs;

	/// <summary>How many of the largest values that fit a maker tries for its first output, its second one and so on.</summary>
	private static readonly int[] MakerBranch = [10, 6, 4, 2, 2, 1, 1, 1];

	private static readonly int[] TakerBranch = [10, 6, 4, 2, 1, 1, 1, 1, 1, 1];

	/// <summary>
	/// The standard output values: 1, 1.5, 2, 2.5, 3, 4, 5 and 8 times a power of ten, from 400 sats to 80 BTC. A
	/// participant can always take one of them, whether another output has it already or not.
	/// </summary>
	public static IReadOnlyList<long> StandardValues { get; } = CreateStandardValues();

	/// <summary>Plans the transaction of a taker with makers. The makers are gone through in the order in which they are given, which decides between plans of the same cost.</summary>
	public static TransactionPlan Plan(PlannedTaker taker, IReadOnlyList<PlannedMaker> makers) =>
		new Run(taker, makers).Execute();

	private static long[] CreateStandardValues()
	{
		var values = new List<long>();
		long powerOfTen = 10;

		// The multipliers are tenfold, so that halves are whole numbers.
		for (var exponent = 2; exponent <= 9; exponent++)
		{
			foreach (var multiplier in (long[])[10, 15, 20, 25, 30, 40, 50, 80])
			{
				var value = multiplier * powerOfTen;

				if (value >= Dust)
				{
					values.Add(value);
				}
			}

			powerOfTen *= 10;
		}

		return [.. values];
	}

	/// <summary>What an output counts for by the number of outputs that have its value.</summary>
	private static double PointsOf(int outputsWithTheValue) =>
		outputsWithTheValue >= 3 ? 1 : outputsWithTheValue == 2 ? 0.5 : 0;

	/// <summary>The outputs of a participant while its plan is in the making.</summary>
	private abstract class Holder
	{
		public List<long> Picks { get; set; } = [];
		public long Change { get; set; }
		public long Forfeit { get; set; }

		public int OutputCount => Picks.Count + (Change != 0 ? 1 : 0);

		public IEnumerable<long> Outputs => Change != 0 ? Picks.Append(Change) : Picks;
	}

	private sealed class Part : Holder
	{
		public required int Index { get; init; }

		/// <summary>The inputs of the maker that count as matched.</summary>
		public required int Counted { get; init; }

		/// <summary>The payment outputs of the maker.</summary>
		public required long[] Pay { get; init; }

		/// <summary>The liquidity of the maker less its payment outputs.</summary>
		public required long Amount { get; init; }

		/// <summary>The picks that match the remainder of a participant before this one.</summary>
		public List<long> Forced { get; set; } = [];
	}

	private sealed class TakerOutputs : Holder;

	private readonly record struct PartSnapshot(long[] Picks, long Change, long Forfeit, long[] Forced);

	private sealed record Snapshot(double Cost, PartSnapshot[] Parts, long[] TakerPicks, long TakerChange, long TakerForfeit)
	{
		public Part[]? Order { get; set; }
	}

	/// <summary>A way for the taker to place its amount.</summary>
	private readonly record struct TakerCandidate(long[] Picks, long Change, long Forfeit);

	/// <summary>A way to match an output that is left alone with its value at the end.</summary>
	private sealed record ClosingOption(Part Maker, long? Swapped, long Delta, long Odd, long Value, int Extra, List<(Part Part, long Shift)> Shifts);

	/// <summary>One planning of a transaction.</summary>
	private sealed class Run
	{
		private readonly IReadOnlyList<PlannedMaker> _makers;
		private readonly PlannedTaker _takerInput;
		private readonly int _makerCount;
		private readonly int _takerInputs;
		private readonly long _takerAmount;
		private readonly decimal _feeRate;
		private readonly double _f;
		private readonly double[] _buy;
		private readonly double[] _sell;
		private readonly double[] _points;
		private readonly Part[] _parts;
		private readonly int _matchedInputs;
		private readonly int _extraInputs;
		private readonly int _paymentCount;
		private readonly Dictionary<long, int> _tally = [];

		/// <summary>What one anonymity point settles for across the makers.</summary>
		private readonly double _settledPerPoint;

		/// <summary>What a point is worth when plans are compared: never less than the output that mints it.</summary>
		private readonly double _pointWorth;

		private readonly double _popularityWeight;

		private TakerOutputs _taker = new();
		private Part[] _order = [];
		private Part? _head;
		private long _lastAmount;

		/// <summary>What the taker expected to gain from the settlement the last time it was planned.</summary>
		private long _expectedGain;

		public Run(PlannedTaker taker, IReadOnlyList<PlannedMaker> makers)
		{
			_takerInput = taker;
			_makers = makers;
			_makerCount = makers.Count;
			_takerInputs = taker.InputCount;
			_takerAmount = taker.Amount;
			_feeRate = MarketRules.GetFeeRate(makers.Select(maker => maker.MinFeeRate));
			_f = (double)_feeRate;
			_buy = [.. makers.Select(maker => (double)maker.BuyRate)];
			_sell = [.. makers.Select(maker => (double)maker.SellRate)];
			_points = [.. makers.Select(maker => (double)maker.Points)];

			_parts = [.. makers.Select((maker, index) => new Part
			{
				Index = index,
				Counted = maker.MatchedInputs,
				Pay = [.. maker.Payments],
				Amount = maker.Liquidity - maker.Payments.Sum()
			})];

			_matchedInputs = _parts.Sum(part => part.Counted);
			_extraInputs = makers.Sum(maker => maker.ExtraInputs);
			_paymentCount = _parts.Sum(part => part.Pay.Length);

			double settledPerPoint = 0;

			for (var i = 0; i < _makerCount; i++)
			{
				settledPerPoint += _matchedInputs > _points[i] ? _buy[i] : _sell[i];
			}

			_settledPerPoint = settledPerPoint;
			_pointWorth = Math.Max(LoneVb * _f, settledPerPoint);
			_popularityWeight = 0.1 * OutputVb * _f / 10;
		}

		public TransactionPlan Execute()
		{
			double objective = 0;
			int[] chain = [];

			if (_makerCount > 0)
			{
				PlanMakersAndTaker();
				objective = Finish();
				chain = [.. _order.Select(part => part.Index)];
			}

			return Describe(objective, chain);
		}

		private void Add(long value, int delta)
		{
			var count = _tally.GetValueOrDefault(value) + delta;

			if (count > 0)
			{
				_tally[value] = count;
			}
			else
			{
				_tally.Remove(value);
			}
		}

		private void AddOutputs(Holder holder, int delta)
		{
			foreach (var value in holder.Picks)
			{
				Add(value, delta);
			}

			if (holder.Change != 0)
			{
				Add(holder.Change, delta);
			}
		}

		private int CountOf(long value) => _tally.GetValueOrDefault(value);

		/// <summary>What the outputs that are in the tally are worth together.</summary>
		private double PointCount()
		{
			double points = 0;

			foreach (var count in _tally.Values)
			{
				points += count * PointsOf(count);
			}

			return points;
		}

		/// <summary>A tie-break only: an output that has to exist anyway joins the value that the most outputs have.</summary>
		private double Popularity(long value) => _popularityWeight * Math.Min(CountOf(value), 10);

		/// <summary>What the makers pay the taker and what the taker pays the makers for a transaction of the given points.</summary>
		private (double Earn, double Pay) SettleAt(double points)
		{
			double earn = 0, pay = 0;

			for (var i = 0; i < _makerCount; i++)
			{
				var delta = points - _points[i];
				var sats = delta > 0 ? delta * _buy[i] : delta * _sell[i];

				if (sats > 0)
				{
					earn += sats;
				}
				else
				{
					pay -= sats;
				}
			}

			return (earn, pay);
		}

		private void SetOrder(Part[] order)
		{
			_order = order;
			_head = order[0].Amount > order[^1].Amount ? order[0] : null;
			_lastAmount = order[^1].Amount;
		}

		/// <summary>The cost of the plan as it stands: outputs by whether their value is shared at all, less the points that are minted.</summary>
		private double PlanCost()
		{
			long vb = 0;
			var forfeit = _taker.Forfeit;

			foreach (var part in _parts)
			{
				foreach (var value in part.Picks)
				{
					vb += CountOf(value) >= 2 ? OutputVb : LoneVb;
				}

				if (part.Change != 0)
				{
					vb += CountOf(part.Change) >= 2 ? OutputVb : LoneVb;
				}

				forfeit += part.Forfeit;
			}

			// The change of the taker counts as alone with its value like any other output when a plan is chosen.
			foreach (var value in _taker.Picks)
			{
				vb += CountOf(value) >= 2 ? OutputVb : LoneVb;
			}

			if (_taker.Change != 0)
			{
				vb += CountOf(_taker.Change) >= 2 ? OutputVb : LoneVb;
			}

			return vb * _f + forfeit - _pointWorth * Math.Min(_matchedInputs, PointCount());
		}

		private Snapshot Evaluate() =>
			new(
				PlanCost(),
				[.. _parts.Select(part => new PartSnapshot([.. part.Picks], part.Change, part.Forfeit, [.. part.Forced]))],
				[.. _taker.Picks],
				_taker.Change,
				_taker.Forfeit);

		/// <summary>The ways for the taker to place its amount against the outputs of the makers that are in the tally. Its own are not.</summary>
		/// <param name="gain">What the taker expects to gain from the settlement. It is placed with its amount.</param>
		/// <param name="full">Whether this is the search for the final plan. The rounds before it use a cheaper one.</param>
		private List<TakerCandidate> TakerPlans(long gain, bool full)
		{
			// Values that the taker can lift: an output of a value that is alone turns it into a pair, and joining a
			// pair makes a set of three, which mints two points.
			var lone = new HashSet<long>();
			var pair = new HashSet<long>();

			foreach (var (value, count) in _tally)
			{
				if (count == 1)
				{
					lone.Add(value);
				}
				else if (count == 2)
				{
					pair.Add(value);
				}
			}

			var gap = Math.Max(0, _matchedInputs - PointCount());
			var amount = _takerAmount + gain;

			// The taker may make as many outputs as pay for themselves. The smallest values that are alone come first, as many as fit.
			var forced = new List<long>();
			long forcedSum = 0;

			foreach (var value in lone.Where(value => value <= amount - Dust).Order())
			{
				if (forcedSum + value <= amount - Dust)
				{
					forced.Add(value);
					forcedSum += value;
				}
			}

			// While the transaction is short of the points that its inputs allow, pairs come next, the smallest first.
			var forcedWithPairs = new List<long>(forced);
			var forcedWithPairsSum = forcedSum;
			var need = gap - forced.Count;

			foreach (var value in pair.Where(value => value <= amount - Dust).Order())
			{
				if (need > 0 && forcedWithPairsSum + value <= amount - Dust)
				{
					forcedWithPairs.Add(value);
					forcedWithPairsSum += value;
					need -= 2;
				}
			}

			var maxOutputs = full ? MarketRules.MaxTakerOutputs : 6;
			var creditedOutputs = Math.Max(_takerInputs, Math.Ceiling(gap));
			long[] existing = [.. _tally.Keys];

			var options = new DecomposeOptions
			{
				FeeRate = _f,
				MaxOutputs = maxOutputs,
				Branch = TakerBranch,
				Credit = place => place == 0 ? _pointWorth : place < creditedOutputs ? _settledPerPoint : 0,
				Bonus = value => lone.Contains(value) ? (LoneVb - OutputVb) * _f : pair.Contains(value) && gap > 0 ? _pointWorth : 0,
				Popularity = Popularity,
				ForfeitMax = LoneVb * _f,
				ChangeVb = _ => LoneVb,
				Existing = existing,
				FitWindow = LoneVb * _f,
				FitMax = full ? 4 : 3
			};

			long[] eligible = [.. existing.OrderDescending()];
			long[] lonely = [.. lone];

			var candidates = new List<TakerCandidate>();

			void Take(Decomposition? plan)
			{
				if (plan is not null)
				{
					candidates.Add(new TakerCandidate([.. plan.Picks], plan.Change, plan.Forfeit));
				}
			}

			Take(Decomposer.DecomposeWith(amount, eligible, options, lonely, full ? 2 : 1));
			Take(forced.Count > 0 ? Decomposer.DecomposeWith(amount, eligible, options, lonely, 1, forced) : null);
			Take(forcedWithPairs.Count > forced.Count ? Decomposer.DecomposeWith(amount, eligible, options, lonely, 1, forcedWithPairs) : null);

			// For the final plan only: an output of every value that is alone, and the rest as change, which the closing step may still match.
			if (full && forced.Count > 0 && forced.Count < maxOutputs && !forced.Contains(amount - forcedSum))
			{
				candidates.Add(new TakerCandidate([.. forced], amount - forcedSum, 0));
			}

			return candidates;
		}

		/// <summary>The candidate that makes the cheapest transaction: what each does to everyone's fees and to the points decides.</summary>
		private TakerOutputs Cheapest(List<TakerCandidate> candidates)
		{
			var keep = _taker;
			TakerOutputs? pick = null;
			var pickCost = double.PositiveInfinity;

			foreach (var candidate in candidates)
			{
				var outputs = new TakerOutputs { Picks = [.. candidate.Picks], Change = candidate.Change, Forfeit = candidate.Forfeit };

				_taker = outputs;
				AddOutputs(outputs, 1);
				var cost = PlanCost();
				AddOutputs(outputs, -1);

				if (cost < pickCost - Margin)
				{
					pick = outputs;
					pickCost = cost;
				}
			}

			_taker = keep;
			return pick ?? new TakerOutputs();
		}

		/// <summary>
		/// Plans the makers in two orders and from two starting points, each repeated until nothing changes, and
		/// leaves the cheapest plan that was seen in place, without the outputs of the taker.
		/// </summary>
		private void PlanMakersAndTaker()
		{
			// The smallest maker first: each one matches the remainders of the two before it, which always fit.
			// Where the largest maker stands far above the rest, nothing that it could match adds up to its amount:
			// it heads the chain then, with values that the others can take, and the second largest closes it.
			var ascending = _parts.OrderBy(part => part.Amount).ToArray();
			var orders = new List<Part[]> { ascending };

			if (ascending.Length >= 3)
			{
				orders.Add([ascending[^1], .. ascending[..^1]]);
			}

			Snapshot? best = null;
			_expectedGain = 0;

			foreach (var order in orders)
			{
				SetOrder(order);

				// The first round may or may not take the whole amounts of the other makers for values to choose from.
				foreach (var seedAmounts in (bool[])[true, false])
				{
					foreach (var part in _parts)
					{
						part.Picks = [];
						part.Change = 0;
						part.Forfeit = 0;
						part.Forced = [];
					}

					_taker = new TakerOutputs();
					_tally.Clear();

					foreach (var part in _parts)
					{
						foreach (var payment in part.Pay)
						{
							Add(payment, 1);
						}
					}

					// In the first round the amount of the taker is in the tally as if it were an output already: the
					// makers match it, which is how the main output of the taker comes to be one of a set of three.
					Add(_takerAmount, 1);

					double? previousCost = null;

					for (var round = 0; round < 4; round++)
					{
						// The taker plans again after the makers. They are not to follow outputs that it will not keep.
						if (round > 0)
						{
							AddOutputs(_taker, -1);
						}

						PlanMakers(round, seedAmounts);

						if (round == 0)
						{
							Add(_takerAmount, -1);
						}

						long gain = 0;

						for (var iteration = 0; iteration < 2; iteration++)
						{
							_taker = Cheapest(TakerPlans(gain, full: false));
							AddOutputs(_taker, 1);
							var (earn, pay) = SettleAt(Math.Min(_matchedInputs, PointCount()));
							AddOutputs(_taker, -1);
							gain = (long)Math.Max(0, Math.Floor(earn - pay));
						}

						_expectedGain = gain;
						AddOutputs(_taker, 1);

						var snapshot = Evaluate();
						snapshot.Order = _order;

						if (best is null || snapshot.Cost < best.Cost - Margin)
						{
							best = snapshot;
						}

						if (previousCost is { } previous && Math.Abs(snapshot.Cost - previous) < Margin)
						{
							break;
						}

						previousCost = snapshot.Cost;
					}
				}
			}

			// The makers of the best round, and no outputs of the taker yet.
			SetOrder(best!.Order!);
			_taker = new TakerOutputs();

			for (var i = 0; i < _parts.Length; i++)
			{
				var saved = best.Parts[i];

				_parts[i].Picks = [.. saved.Picks];
				_parts[i].Change = saved.Change;
				_parts[i].Forfeit = saved.Forfeit;
				_parts[i].Forced = [.. saved.Forced];
			}

			Retally();
		}

		/// <summary>One round of the makers: each of them plans its outputs again, in the order of the chain.</summary>
		private void PlanMakers(int round, bool seedAmounts)
		{
			var order = _order;

			// The outputs of makers that have not planned in this round yet can be seen and matched, but nothing is
			// built on them as if they were settled: the chain only runs one way.
			var down = new Dictionary<long, int>();

			foreach (var maker in order)
			{
				foreach (var value in maker.Outputs)
				{
					down[value] = down.GetValueOrDefault(value) + 1;
				}
			}

			for (var place = 0; place < order.Length; place++)
			{
				var part = order[place];
				var isLast = place == order.Length - 1;
				var isHead = ReferenceEquals(part, _head);
				var previousTail = part.Change;

				// The maker sees only what the others would match.
				foreach (var value in part.Outputs.ToArray())
				{
					Add(value, -1);

					var left = down.GetValueOrDefault(value) - 1;

					if (left > 0)
					{
						down[value] = left;
					}
					else
					{
						down.Remove(value);
					}
				}

				var own = new HashSet<long>(part.Pay);
				long[] eligible;

				if (round == 0)
				{
					var values = new HashSet<long>(StandardValues);

					foreach (var other in _parts)
					{
						if (seedAmounts && !ReferenceEquals(other, part))
						{
							values.Add(other.Amount);
						}

						foreach (var payment in other.Pay)
						{
							values.Add(payment);
						}
					}

					values.Add(_takerAmount);
					eligible = [.. values.Where(value => value >= Dust && !own.Contains(value)).OrderDescending()];
				}
				else
				{
					// The values that exist, and a standard value stays available as well.
					var values = new HashSet<long>(_tally.Keys);
					values.UnionWith(StandardValues);
					eligible = [.. values.Where(value => !own.Contains(value)).OrderDescending()];
				}

				// Nobody plans after the last maker: a new value is only matched if the taker can take an output of it.
				if (isLast)
				{
					eligible = [.. eligible.Where(value => _tally.ContainsKey(value) || value <= _takerAmount)];
				}

				// The values of the head have to be small enough for a later maker to take.
				if (isHead)
				{
					eligible = [.. eligible.Where(value => value <= _lastAmount - Dust)];
				}

				// Values that are short of three holders, counting only what is settled in this round: payments, the
				// amount of the taker and the makers that planned so far. The remainder that this maker had before is
				// left out: an output of it that a later maker took is nothing to match in return.
				var lone = new List<long>();
				var twos = new List<long>();
				double settled = 0;

				foreach (var (value, count) in _tally)
				{
					var holders = count - down.GetValueOrDefault(value);
					settled += holders * PointsOf(holders);

					if (own.Contains(value) || value == previousTail)
					{
						continue;
					}

					if (holders == 1)
					{
						lone.Add(value);
					}
					else if (holders == 2)
					{
						twos.Add(value);
					}
				}

				// The largest first: the taker can only ever take the small ones, so the makers take the large ones.
				lone.Sort((a, b) => b.CompareTo(a));
				twos.Sort((a, b) => b.CompareTo(a));

				var gap = Math.Max(0, _matchedInputs - PointCount());
				var loneSet = new HashSet<long>(lone);
				var twoSet = new HashSet<long>(twos);
				var later = order.Length - 1 - place;

				// Pinned outputs, three at most, so that one output is left for the remainder of this maker. Every
				// value that is alone and fits comes first. Then pairs, each of which becomes a set of three, but
				// only while the transaction would otherwise end short of the points that its inputs allow: what is
				// settled so far, and the point that each later maker adds anyway by matching the remainder before it.
				var room = isHead ? 0 : MaxMakerOutputs - 1;
				var forced = new List<long>();
				long forcedSum = 0;
				var need = _matchedInputs - settled - later;

				foreach (var value in lone)
				{
					if (forced.Count >= room)
					{
						break;
					}

					if (forcedSum + value <= part.Amount - Dust)
					{
						forced.Add(value);
						forcedSum += value;
						need -= 1;
					}
				}

				foreach (var value in twos)
				{
					if (forced.Count >= room || need <= 0)
					{
						break;
					}

					if (forcedSum + value <= part.Amount - Dust)
					{
						forced.Add(value);
						forcedSum += value;
						need -= 2;
					}
				}

				// A remainder mints a point once two more participants match it, and half a point with one. Every
				// maker that is still to plan is larger, so size is no obstacle for them. The remainder of the head
				// has to fit the largest of the others instead. The taker adds one if the remainder fits its amount.
				long[]? fits = isHead
					? [.. ((int[])[order.Length - 1, order.Length - 2]).Where(index => index > place).Select(index => order[index].Amount - Dust)]
					: null;

				int Cloners(long left) =>
					Math.Min(2, (fits is not null ? fits.Count(fit => left <= fit) : later) + (left <= _takerAmount ? 1 : 0));

				// The remainder of the last maker goes to the taker, which then has its amount, its gain, less that
				// remainder and the other small values that are alone, left to place. A remainder for which that
				// is nothing, one existing value or the sum of two or three leaves the taker without change.
				Func<long, double>? tailBonus = null;

				if (isLast)
				{
					var window = LoneVb * _f;
					long[] values = [.. _tally.Keys.Order()];
					var pairSums = new long[values.Length * (values.Length - 1) / 2];
					var next = 0;

					for (var i = 0; i < values.Length; i++)
					{
						for (var j = i + 1; j < values.Length; j++)
						{
							pairSums[next++] = values[i] + values[j];
						}
					}

					Array.Sort(pairSums);

					bool Has(long[] sorted, long target)
					{
						int a = 0, b = sorted.Length;

						while (a < b)
						{
							var middle = (a + b) >> 1;

							if (sorted[middle] < target - window)
							{
								a = middle + 1;
							}
							else
							{
								b = middle;
							}
						}

						return a < sorted.Length && sorted[a] <= target;
					}

					bool HasThree(long target)
					{
						foreach (var value in values)
						{
							if (value < target && Has(pairSums, target - value))
							{
								return true;
							}
						}

						return false;
					}

					long others = 0;
					var reach = _takerAmount + _expectedGain - Dust;

					foreach (var value in lone.Where(value => value <= reach && !forced.Contains(value)).Order())
					{
						if (others + value <= reach)
						{
							others += value;
						}
					}

					var gainAtCreation = _expectedGain;

					tailBonus = left =>
					{
						var rest = _takerAmount + gainAtCreation - others - left;

						return rest >= 0 && (rest <= window || Has(values, rest) || Has(pairSums, rest) || HasThree(rest)) ? LoneVb * _f : 0;
					};
				}

				// Beyond the pinned outputs, another output of a value that is shared already mints one sure point:
				// half of what the third of a pair does. It is worth its fee only when the makers still to come could
				// not close the shortfall by each making one pair a set of three.
				var shortfall = need - 2 * later;
				var wanted = new HashSet<long>(forced.Where(twoSet.Contains));
				long[]? existing = isLast ? [.. _tally.Keys] : null;

				var options = new DecomposeOptions
				{
					FeeRate = _f,
					MaxOutputs = MaxMakerOutputs,
					Branch = MakerBranch,
					Credit = index => index == 0 ? _pointWorth : index < part.Counted && shortfall > 0 ? _settledPerPoint : 0,
					Bonus = value => loneSet.Contains(value)
						? (LoneVb - OutputVb) * _f
						: wanted.Contains(value) || (shortfall > 0 && CountOf(value) >= 2) ? _pointWorth : 0,
					Popularity = Popularity,
					ForfeitMax = LoneVb * _f,
					ChangeVb = left => Cloners(left) != 0 ? TailVb : LoneVb,
					CreditChange = left => Cloners(left) / 2.0,
					TailBonus = tailBonus,
					Exclude = own,
					Existing = existing,
					FitWindow = isLast ? _takerAmount : null,
					FitLimit = isLast ? 200 : null
				};

				var plan = Decomposer.DecomposeWith(part.Amount, eligible, options, lone, 0, forced);

				// The remainder of the last link has to suit the taker, and pinned outputs may stand in the way of
				// that. But a value that is too large for the taker must not be left alone either.
				if (isLast)
				{
					var penalty = LoneVb * _f + (gap > 0 ? _settledPerPoint : 0);

					double Stranded(Decomposition candidate) =>
						lone.Count(value => value > _takerAmount && !candidate.Picks.Contains(value)) * penalty;

					var free = Decomposer.DecomposeWith(part.Amount, eligible, options, lone, 2);

					if (free is not null && (plan is null || free.Cost + Stranded(free) < plan.Cost + Stranded(plan) - Margin))
					{
						plan = free;
					}
				}

				if (plan is null)
				{
					part.Picks = [];
					part.Change = part.Amount;
					part.Forfeit = 0;
				}
				else
				{
					part.Picks = [.. plan.Picks];
					part.Change = plan.Change;
					part.Forfeit = plan.Forfeit;
				}

				part.Forced = [.. forced.Where(part.Picks.Contains)];
				AddOutputs(part, 1);
			}
		}

		private void Retally()
		{
			_tally.Clear();

			foreach (var part in _parts)
			{
				foreach (var payment in part.Pay)
				{
					Add(payment, 1);
				}

				AddOutputs(part, 1);
			}

			AddOutputs(_taker, 1);
		}

		private void Restore(Snapshot snapshot)
		{
			for (var i = 0; i < _parts.Length; i++)
			{
				var saved = snapshot.Parts[i];

				_parts[i].Picks = [.. saved.Picks];
				_parts[i].Change = saved.Change;
				_parts[i].Forfeit = saved.Forfeit;
				_parts[i].Forced = [.. saved.Forced];
			}

			_taker = new TakerOutputs { Picks = [.. snapshot.TakerPicks], Change = snapshot.TakerChange, Forfeit = snapshot.TakerForfeit };
			Retally();
		}

		/// <summary>How many outputs of makers and of the taker are alone with their value.</summary>
		private int LonelyCount()
		{
			var count = 0;

			foreach (var part in _parts)
			{
				foreach (var value in part.Outputs)
				{
					if (CountOf(value) < 2)
					{
						count++;
					}
				}
			}

			foreach (var value in _taker.Outputs)
			{
				if (CountOf(value) < 2)
				{
					count++;
				}
			}

			return count;
		}

		/// <summary>Whether nobody holds a value twice, no output is below the smallest one and nobody has more outputs than it may have.</summary>
		private bool IsSound()
		{
			foreach (var part in _parts)
			{
				long[] outputs = [.. part.Outputs];
				long[] all = [.. outputs, .. part.Pay];

				if (all.Distinct().Count() != all.Length || outputs.Any(value => value < Dust) || outputs.Length > MaxMakerOutputs)
				{
					return false;
				}
			}

			long[] ofTaker = [.. _taker.Outputs];

			return ofTaker.Distinct().Count() == ofTaker.Length && ofTaker.All(value => value >= Dust) && ofTaker.Length <= MarketRules.MaxTakerOutputs;
		}

		/// <summary>
		/// Matches an output that is left alone with its value exactly: the change of the taker, or the remainder of a
		/// maker that nobody could take. One maker with a spare output adds a new value, or gives up one that three
		/// or more share for a larger one. Its remainder shrinks by the difference, and because every remainder is
		/// matched by the links after it, the shift travels down the chain: the remainder of every later maker moves
		/// against the sum of the moving remainders that it has pinned outputs of. The leftover becomes the second
		/// holder of the new value, with a sat or two forfeited where that does not divide.
		/// </summary>
		/// <param name="target">The taker, if null, or the maker whose remainder is to be matched.</param>
		private void CloseLone(Part? target)
		{
			var mine = target is null;
			var leftover = mine ? _taker.Change : target!.Change;

			if (leftover < Dust || CountOf(leftover) != 1)
			{
				return;
			}

			var order = _order;
			var placeOf = new Dictionary<Part, int>();

			for (var i = 0; i < order.Length; i++)
			{
				placeOf[order[i]] = i;
			}

			// How the remainders shift per sat that is taken off the remainder of a maker. Null if a link has no remainder to take its share.
			List<(Part Part, long Shift)>? Ripple(Part from)
			{
				if (from.Change == 0)
				{
					return null;
				}

				var shifts = new List<(Part Part, long Shift)> { (from, -1) };
				var byTail = new Dictionary<long, long> { [from.Change] = -1 };

				for (var i = placeOf[from] + 1; i < order.Length; i++)
				{
					var maker = order[i];
					long shift = 0;
					var hit = false;

					foreach (var value in maker.Forced)
					{
						if (byTail.TryGetValue(value, out var moving))
						{
							shift -= moving;
							hit = true;
						}
					}

					if (!hit)
					{
						continue;
					}

					if (shift != 0)
					{
						if (maker.Change == 0 || byTail.ContainsKey(maker.Change))
						{
							return null;
						}

						byTail[maker.Change] = shift;
					}

					shifts.Add((maker, shift));
				}

				return shifts;
			}

			var takerClones = mine ? new HashSet<long>(_taker.Picks) : null;
			var options = new List<ClosingOption>();

			foreach (var maker in order)
			{
				if (ReferenceEquals(maker, target) || Ripple(maker) is not { } shifts)
				{
					continue;
				}

				// How the leftover moves per sat of the difference, and the tightest remainder that shrinks along the way.
				long targetShift = 0;

				if (!mine)
				{
					foreach (var (part, shift) in shifts)
					{
						if (ReferenceEquals(part, target))
						{
							targetShift = shift;
						}
					}
				}

				var room = double.PositiveInfinity;

				foreach (var (part, shift) in shifts)
				{
					if (mine && shift != 0 && takerClones!.Contains(part.Change))
					{
						targetShift -= shift;
					}

					if (shift < 0 && !ReferenceEquals(part, target))
					{
						room = Math.Min(room, Math.Floor((double)(part.Change - Dust) / -shift));
					}
				}

				// The leftover would outgrow the value that is meant to match it.
				if (targetShift > 0)
				{
					continue;
				}

				var divisor = 1 - targetShift;

				foreach (var swapped in maker.Picks)
				{
					if (!maker.Forced.Contains(swapped) && CountOf(swapped) >= 3)
					{
						var delta = (long)Math.Floor((double)(leftover - swapped) / divisor);

						if (delta > 0 && delta <= room)
						{
							options.Add(new ClosingOption(maker, swapped, delta, leftover - swapped - divisor * delta, swapped + delta, Extra: 0, shifts));
						}
					}
				}

				if (maker.OutputCount < MaxMakerOutputs)
				{
					var delta = (long)Math.Floor((double)leftover / divisor);

					if (delta >= Dust && delta <= room)
					{
						options.Add(new ClosingOption(maker, null, delta, leftover - divisor * delta, delta, Extra: 1, shifts));
					}
				}
			}

			// Giving up a shared value comes before adding an output, and the smallest shift first.
			var sorted = options.OrderBy(option => option.Extra).ThenBy(option => option.Delta).ToArray();

			var before = LonelyCount();
			var saved = Evaluate();
			Snapshot? won = null;
			var tried = 0;

			foreach (var option in sorted)
			{
				if (tried >= 48)
				{
					break;
				}

				// The value of every remainder that moves, with how far it moves.
				var move = new Dictionary<long, long>();

				foreach (var (part, shift) in option.Shifts)
				{
					if (shift != 0)
					{
						move[part.Change] = shift * option.Delta;
					}
				}

				long Moved(long value) => move.TryGetValue(value, out var by) ? value + by : value;

				long ShiftOf(Part part)
				{
					foreach (var (shifted, shift) in option.Shifts)
					{
						if (ReferenceEquals(shifted, part))
						{
							return shift;
						}
					}

					return 0;
				}

				// Every holder of a moving remainder has to be its owner or to have a pinned output of it. An output
				// that was freely chosen, or a payment, of that value would be left behind.
				var ok = true;

				foreach (var part in _parts)
				{
					if (part.Picks.Any(value => move.ContainsKey(value) && !part.Forced.Contains(value))
						|| part.Pay.Any(move.ContainsKey)
						|| (move.ContainsKey(part.Change) && ShiftOf(part) == 0))
					{
						ok = false;
					}
				}

				if (!ok)
				{
					continue;
				}

				tried++;

				foreach (var part in _parts)
				{
					var forcedBefore = part.Forced;

					part.Picks = [.. part.Picks.Select(value =>
						ReferenceEquals(part, option.Maker) && option.Swapped is { } swapped && value == swapped
							? option.Value
							: forcedBefore.Contains(value) ? Moved(value) : value)];
					part.Forced = [.. forcedBefore.Select(Moved)];
				}

				if (option.Swapped is null)
				{
					option.Maker.Picks.Add(option.Value);
				}

				foreach (var (part, shift) in option.Shifts)
				{
					if (shift != 0)
					{
						part.Change += shift * option.Delta;
					}
				}

				if (mine)
				{
					_taker.Picks = [.. _taker.Picks.Select(Moved)];
					_taker.Picks.Add(option.Value);
					_taker.Change = 0;
					_taker.Forfeit += option.Odd;
				}
				else
				{
					// The remainder of the maker is the new value itself now.
					target!.Change -= option.Odd;
					target.Forfeit += option.Odd;
				}

				Retally();

				if (LonelyCount() <= before - 1 && IsSound())
				{
					var snapshot = Evaluate();

					if (snapshot.Cost < (won ?? saved).Cost - Margin)
					{
						won = snapshot;
					}
				}

				Restore(saved);
			}

			if (won is not null)
			{
				Restore(won);
			}
		}

		/// <summary>Nobody holds more than one output that is alone with its value: such outputs of one participant become one remainder.</summary>
		private void MergeLone(Holder holder)
		{
			long[] outputs = [.. holder.Outputs];
			long[] lone = [.. outputs.Where(value => CountOf(value) == 1)];

			if (lone.Length < 2)
			{
				return;
			}

			foreach (var value in lone)
			{
				Add(value, -1);
			}

			holder.Picks = [.. outputs.Where(value => CountOf(value) >= 2)];

			if (holder is Part part)
			{
				part.Forced = [.. part.Forced.Where(part.Picks.Contains)];
			}

			holder.Change = lone.Sum();
			Add(holder.Change, 1);
		}

		private void Tidy()
		{
			foreach (var part in _parts)
			{
				MergeLone(part);
			}

			MergeLone(_taker);
		}

		/// <summary>
		/// Finishes a set of outputs of the makers: the taker is planned with its full search, each of its candidates
		/// is carried through the closing steps, and the cheapest transaction is kept.
		/// </summary>
		private Snapshot FinishFrom(Snapshot bare)
		{
			Restore(bare);

			long gain = 0;
			List<TakerCandidate> candidates = [];
			Snapshot? finished = null;

			for (var iteration = 0; iteration < 2; iteration++)
			{
				candidates = TakerPlans(gain, full: true);
				_taker = Cheapest(candidates);
				AddOutputs(_taker, 1);
				var (earn, pay) = SettleAt(Math.Min(_matchedInputs, PointCount()));
				AddOutputs(_taker, -1);
				gain = (long)Math.Max(0, Math.Floor(earn - pay));
			}

			if (candidates.Count == 0)
			{
				candidates = [new TakerCandidate(bare.TakerPicks, bare.TakerChange, bare.TakerForfeit)];
			}

			foreach (var candidate in candidates)
			{
				Restore(bare);
				_taker = new TakerOutputs { Picks = [.. candidate.Picks], Change = candidate.Change, Forfeit = candidate.Forfeit };
				Retally();

				Tidy();
				CloseLone(null);

				foreach (var part in _parts)
				{
					CloseLone(part);
				}

				Tidy();

				var snapshot = Evaluate();

				if (finished is null || snapshot.Cost < finished.Cost - Margin)
				{
					finished = snapshot;
				}
			}

			return finished!;
		}

		/// <summary>Gives the taker its final outputs and closes what is left open. Returns the cost of the plan that is in place afterwards.</summary>
		private double Finish()
		{
			var bare = Evaluate();
			var finished = FinishFrom(bare);

			// If an output is still alone with its value, or the points are short, the last maker gets other endings:
			// a remainder that nobody can match is dead weight, and an output of a pair that is carved out of it makes
			// a set of three and can bring the remainder within reach of the closing step.
			Restore(finished);

			if (LonelyCount() > 0 || PointCount() < _matchedInputs)
			{
				Restore(bare);

				var last = _order[^1];
				var held = new HashSet<long>(last.Outputs.Concat(last.Pay));
				var twos = new List<long>();

				if (last.Change >= Dust && CountOf(last.Change) == 1 && last.OutputCount < MaxMakerOutputs)
				{
					foreach (var (value, count) in _tally)
					{
						if (count == 2 && !held.Contains(value) && value <= last.Change - Dust)
						{
							twos.Add(value);
						}
					}
				}

				foreach (var value in twos.OrderDescending().Take(3))
				{
					Restore(bare);
					last.Picks.Add(value);
					last.Forced.Add(value);
					last.Change -= value;
					Retally();

					var snapshot = FinishFrom(Evaluate());

					if (snapshot.Cost < finished.Cost - Margin)
					{
						finished = snapshot;
					}
				}
			}

			Restore(finished);
			return finished.Cost;
		}

		/// <summary>Tells the plan that is in place: the outputs, and the counts, fees and settlement that follow from them.</summary>
		private TransactionPlan Describe(double objective, int[] chain)
		{
			// Fees go by whether a value is shared at all, and points by how widely it is shared.
			int matchedOutputs = 0, unmatchedMakerOutputs = 0, matchedPayments = 0, unmatchedTakerOutputs = 0;
			var forfeit = _taker.Forfeit;

			foreach (var part in _parts)
			{
				foreach (var value in part.Outputs)
				{
					if (CountOf(value) >= 2)
					{
						matchedOutputs++;
					}
					else
					{
						unmatchedMakerOutputs++;
					}
				}

				matchedPayments += part.Pay.Count(value => CountOf(value) >= 2);
				forfeit += part.Forfeit;
			}

			foreach (var value in _taker.Outputs)
			{
				if (CountOf(value) >= 2)
				{
					matchedOutputs++;
				}
				else
				{
					unmatchedTakerOutputs++;
				}
			}

			var outputPoints = (int)PointCount();
			var points = MarketRules.GetPoints(_matchedInputs, outputPoints);
			var counts = new PlanCounts(matchedOutputs, unmatchedMakerOutputs, unmatchedTakerOutputs, _paymentCount, matchedPayments, _extraInputs);
			var fees = MarketRules.GetFees(_feeRate, _takerInputs, _matchedInputs, counts, forfeit);
			var settlement = MarketRules.Settle(points, _makers);

			return new TransactionPlan(
				_feeRate,
				_matchedInputs,
				outputPoints,
				points,
				[.. _parts.Select(part => new PlannedMakerOutputs(
					_makers[part.Index].Alias,
					part.Counted,
					part.Amount,
					[.. part.Picks],
					part.Change,
					part.Forfeit,
					[.. part.Forced],
					[.. part.Pay]))],
				new PlannedTakerOutputs(_takerInputs, _takerAmount, [.. _taker.Picks], _taker.Change, _taker.Forfeit),
				[.. _tally.OrderByDescending(entry => entry.Key).Select(entry => (entry.Key, entry.Value))],
				counts,
				fees,
				settlement,
				MarketRules.GetTakerCost(fees, settlement),
				objective,
				chain);
		}
	}
}
