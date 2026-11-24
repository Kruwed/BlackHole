using System.Globalization;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>How the market writes its numbers, the same way wherever they show.</summary>
public static class MarketFormat
{
	private const decimal SatsPerBtc = 100_000_000m;

	/// <summary>What stands in place of an amount of nothing.</summary>
	public const string None = "—";

	/// <summary>The minus sign of a number that is written with its sign.</summary>
	public const string Minus = "−";

	/// <summary>An amount in BTC, always with eight decimals.</summary>
	public static string Btc(long sats) => (sats / SatsPerBtc).ToString("0.00000000", CultureInfo.InvariantCulture);

	/// <summary>An amount in BTC with eight decimals, or a dash for nothing: the bond of a maker that has none.</summary>
	public static string BtcOrNone(long sats) => sats == 0 ? None : Btc(sats);

	/// <summary>An amount in BTC with a given number of decimals, a half rounded up.</summary>
	public static string Btc(long sats, int decimals) =>
		Math.Round(sats / SatsPerBtc, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);

	/// <summary>A fee rate, with one decimal.</summary>
	public static string FeeRate(decimal satsPerVirtualByte) =>
		Math.Round(satsPerVirtualByte, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture);

	/// <summary>A rate of an anonymity point or a spread: with one decimal at most, and none where it is a whole number.</summary>
	public static string Rate(decimal satsPerPoint)
	{
		var rounded = Math.Floor(Math.Abs(satsPerPoint) * 10 + 0.5m) / 10;

		// The spread of an offer without points is below zero: it sells at 0 whatever it buys at.
		return rounded == 0 ? "0" : (satsPerPoint < 0 ? Minus : "") + rounded.ToString("0.#", CultureInfo.InvariantCulture);
	}

	/// <summary>A number of sats, virtual bytes or blocks: rounded to a whole number, a half up, with commas between the thousands.</summary>
	public static string Whole(decimal number) =>
		Math.Floor(number + 0.5m).ToString("#,0", CultureInfo.InvariantCulture);

	/// <summary>A whole number with its sign in front: a plus, a minus or, for nothing, none.</summary>
	public static string Signed(decimal number) =>
		(number > 0 ? "+" : number < 0 ? Minus : "") + Whole(Math.Abs(number));

	/// <summary>A number as it stands in a box that takes a typed one: with the decimals that it has, eight at most, and no more.</summary>
	/// <summary>
	/// A difference of anonymity points with its sign: "+56", "−0.5". Points are counted in halves, and the points of
	/// an offer are a mean, so a difference need not be whole. Where it is, it reads as the design writes it.
	/// </summary>
	public static string SignedPoints(decimal points)
	{
		var rounded = Math.Floor(Math.Abs(points) * 10 + 0.5m) / 10;

		return (rounded == 0 ? "" : points > 0 ? "+" : Minus) + rounded.ToString("#,0.#", CultureInfo.InvariantCulture);
	}

	public static string Plain(decimal number) => number.ToString("0.########", CultureInfo.InvariantCulture);

	/// <summary>Anonymity points: a whole number where they are one, and one decimal otherwise.</summary>
	public static string Points(decimal points) => (Math.Floor(points * 10 + 0.5m) / 10).ToString("0.#", CultureInfo.InvariantCulture);

	/// <summary>A value of a criterion with its unit, as a filter of the book shows it.</summary>
	/// <param name="value">The value in the unit of the criterion: rates in their own, the bond and the liquidity in BTC.</param>
	public static string Criterion(BookCriterion criterion, decimal value)
	{
		switch (criterion)
		{
			case BookCriterion.FeeRate:
				return $"{FeeRate(value)} sat/vB";

			case BookCriterion.Bond:
				return $"{(value == 0 ? None : value.ToString("0.00000000", CultureInfo.InvariantCulture))} BTC";

			case BookCriterion.Liquidity:
				return $"{value.ToString("0.00000000", CultureInfo.InvariantCulture)} BTC";

			default:
				var unit = Math.Abs(value - 1) < 0.05m ? "sat" : "sats";

				return criterion == BookCriterion.Spread ? $"{Rate(value)} {unit}" : $"{Rate(value)} {unit}/AP";
		}
	}

	/// <summary>The name of a criterion as the book and its filters show it.</summary>
	public static string Name(BookCriterion criterion) =>
		criterion switch
		{
			BookCriterion.FeeRate => "Fee rate",
			BookCriterion.Spread => "Spread",
			BookCriterion.BuyRate => "Buy rate",
			BookCriterion.SellRate => "Sell rate",
			BookCriterion.Bond => "Bond",
			BookCriterion.Liquidity => "Liquidity",
			_ => throw new ArgumentOutOfRangeException(nameof(criterion))
		};

	/// <summary>The unit that stands next to the name of a criterion in the head of its column. Rates have none there.</summary>
	public static string Unit(BookCriterion criterion) =>
		criterion switch
		{
			BookCriterion.FeeRate => "sat/vB",
			BookCriterion.Bond or BookCriterion.Liquidity => "BTC",
			_ => ""
		};

	/// <summary>Reads an amount in BTC as it is typed. Null if it is none, or has more than eight decimals.</summary>
	public static long? ParseBtc(string text)
	{
		if (!decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var btc) || btc < 0 || btc > 21_000_000m)
		{
			return null;
		}

		var sats = btc * SatsPerBtc;

		return sats == decimal.Truncate(sats) ? (long)sats : null;
	}

	/// <summary>Reads a number that is not negative as it is typed: digits and one decimal point. Null if it is none.</summary>
	/// <summary>
	/// A number that may be below zero, as the limit of the spread can be. The sign is the one of the keyboard or the
	/// one that the screen writes.
	/// </summary>
	public static decimal? ParseSignedNumber(string text) =>
		decimal.TryParse(text.Trim().Replace(Minus, "-"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? number : null;

	public static decimal? ParseNumber(string text) =>
		decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
}
