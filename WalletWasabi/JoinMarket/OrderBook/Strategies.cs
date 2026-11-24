namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>
/// A point of a strategy triangle: how much each of its three corners counts. The weights are not negative and add up to one.
/// </summary>
/// <param name="Top">The weight of the corner at the top.</param>
/// <param name="Left">The weight of the corner at the bottom left.</param>
/// <param name="Right">The weight of the corner at the bottom right.</param>
public readonly record struct TriangleWeights(double Top, double Left, double Right)
{
	/// <summary>The middle of the triangle: every corner counts the same.</summary>
	public static TriangleWeights Centre { get; } = new(1.0 / 3, 1.0 / 3, 1.0 / 3);

	public double this[int corner] =>
		corner switch
		{
			0 => Top,
			1 => Left,
			2 => Right,
			_ => throw new ArgumentOutOfRangeException(nameof(corner))
		};

	/// <summary>The three presets of the corners, blended by the weights.</summary>
	public double Mix(IReadOnlyList<double> presets)
	{
		double mix = 0;

		for (var corner = 0; corner < 3; corner++)
		{
			mix += this[corner] * presets[corner];
		}

		return mix;
	}
}

/// <summary>The triangle that a strategy is picked in, as it is drawn: an upright triangle in a field of 340 by 206.</summary>
public static class StrategyTriangle
{
	/// <summary>The left edge of the field in the coordinates of the corners.</summary>
	public const double FieldLeft = -40;

	public const double FieldWidth = 340;

	public const double FieldHeight = 206;

	/// <summary>The corner at the top.</summary>
	public static (double X, double Y) Top { get; } = (130, 44);

	/// <summary>The corner at the bottom left.</summary>
	public static (double X, double Y) Left { get; } = (50, 182);

	/// <summary>The corner at the bottom right.</summary>
	public static (double X, double Y) Right { get; } = (210, 182);

	/// <summary>
	/// The weights of a position in the field. A position outside the triangle counts as the nearest way into it:
	/// what is negative is cut off. Every weight is rounded to a hundredth.
	/// </summary>
	public static TriangleWeights GetWeights(double x, double y)
	{
		var determinant = (Left.Y - Right.Y) * (Top.X - Right.X) + (Right.X - Left.X) * (Top.Y - Right.Y);
		var top = ((Left.Y - Right.Y) * (x - Right.X) + (Right.X - Left.X) * (y - Right.Y)) / determinant;
		var left = ((Right.Y - Top.Y) * (x - Right.X) + (Top.X - Right.X) * (y - Right.Y)) / determinant;
		var right = 1 - top - left;

		top = Math.Max(0, top);
		left = Math.Max(0, left);
		right = Math.Max(0, right);

		var sum = top + left + right;

		if (sum == 0)
		{
			sum = 1;
		}

		return new TriangleWeights(
			BookRules.RoundHalfUp(top / sum * 100) / 100,
			BookRules.RoundHalfUp(left / sum * 100) / 100,
			BookRules.RoundHalfUp(right / sum * 100) / 100);
	}

	/// <summary>
	/// How the field lies in an area of the given size: as large as fits, in the middle of the area. The triangle is
	/// drawn as large as its column lets it be.
	/// </summary>
	/// <returns>By how much the field is enlarged, and where its upper left corner is in the area.</returns>
	public static (double Scale, double OffsetX, double OffsetY) Fit(double width, double height)
	{
		var scale = Math.Min(width / FieldWidth, height / FieldHeight);

		if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
		{
			scale = 1;
		}

		return (scale, (width - FieldWidth * scale) / 2, (height - FieldHeight * scale) / 2);
	}

	/// <summary>The weights of a point of an area of the given size that the field is drawn in: what a click there means.</summary>
	public static TriangleWeights GetWeights(double x, double y, double width, double height)
	{
		var (scale, offsetX, offsetY) = Fit(width, height);

		return GetWeights((x - offsetX) / scale + FieldLeft, (y - offsetY) / scale);
	}

	/// <summary>Where the marker of the given weights is drawn.</summary>
	public static (double X, double Y) GetPosition(TriangleWeights weights) =>
		(weights.Top * Top.X + weights.Left * Left.X + weights.Right * Right.X,
			weights.Top * Top.Y + weights.Left * Left.Y + weights.Right * Right.Y);

	/// <summary>
	/// What is written below the triangle: the weights in whole percent with the names of their corners. A corner
	/// that counts nothing is left out.
	/// </summary>
	public static string Describe(TriangleWeights weights, string top, string left, string right)
	{
		(string Name, double Weight)[] corners = [(top, weights.Top), (left, weights.Left), (right, weights.Right)];

		return string.Join(
			" · ",
			corners
				.Where(corner => corner.Weight > 0)
				.Select(corner => FormattableString.Invariant($"{BookRules.RoundHalfUp(corner.Weight * 100):0}% {corner.Name}")));
	}
}

/// <summary>What the strategy of a taker makes of the filters.</summary>
/// <param name="Ticks">The position of every filter, in the order of <see cref="BookRules.Criteria"/>.</param>
/// <param name="Cutoffs">The limit of every filter. Null where a filter cuts nothing.</param>
public sealed record TakerFilters(IReadOnlyList<int> Ticks, IReadOnlyList<decimal?> Cutoffs);

/// <summary>
/// The strategy triangle of a taker: Maximize Privacy at the top, Minimize Costs at the bottom left and Maximize Speed
/// at the bottom right. A point of it sets four filters at once.
/// </summary>
/// <remarks>
/// Every corner says what share of the bonded makers a filter is to admit, and a point inside the triangle blends
/// the three shares by its weights. The filter is then set to the value of the bonded maker at that share, the best
/// first, and at least one maker is always admitted. The spread has no share of its own: its limit is the limit of
/// the sell rate less the limit of the buy rate, so that it admits every maker that the two rates admit. The
/// liquidity is never touched: the taker sets it by hand.
/// </remarks>
public static class TakerStrategy
{
	public const string TopName = "Maximize Privacy";
	public const string LeftName = "Minimize Costs";
	public const string RightName = "Maximize Speed";

	/// <summary>The share of the bonded makers that each corner admits, for privacy, costs and speed.</summary>
	public static IReadOnlyDictionary<BookCriterion, double[]> Presets { get; } = new Dictionary<BookCriterion, double[]>
	{
		[BookCriterion.Bond] = [0.5, 0.9, 0.7],
		[BookCriterion.FeeRate] = [0.7, 0.5, 0.9],
		[BookCriterion.SellRate] = [0.9, 0.5, 0.7],
		[BookCriterion.BuyRate] = [0.9, 0.5, 0.7]
	};

	/// <summary>The filters that a point of the triangle sets.</summary>
	/// <param name="currentTicks">The positions of the filters before. Only the one of the liquidity is kept.</param>
	public static TakerFilters GetFilters(IReadOnlyList<BookMaker> makers, TriangleWeights weights, IReadOnlyList<int>? currentTicks)
	{
		var bonded = makers.Where(maker => maker.IsBonded).ToArray();
		var all = BookRules.Criteria.Select(criterion => BookRules.SortValues(makers, criterion)).ToArray();
		var ticks = new int[BookRules.Criteria.Count];

		foreach (var criterion in BookRules.Criteria)
		{
			var index = BookRules.IndexOf(criterion);

			if (!Presets.TryGetValue(criterion, out var shares))
			{
				ticks[index] = currentTicks is not null && index < currentTicks.Count ? currentTicks[index] : 0;
				continue;
			}

			var values = BookRules.SortValues(bonded, criterion);

			if (values.Length == 0)
			{
				ticks[index] = 0;
				continue;
			}

			var share = weights.Mix(shares);
			var keep = (int)Math.Max(1, BookRules.RoundHalfUp(share * values.Length));

			ticks[index] = BookRules.GetTick(all[index], BookRules.IsLowerBetter(criterion), values[Math.Min(keep, values.Length) - 1]);
		}

		var cutoffs = ticks.Select((tick, index) => BookRules.GetCutoff(all[index], tick)).ToArray();

		var spread = BookRules.IndexOf(BookCriterion.Spread);
		var sellCutoff = cutoffs[BookRules.IndexOf(BookCriterion.SellRate)];
		var buyCutoff = cutoffs[BookRules.IndexOf(BookCriterion.BuyRate)];

		if (sellCutoff is { } sell && buyCutoff is { } buy)
		{
			// An exact value, not the value of a maker.
			var limit = RoundToTenth(sell - buy);

			ticks[spread] = BookRules.GetTick(all[spread], isLowerBetter: true, limit);
			cutoffs[spread] = limit;
		}
		else
		{
			ticks[spread] = 0;
			cutoffs[spread] = null;
		}

		return new TakerFilters(ticks, cutoffs);
	}

	/// <summary>Rounds to a tenth, and a half of a tenth up.</summary>
	public static decimal RoundToTenth(decimal value) => Math.Floor(value * 10 + 0.5m) / 10;
}

/// <summary>What the strategy of a maker makes of its offer and of its coins.</summary>
/// <param name="FeeRate">The lowest fee rate of the offer.</param>
/// <param name="BondShare">The share of the wallet that goes into the fidelity bond.</param>
public sealed record MakerTerms(decimal FeeRate, decimal SellRate, decimal BuyRate, double BondShare);

/// <summary>
/// The strategy triangle of a maker: Earn AP at the top, Earn sats at the bottom left and Remix often at the bottom
/// right. A point of it says where the three rates of the offer stand in the book and how the wallet is split
/// between the fidelity bond and the liquidity.
/// </summary>
public static class MakerStrategy
{
	public const string TopName = "Earn AP";
	public const string LeftName = "Earn sats";
	public const string RightName = "Remix often";

	/// <summary>The share of the wallet that each corner locks as a bond.</summary>
	public static IReadOnlyList<double> BondShares { get; } = [0.5, 0.3, 0.7];

	/// <summary>The share of the makers of the book whose fee rate the offer undercuts, for each corner.</summary>
	public static IReadOnlyList<double> FeeRateShares { get; } = [0.5, 0.7, 0.3];

	/// <summary>The share of the makers whose sell rate the offer undercuts.</summary>
	public static IReadOnlyList<double> SellRateShares { get; } = [0.5, 0.3, 0.7];

	/// <summary>The share of the makers whose buy rate the offer exceeds.</summary>
	public static IReadOnlyList<double> BuyRateShares { get; } = [0.7, 0.3, 0.5];

	/// <summary>Whether a maker beats another one on the criterion by a lower value: by its fee rate and by its sell rate it does.</summary>
	private static bool Undercuts(BookCriterion rate) => rate is BookCriterion.FeeRate or BookCriterion.SellRate;

	/// <summary>
	/// The value of the book at a position: a fee rate or a sell rate that undercuts the given share of the makers, or
	/// a buy rate that exceeds that share of them. Zero for an empty book.
	/// </summary>
	public static decimal GetRateAt(IReadOnlyList<BookMaker> makers, BookCriterion rate, double share)
	{
		if (makers.Count == 0)
		{
			return 0;
		}

		var values = makers.Select(maker => maker.GetValue(rate));
		decimal[] sorted = Undercuts(rate) ? [.. values.OrderDescending()] : [.. values.Order()];

		return sorted[Math.Min(sorted.Length - 1, (int)BookRules.RoundHalfUp(share * (sorted.Length - 1)))];
	}

	/// <summary>
	/// The share of the book, in whole percent, that a rate beats: the makers that quote a higher fee rate or sell
	/// rate, or a lower buy rate.
	/// </summary>
	public static int GetBeatenPercent(IReadOnlyList<BookMaker> makers, BookCriterion rate, decimal value)
	{
		if (makers.Count == 0)
		{
			return 0;
		}

		var beaten = Undercuts(rate)
			? makers.Count(maker => maker.GetValue(rate) > value)
			: makers.Count(maker => maker.GetValue(rate) < value);

		return (int)BookRules.RoundHalfUp(100.0 * beaten / makers.Count);
	}

	/// <summary>The rates of the offer and the share of the bond that a point of the triangle stands for.</summary>
	public static MakerTerms GetTerms(IReadOnlyList<BookMaker> makers, TriangleWeights weights) =>
		new(
			GetRateAt(makers, BookCriterion.FeeRate, weights.Mix(FeeRateShares)),
			GetRateAt(makers, BookCriterion.SellRate, weights.Mix(SellRateShares)),
			GetRateAt(makers, BookCriterion.BuyRate, weights.Mix(BuyRateShares)),
			weights.Mix(BondShares));
}
