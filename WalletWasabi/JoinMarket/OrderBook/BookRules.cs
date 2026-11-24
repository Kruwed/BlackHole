namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>Where a maker stands in the book: its standing on every criterion, and what they add up to.</summary>
/// <param name="Standings">The standing on every criterion, in the order of <see cref="BookRules.Criteria"/>: 1 for the best value of the book and 0 for the worst.</param>
/// <param name="Score">The weighted mean of the standings.</param>
/// <param name="Rank">The place by score, 1 being the best.</param>
public sealed record MakerRanking(long Id, IReadOnlyList<double> Standings, double Score, int Rank);

/// <summary>
/// The rules of the order book: how makers are ranked, and how the filters of a taker cut the book.
/// </summary>
/// <remarks>
/// A filter has a position, the tick, from 0 to the number of makers less one. Tick 0 cuts nothing. Every further tick
/// drops one more maker, so that the last one keeps the best value of the book alone. The limit that a tick stands
/// for is the value of the maker that many places up from the worst.
/// </remarks>
public static class BookRules
{
	/// <summary>The criteria, in the order in which standings, filter positions and limits are listed.</summary>
	public static IReadOnlyList<BookCriterion> Criteria { get; } =
		[BookCriterion.FeeRate, BookCriterion.Spread, BookCriterion.BuyRate, BookCriterion.SellRate, BookCriterion.Bond, BookCriterion.Liquidity];

	/// <summary>The columns of the book, from left to right.</summary>
	public static IReadOnlyList<BookCriterion> ColumnOrder { get; } =
		[BookCriterion.FeeRate, BookCriterion.Spread, BookCriterion.SellRate, BookCriterion.BuyRate, BookCriterion.Liquidity, BookCriterion.Bond];

	/// <summary>The filters of a taker, from top to bottom.</summary>
	public static IReadOnlyList<BookCriterion> FilterOrder { get; } =
		[BookCriterion.Liquidity, BookCriterion.Bond, BookCriterion.FeeRate, BookCriterion.Spread, BookCriterion.SellRate, BookCriterion.BuyRate];

	/// <summary>What the standing on each criterion weighs in the score of a maker, in the order of <see cref="Criteria"/>.</summary>
	public static IReadOnlyList<int> RankWeights { get; } = [6, 5, 4, 3, 2, 1];

	/// <summary>Whether a lower value is the better one for a taker: a low fee rate, a narrow spread and a low sell rate are, a high buy rate, a large bond and deep liquidity are.</summary>
	public static bool IsLowerBetter(BookCriterion criterion) =>
		criterion is BookCriterion.FeeRate or BookCriterion.Spread or BookCriterion.SellRate;

	public static int IndexOf(BookCriterion criterion) => (int)criterion;

	/// <summary>The values that the makers have for a criterion, the best first.</summary>
	public static decimal[] SortValues(IEnumerable<BookMaker> makers, BookCriterion criterion)
	{
		var values = makers.Select(maker => maker.GetValue(criterion));

		return IsLowerBetter(criterion) ? [.. values.Order()] : [.. values.OrderDescending()];
	}

	/// <summary>
	/// The standing of every maker on every criterion: 1 for the best value of the book and 0 for the worst. Makers of
	/// the same value share the standing of the first of them.
	/// </summary>
	/// <returns>For every maker, in the order in which they were given, its standings in the order of <see cref="Criteria"/>.</returns>
	public static double[][] GetStandings(IReadOnlyList<BookMaker> makers)
	{
		var count = makers.Count;
		var standings = makers.Select(_ => new double[Criteria.Count]).ToArray();

		foreach (var criterion in Criteria)
		{
			var values = makers.Select(maker => maker.GetValue(criterion)).ToArray();
			var indexes = Enumerable.Range(0, count);

			// Makers of the same value stay in the order in which they were given.
			var order = IsLowerBetter(criterion)
				? indexes.OrderBy(index => values[index]).ToArray()
				: indexes.OrderByDescending(index => values[index]).ToArray();

			var position = 0;

			for (var i = 0; i < count; i++)
			{
				if (i > 0 && values[order[i - 1]] != values[order[i]])
				{
					position = i;
				}

				// A maker that is alone in the book is the best one of it.
				standings[order[i]][IndexOf(criterion)] = count > 1 ? 1 - (double)position / (count - 1) : 1;
			}
		}

		return standings;
	}

	/// <summary>
	/// The standing of an offer that is not in the book against the values of the book: by how many makers of the book
	/// are better than it.
	/// </summary>
	/// <param name="sortedValues">The values of the book for every criterion, each the best first.</param>
	public static double[] GetStandingsAgainst(BookMaker maker, IReadOnlyList<decimal[]> sortedValues)
	{
		var standings = new double[Criteria.Count];

		foreach (var criterion in Criteria)
		{
			var values = sortedValues[IndexOf(criterion)];
			var own = maker.GetValue(criterion);
			var better = IsLowerBetter(criterion) ? values.Count(value => value < own) : values.Count(value => value > own);

			standings[IndexOf(criterion)] = 1 - (double)Math.Min(better, values.Length - 1) / Math.Max(1, values.Length - 1);
		}

		return standings;
	}

	/// <summary>The score of a maker: the weighted mean of its standings.</summary>
	public static double GetScore(IReadOnlyList<double> standings)
	{
		double sum = 0;

		for (var i = 0; i < RankWeights.Count; i++)
		{
			sum += RankWeights[i] * standings[i];
		}

		return sum / RankWeights.Sum();
	}

	/// <summary>The score and the rank of every maker. Of two makers of the same score, the one of the lower number ranks higher.</summary>
	/// <returns>The rankings in the order in which the makers were given.</returns>
	public static MakerRanking[] Rank(IReadOnlyList<BookMaker> makers)
	{
		var standings = GetStandings(makers);
		var scores = standings.Select(GetScore).ToArray();

		var ranks = new Dictionary<long, int>();
		var place = 1;

		foreach (var index in Enumerable.Range(0, makers.Count).OrderByDescending(index => scores[index]).ThenBy(index => makers[index].Id))
		{
			ranks[makers[index].Id] = place++;
		}

		return [.. makers.Select((maker, index) => new MakerRanking(maker.Id, standings[index], scores[index], ranks[maker.Id]))];
	}

	/// <summary>The limit that a position of a filter stands for. Null for the position that cuts nothing.</summary>
	/// <param name="sortedBestFirst">The values of the book for the criterion, the best first.</param>
	public static decimal? GetCutoff(IReadOnlyList<decimal> sortedBestFirst, int tick) =>
		tick <= 0 || sortedBestFirst.Count == 0
			? null
			: sortedBestFirst[Math.Max(0, sortedBestFirst.Count - 1 - tick)];

	/// <summary>The tightest position of a filter that still admits a value. Zero if the value is looser than the whole book.</summary>
	public static int GetTick(IReadOnlyList<decimal> sortedBestFirst, bool isLowerBetter, decimal value)
	{
		var count = sortedBestFirst.Count;

		bool Admits(decimal limit) => isLowerBetter ? value <= limit : value >= limit;

		if (count == 0 || !Admits(sortedBestFirst[count - 1]))
		{
			return 0;
		}

		var tick = 0;

		for (var candidate = 0; candidate <= count - 1; candidate++)
		{
			if (Admits(sortedBestFirst[count - 1 - candidate]))
			{
				tick = candidate;
			}
			else
			{
				break;
			}
		}

		return tick;
	}

	/// <summary>Whether a value is at least as good as a limit. No limit admits every value.</summary>
	public static bool Passes(BookCriterion criterion, decimal value, decimal? cutoff) =>
		cutoff is not { } limit || (IsLowerBetter(criterion) ? value <= limit : value >= limit);

	/// <summary>Whether a maker is at least as good as every limit that is set.</summary>
	/// <param name="cutoffs">The limits in the order of <see cref="Criteria"/>. Null where there is none.</param>
	public static bool Passes(BookMaker maker, IReadOnlyList<decimal?> cutoffs)
	{
		foreach (var criterion in Criteria)
		{
			if (!Passes(criterion, maker.GetValue(criterion), cutoffs[IndexOf(criterion)]))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>Rounds to the nearest whole number, and a half up.</summary>
	public static double RoundHalfUp(double value)
	{
		var floor = Math.Floor(value);

		return value - floor >= 0.5 ? floor + 1 : floor;
	}
}
