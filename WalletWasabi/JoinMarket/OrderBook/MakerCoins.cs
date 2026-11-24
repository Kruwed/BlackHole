using System.Globalization;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>A coin of a wallet as the maker's side of the market sees it.</summary>
/// <param name="Id">What tells the coins of a wallet apart.</param>
/// <param name="Origin">The transaction that created the coin. Of the coins of one transaction, one counts in an offer.</param>
/// <param name="Amount">What the coin holds, in sats.</param>
/// <param name="Points">The anonymity points of the coin. Null while the wallet does not know them.</param>
/// <param name="IsBond">Whether the coin is locked in a fidelity bond.</param>
public sealed record MarketCoin(string Id, string Origin, long Amount, decimal? Points, bool IsBond = false)
{
	/// <summary>Whether the coin is private: it has at least one anonymity point. Only private coins make a bond.</summary>
	public bool IsPrivate => Points >= 1;

	/// <summary>
	/// Whether the coin has anonymity points at all. A coin without points counts in an offer like any other, and has
	/// none to sell. A coin whose points the wallet does not know yet is taken as one without.
	/// </summary>
	public bool HasPoints => Points > 0;
}

/// <summary>The column that the list of the coins is sorted by.</summary>
public enum CoinSortKey
{
	Amount,
	Points
}

/// <summary>The tab of the maker's side that coins are picked for.</summary>
public enum MakerTab
{
	/// <summary>Lock Coins: the fidelity bond.</summary>
	Bond,

	/// <summary>List Offers.</summary>
	Offers
}

/// <summary>How the wallet is split between the fidelity bond and the liquidity, and the coins that are picked for a tab.</summary>
/// <param name="BondShare">The share of the wallet that goes into the bond. A bond that exists fixes it.</param>
/// <param name="BondTarget">What is still to be locked, in sats. A fraction of a sat is possible, as the share is a fraction.</param>
/// <param name="OfferTarget">What is still to be offered, in sats.</param>
/// <param name="CoinIds">The coins that are picked for the tab.</param>
/// <param name="Amount">The amount that the tab starts with, in sats: its target, but not more than the picked coins hold. Null if that is nothing.</param>
public sealed record CoinAllocation(double BondShare, double BondTarget, double OfferTarget, IReadOnlyList<string> CoinIds, long? Amount);

/// <summary>What a selection of coins is worth as an offer.</summary>
/// <param name="Points">
/// The anonymity points of the offer: the mean of the points of the first three selected coins, weighted by their
/// amounts. A coin without points counts in it with none, so an offer of such coins has no points.
/// </param>
/// <param name="Amount">The most that the offer may be for, in sats: the three largest selected coins of different transactions.</param>
/// <param name="WithoutPoints">The selected coins that have no anonymity points. They count like the others, and have nothing to sell.</param>
/// <param name="SharedOrigin">The selected coins that come from the same transaction as a larger selected coin. They are not counted.</param>
/// <param name="Extra">The selected coins beyond the first three. They only add the cost of their inputs.</param>
public sealed record OfferFigures(decimal Points, long Amount, IReadOnlyList<string> WithoutPoints, IReadOnlyList<string> SharedOrigin, IReadOnlyList<string> Extra)
{
	/// <summary>The points as the button of the offer shows them: with one decimal.</summary>
	public string PointsText => MakerCoins.FormatPoints(Points);

	/// <summary>
	/// Whether the offer has anonymity points to sell. An offer without any lists a sell rate of 0: whatever a
	/// transaction is worth, such a maker loses no points in it.
	/// </summary>
	public bool HasPoints => Points > 0;
}

/// <summary>The rules by which a maker's coins are ordered, picked and counted.</summary>
public static class MakerCoins
{
	/// <summary>How many coins of an offer count: the taker pays for the inputs of that many, and they make the amount of the offer.</summary>
	public const int CountedCoins = 3;

	/// <summary>
	/// The order in which coins are picked by themselves: by the column that the list is sorted by, in its direction.
	/// Between coins that the column does not tell apart, the larger one goes first, or the one of more points.
	/// </summary>
	public static Comparison<MarketCoin> GetOrder(CoinSortKey sortKey, bool ascending) =>
		(a, b) =>
		{
			var byAmount = b.Amount.CompareTo(a.Amount);
			var byPoints = (b.Points ?? -1).CompareTo(a.Points ?? -1);
			var direction = ascending ? -1 : 1;

			return sortKey == CoinSortKey.Points
				? (byPoints != 0 ? direction * byPoints : byAmount)
				: (byAmount != 0 ? direction * byAmount : byPoints);
		};

	/// <summary>The coins in the order in which they are picked by themselves. Coins that the order does not tell apart stay as they were given.</summary>
	public static IEnumerable<MarketCoin> Sort(IEnumerable<MarketCoin> coins, CoinSortKey sortKey, bool ascending) =>
		coins.Order(Comparer<MarketCoin>.Create(GetOrder(sortKey, ascending)));

	/// <summary>
	/// The coins that an offer starts with: the first three coins in the order of the list, each of a different
	/// transaction, and none that is locked or in an offer already. Whether a coin has anonymity points does not
	/// matter for that: a list that is sorted by points has the coins with the most points first.
	/// </summary>
	/// <param name="taken">The coins that posted offers hold.</param>
	public static IReadOnlyList<string> GetDefaultOfferCoins(IEnumerable<MarketCoin> coins, IReadOnlySet<string> taken, CoinSortKey sortKey, bool ascending)
	{
		var origins = new HashSet<string>();
		var ids = new List<string>();

		foreach (var coin in Sort(coins.Where(coin => !coin.IsBond && !taken.Contains(coin.Id)), sortKey, ascending))
		{
			if (ids.Count < CountedCoins && origins.Add(coin.Origin))
			{
				ids.Add(coin.Id);
			}
		}

		return ids;
	}

	/// <summary>
	/// Splits the wallet between the fidelity bond and the liquidity, and picks coins for a tab. For a bond: the first
	/// private coin, and more only while the amount of the bond is not covered. For an offer: the first three coins of
	/// different transactions, with or without anonymity points.
	/// </summary>
	/// <param name="taken">The coins that posted offers hold.</param>
	/// <param name="offered">The amount that posted offers are for together, in sats.</param>
	/// <param name="bondShare">The share of the wallet that is to go into the bond.</param>
	public static CoinAllocation Allocate(
		IReadOnlyList<MarketCoin> coins,
		IReadOnlySet<string> taken,
		long offered,
		double bondShare,
		MakerTab tab,
		CoinSortKey sortKey,
		bool ascending)
	{
		var total = coins.Sum(coin => coin.Amount);
		var bonded = coins.Where(coin => coin.IsBond).Sum(coin => coin.Amount);

		// A bond that exists fixes the split until it is over.
		if (bonded > 0 && total > 0)
		{
			bondShare = (double)bonded / total;
		}

		var bondTarget = Math.Max(0, bondShare * total - bonded);
		var offerTarget = Math.Max(0, (1 - bondShare) * total - offered);

		// A bond takes private coins only. An offer takes any coin.
		var free = Sort(coins.Where(coin => !coin.IsBond && !taken.Contains(coin.Id) && (tab == MakerTab.Offers || coin.IsPrivate)), sortKey, ascending);
		var ids = new List<string>();
		long sum = 0;

		if (tab == MakerTab.Bond)
		{
			foreach (var coin in free)
			{
				// A tenth of a sat is the margin of the comparison: the target is a fraction, the coins are not.
				if (sum > 0 && sum >= bondTarget - 0.1)
				{
					break;
				}

				ids.Add(coin.Id);
				sum += coin.Amount;
			}
		}
		else
		{
			var origins = new HashSet<string>();

			foreach (var coin in free)
			{
				if (ids.Count >= CountedCoins)
				{
					break;
				}

				if (!origins.Add(coin.Origin))
				{
					continue;
				}

				ids.Add(coin.Id);
				sum += coin.Amount;
			}
		}

		var target = tab == MakerTab.Bond ? bondTarget : offerTarget;
		var amount = (long)BookRules.RoundHalfUp(Math.Min(target, sum));

		return new CoinAllocation(bondShare, bondTarget, offerTarget, ids, amount > 0 ? amount : null);
	}

	/// <summary>What the selected coins are worth as an offer.</summary>
	public static OfferFigures GetOfferFigures(IReadOnlyList<MarketCoin> coins, IReadOnlySet<string> selected, CoinSortKey sortKey, bool ascending)
	{
		var selection = coins.Where(coin => selected.Contains(coin.Id)).ToArray();
		var ordered = Sort(selection, sortKey, ascending).ToArray();

		// The points of the offer are those of the first three coins, whatever transactions they come from. A coin
		// without points, or with points that are not known yet, counts with none.
		var counted = ordered.Take(CountedCoins).ToArray();
		var countedAmount = counted.Sum(coin => coin.Amount);
		var points = countedAmount > 0 ? counted.Sum(coin => coin.Amount * (coin.Points ?? 0m)) / countedAmount : 0m;

		// The amount of the offer is that of the three largest coins of different transactions.
		long amount = 0;
		var origins = new HashSet<string>();
		var taken = 0;

		foreach (var coin in ordered.OrderByDescending(coin => coin.Amount))
		{
			if (taken >= CountedCoins)
			{
				break;
			}

			if (!origins.Add(coin.Origin))
			{
				continue;
			}

			amount += coin.Amount;
			taken++;
		}

		var sharedOrigin = new List<string>();
		var largestOfOrigin = new HashSet<string>();

		foreach (var coin in selection.OrderByDescending(coin => coin.Amount))
		{
			if (!largestOfOrigin.Add(coin.Origin))
			{
				sharedOrigin.Add(coin.Id);
			}
		}

		return new OfferFigures(
			points,
			amount,
			[.. selection.Where(coin => !coin.HasPoints).Select(coin => coin.Id)],
			sharedOrigin,
			[.. ordered.Skip(CountedCoins).Select(coin => coin.Id)]);
	}

	/// <summary>Points as the button of an offer and the card of a posted offer show them: with one decimal, a half of a tenth rounded up.</summary>
	public static string FormatPoints(decimal points) =>
		(Math.Floor(points * 10 + 0.5m) / 10).ToString("0.0", CultureInfo.InvariantCulture);
}
