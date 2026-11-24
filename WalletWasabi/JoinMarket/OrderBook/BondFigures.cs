using System.Globalization;
using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>The size and the fee of a transaction that locks coins in a fidelity bond, as it is estimated before it is built.</summary>
/// <param name="HasChange">Whether the transaction has an output for what is not locked.</param>
/// <param name="Fee">The fee in sats. Fee rates have decimals, so this can be a fraction of a sat.</param>
public sealed record BondTransactionEstimate(bool HasChange, int VirtualBytes, decimal Fee);

/// <summary>Where a fidelity bond stands among the bonds of the makers of the book.</summary>
/// <param name="Rank">The place of the bond by its amount, 1 being the largest.</param>
/// <param name="Ranked">How many bonds are ranked, this one included.</param>
/// <param name="TopPercent">The share of the ranked bonds, in whole percent, that the bond is among: "Top 5%".</param>
public sealed record BondStanding(int Rank, int Ranked, int TopPercent);

/// <summary>What the market tells about a fidelity bond: what locking it costs, where it stands, and how long it lasts.</summary>
public static class BondFigures
{
	/// <summary>How many blocks a new bond is locked for unless the maker says otherwise: two weeks.</summary>
	public const int DefaultLockBlocks = 2016;

	/// <summary>Up to this many sats that are left over after the bond make no output of their own.</summary>
	public const long ChangeThreshold = 546;

	/// <summary>
	/// The estimate of the transaction that locks an amount out of the selected coins: its size at the fixed sizes of
	/// the market, with an output for what is left if that is more than <see cref="ChangeThreshold"/>.
	/// </summary>
	/// <param name="selectedTotal">What the selected coins hold, in sats.</param>
	/// <param name="amount">The amount that is locked, in sats.</param>
	public static BondTransactionEstimate EstimateTransaction(long selectedTotal, long amount, int inputs, decimal feeRate)
	{
		var hasChange = selectedTotal - amount > ChangeThreshold;
		var virtualBytes = MarketRules.OverheadVb + MarketRules.InputVb * inputs + MarketRules.OutputVb * (hasChange ? 2 : 1);

		return new BondTransactionEstimate(hasChange, virtualBytes, virtualBytes * feeRate);
	}

	/// <summary>Where a bond of the given amount stands among the bonds of the makers of the book, which it is counted with.</summary>
	/// <param name="bond">The amount of the bond, in sats.</param>
	public static BondStanding GetStanding(long bond, IEnumerable<BookMaker> makers)
	{
		var bonds = makers.Select(maker => maker.Bond).Where(amount => amount > 0).ToArray();
		var bigger = bonds.Count(amount => amount > bond);

		return new BondStanding(
			bigger + 1,
			bonds.Length + 1,
			Math.Max(1, (int)Math.Ceiling(100.0 * (bigger + 1) / (bonds.Length + 1))));
	}

	/// <summary>
	/// A number of blocks as the time that they roughly take, at ten minutes each: "2 weeks", "3 days" or "5 hours",
	/// with one decimal at most.
	/// </summary>
	public static string DescribeDuration(long blocks)
	{
		var hours = blocks / 6.0;
		var days = hours / 24;
		var weeks = days / 7;

		return weeks >= 2 ? Describe(weeks, "week") : days >= 1 ? Describe(days, "day") : Describe(hours, "hour");

		static string Describe(double value, string unit)
		{
			var rounded = BookRules.RoundHalfUp(value * 10) / 10;
			var number = rounded == Math.Floor(rounded)
				? rounded.ToString("0", CultureInfo.InvariantCulture)
				: rounded.ToString("0.0", CultureInfo.InvariantCulture);

			return $"{number} {unit}{(rounded == 1 ? "" : "s")}";
		}
	}
}
