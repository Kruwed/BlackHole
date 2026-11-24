using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The maker's side of the market on the sample book and the sample wallet: what the strategy triangle of a maker
/// sets, which coins are picked, what a selection is worth as an offer and what is told about a fidelity bond. The
/// expected results were read off the prototype of the design.
/// </summary>
public class MakerSideRulesTests
{
	public static TheoryData<int> StrategyCases => MarketVectors.CaseIndexes("maker", "strategy.json");

	public static TheoryData<int> OfferFigureCases => MarketVectors.CaseIndexes("maker", "offer-figures.json");

	private static MarketCoin[] SampleCoins => [.. SampleMarket.Coins.Select(coin => coin.ToMarketCoin())];

	/// <summary>The rates of the offer, the share of the bond, the coins that are picked and the amount that a tab starts with, for a point of the triangle.</summary>
	[Theory]
	[MemberData(nameof(StrategyCases))]
	public void MakerStrategySetsTheOfferAndPicksTheCoinsAsInTheDesign(int index)
	{
		using var document = MarketVectors.Open("maker", "strategy.json");
		var testCase = document.RootElement.GetProperty("cases")[index];

		var weights = testCase.GetProperty("weights").EnumerateArray().Select(weight => weight.GetDouble()).ToArray();
		var terms = MakerStrategy.GetTerms(SampleMarket.Makers, new TriangleWeights(weights[0], weights[1], weights[2]));

		Assert.Equal(testCase.GetProperty("feeRate").GetDecimal(), terms.FeeRate);
		Assert.Equal(testCase.GetProperty("sellRate").GetDecimal(), terms.SellRate);
		Assert.Equal(testCase.GetProperty("buyRate").GetDecimal(), terms.BuyRate);

		var (sortKey, ascending) = ReadSort(testCase.GetProperty("coinSort"));
		var tab = testCase.GetProperty("tab").GetString() == "bond" ? MakerTab.Bond : MakerTab.Offers;
		var allocation = MakerCoins.Allocate(SampleCoins, new HashSet<string>(), offered: 0, terms.BondShare, tab, sortKey, ascending);

		MarketVectors.AssertClose(testCase.GetProperty("bondShare").GetDouble(), allocation.BondShare, "The share of the bond");
		Assert.Equal(ReadIds(testCase.GetProperty("pickedCoinIds")), allocation.CoinIds.Order(StringComparer.Ordinal));

		var amount = testCase.GetProperty("amountBtc");
		Assert.Equal(amount.ValueKind == JsonValueKind.Null ? null : amount.GetString(), allocation.Amount is { } sats ? MarketFormat.Btc(sats) : null);
	}

	/// <summary>The share of the book, in whole percent, that a typed rate beats.</summary>
	[Fact]
	public void RateBeatsTheShareOfTheBookThatTheDesignSays()
	{
		using var document = MarketVectors.Open("maker", "strategy.json");
		var cases = document.RootElement.GetProperty("ratePositions").GetProperty("cases").EnumerateArray().ToArray();

		Assert.Equal(7, cases.Length);

		foreach (var testCase in cases)
		{
			var rate = MarketVectors.ReadCriterion(testCase.GetProperty("rate").GetString()!);

			Assert.Equal(
				testCase.GetProperty("beatsPercentOfBook").GetInt32(),
				MakerStrategy.GetBeatenPercent(SampleMarket.Makers, rate, testCase.GetProperty("value").GetDecimal()));
		}
	}

	/// <summary>What a hand-made selection of coins is worth as an offer: its points, its amount, and which rows are marked.</summary>
	[Theory]
	[MemberData(nameof(OfferFigureCases))]
	public void SelectionIsWorthTheOfferOfTheDesign(int index)
	{
		using var document = MarketVectors.Open("maker", "offer-figures.json");
		var testCase = document.RootElement.GetProperty("cases")[index];
		var (sortKey, ascending) = ReadSort(testCase.GetProperty("coinSort"));

		var figures = MakerCoins.GetOfferFigures(SampleCoins, ReadIds(testCase.GetProperty("selectedCoinIds")).ToHashSet(), sortKey, ascending);

		Assert.Equal(testCase.GetProperty("offerPointsShown").GetString(), figures.PointsText);
		Assert.Equal(testCase.GetProperty("offerAmountBtc").GetString(), figures.Amount > 0 ? MarketFormat.Btc(figures.Amount) : "");

		// A row takes the first mark that applies: no points, then a shared origin, then beyond the first three.
		var withoutPoints = figures.WithoutPoints;
		var sharedOrigin = figures.SharedOrigin.Except(withoutPoints).ToArray();
		var extra = figures.Extra.Except(withoutPoints).Except(sharedOrigin).ToArray();

		Assert.Equal(ReadIds(testCase.GetProperty("rowsYellowNoPoints")), withoutPoints.Order(StringComparer.Ordinal));
		Assert.Equal(ReadIds(testCase.GetProperty("rowsRedSharedOrigin")), sharedOrigin.Order(StringComparer.Ordinal));
		Assert.Equal(ReadIds(testCase.GetProperty("rowsOrangeBeyondThree")), extra.Order(StringComparer.Ordinal));
	}

	/// <summary>The size and the fee of the transaction that locks a bond, where a bond stands among the bonds of the sample book, and how a lock time is worded.</summary>
	[Fact]
	public void BondFiguresAreThoseOfTheDesign()
	{
		using var document = MarketVectors.Open("maker", "bond.json");

		var transactions = document.RootElement.GetProperty("bondTransaction").EnumerateArray().ToArray();
		Assert.Equal(5, transactions.Length);

		foreach (var testCase in transactions)
		{
			var estimate = BondFigures.EstimateTransaction(
				SampleMarket.ToSats(testCase.GetProperty("selectedTotalBtc").GetDecimal()),
				SampleMarket.ToSats(testCase.GetProperty("amountBtc").GetDecimal()),
				testCase.GetProperty("inputs").GetInt32(),
				testCase.GetProperty("feeRate").GetDecimal());

			Assert.Equal(testCase.GetProperty("changeOutput").GetBoolean(), estimate.HasChange);
			Assert.Equal(testCase.GetProperty("vbytes").GetInt32(), estimate.VirtualBytes);
			MarketVectors.AssertClose(testCase.GetProperty("feeSats").GetDecimal(), estimate.Fee, "The fee of the bonding transaction");
		}

		var standings = document.RootElement.GetProperty("standing").EnumerateArray().ToArray();
		Assert.Equal(6, standings.Length);

		foreach (var testCase in standings)
		{
			var standing = BondFigures.GetStanding(SampleMarket.ToSats(testCase.GetProperty("bondBtc").GetDecimal()), SampleMarket.Makers);

			Assert.Equal(testCase.GetProperty("rank").GetInt32(), standing.Rank);
			Assert.Equal(testCase.GetProperty("ranked").GetInt32(), standing.Ranked);
			Assert.Equal(testCase.GetProperty("top").GetInt32(), standing.TopPercent);
		}

		var durations = document.RootElement.GetProperty("durations").EnumerateArray().ToArray();
		Assert.Equal(11, durations.Length);

		foreach (var testCase in durations)
		{
			Assert.Equal(testCase.GetProperty("shown").GetString(), BondFigures.DescribeDuration(testCase.GetProperty("blocks").GetInt64()));
		}
	}

	/// <summary>An offer starts with the three largest private coins of different transactions. Coins that are in an offer already are passed over.</summary>
	[Fact]
	public void OfferStartsWithTheThreeLargestPrivateCoinsOfDifferentTransactions()
	{
		var coins = SampleCoins;

		// The two largest coins of the sample wallet come from the same transaction: only the larger one is taken.
		Assert.Equal(["13", "10", "2"], MakerCoins.GetDefaultOfferCoins(coins, new HashSet<string>(), CoinSortKey.Amount, ascending: false));

		// With those in an offer, the next three follow. Coin 9 shares its transaction with coin 2 and is free now.
		Assert.Equal(["1", "9", "3"], MakerCoins.GetDefaultOfferCoins(coins, new HashSet<string> { "13", "10", "2" }, CoinSortKey.Amount, ascending: false));

		// Sorted by points, the coins of the most points go first, the larger one of two with the same points.
		Assert.Equal(["10", "14", "8"], MakerCoins.GetDefaultOfferCoins(coins, new HashSet<string>(), CoinSortKey.Points, ascending: false));
	}

	/// <summary>
	/// A coin without anonymity points counts in an offer like any other: it is picked, it makes the amount, and it
	/// counts in the points of the offer with none. A bond takes private coins only.
	/// </summary>
	[Fact]
	public void CoinsWithoutPointsCountInAnOfferAndNotInABond()
	{
		MarketCoin[] coins =
		[
			new("none", "a", 500_000_000, 0),
			new("half", "b", 400_000_000, 0.5m),
			new("unknown", "c", 300_000_000, null),
			new("one", "d", 100_000_000, 1),
			new("many", "e", 50_000_000, 7.5m)
		];

		// The first three of the list, by amount or by points.
		Assert.Equal(["none", "half", "unknown"], MakerCoins.GetDefaultOfferCoins(coins, new HashSet<string>(), CoinSortKey.Amount, ascending: false));
		Assert.Equal(["many", "one", "half"], MakerCoins.GetDefaultOfferCoins(coins, new HashSet<string>(), CoinSortKey.Points, ascending: false));

		var figures = MakerCoins.GetOfferFigures(coins, coins.Select(coin => coin.Id).ToHashSet(), CoinSortKey.Amount, ascending: false);

		// The three largest make the amount: 5 + 4 + 3 BTC.
		Assert.Equal(1_200_000_000, figures.Amount);

		// A coin whose points are not known yet is taken as one without. Half a point is points.
		Assert.Equal(["none", "unknown"], figures.WithoutPoints);
		Assert.Equal(["one", "many"], figures.Extra);

		// No point on 5 BTC, half a point on 4 BTC, none on 3 BTC: 2 / 12.
		Assert.Equal("0.2", figures.PointsText);
		Assert.True(figures.HasPoints);

		// An offer of coins without points has none.
		var pointless = MakerCoins.GetOfferFigures(coins, new HashSet<string> { "none", "unknown" }, CoinSortKey.Amount, ascending: false);
		Assert.Equal(0m, pointless.Points);
		Assert.False(pointless.HasPoints);
		Assert.Equal("0.0", pointless.PointsText);
		Assert.Equal(800_000_000, pointless.Amount);

		// One coin with points among them is enough: (0 × 5 + 7.5 × 0.5) / 5.5.
		var mixed = MakerCoins.GetOfferFigures(coins, new HashSet<string> { "none", "many" }, CoinSortKey.Amount, ascending: false);
		Assert.Equal("0.7", mixed.PointsText);
		Assert.True(mixed.HasPoints);

		// The strategy picks for an offer among all coins: half of the 13.5 BTC, which the three largest cover.
		var offer = MakerCoins.Allocate(coins, new HashSet<string>(), 0, bondShare: 0.5, MakerTab.Offers, CoinSortKey.Amount, ascending: false);
		Assert.Equal(["none", "half", "unknown"], offer.CoinIds);
		Assert.Equal(675_000_000, offer.Amount);

		// And for a bond among the private ones.
		var bond = MakerCoins.Allocate(coins, new HashSet<string>(), 0, bondShare: 0.5, MakerTab.Bond, CoinSortKey.Amount, ascending: false);
		Assert.Equal(["one", "many"], bond.CoinIds);
	}

	/// <summary>A bond that exists fixes how the wallet is split, whatever the triangle says, and nothing more is to be locked.</summary>
	[Fact]
	public void ExistingBondFixesTheSplit()
	{
		MarketCoin[] coins =
		[
			new("bond", "a", 300_000_000, 0, IsBond: true),
			new("x", "b", 500_000_000, 4),
			new("y", "c", 200_000_000, 2)
		];

		var allocation = MakerCoins.Allocate(coins, new HashSet<string>(), 0, bondShare: 0.7, MakerTab.Offers, CoinSortKey.Amount, ascending: false);

		Assert.Equal(0.3, allocation.BondShare, 12);
		Assert.Equal(0, allocation.BondTarget, 6);
		Assert.Equal(700_000_000, allocation.OfferTarget, 3);
		Assert.Equal(["x", "y"], allocation.CoinIds);
		Assert.Equal(700_000_000, allocation.Amount);

		// What is offered already comes off what is still to be offered.
		var afterAnOffer = MakerCoins.Allocate(coins, new HashSet<string> { "x" }, offered: 500_000_000, bondShare: 0.7, MakerTab.Offers, CoinSortKey.Amount, ascending: false);
		Assert.Equal(["y"], afterAnOffer.CoinIds);
		Assert.Equal(200_000_000, afterAnOffer.Amount);
	}

	/// <summary>The sample data is the one of the design: the cases of the design are read off it, so not a byte of it may change.</summary>
	[Fact]
	public void SampleDataIsTheOneOfTheDesign()
	{
		Assert.Equal("49b6e332d0705e8b2ac26ee429fe0a36ba91104ca18ce4cd31ebc67fc6b67dfe", Hash("makers.json"));
		Assert.Equal("49b1a03c761295d188102e46bb1de3e4752c9300c7ddd277ca13b8855e52d1c3", Hash("utxos.json"));

		var makers = SampleMarket.Makers;
		Assert.Equal(250, makers.Count);
		Assert.Equal(216, makers.Count(maker => maker.IsBonded));
		Assert.Equal(28, makers.Count(maker => maker.Payments.Count > 0));
		Assert.Equal(39, makers.Count(maker => maker.ExtraInputs > 0));
		Assert.Equal(Enumerable.Range(1, 250).Select(id => (long)id), makers.Select(maker => maker.Id));
		Assert.All(makers, maker => Assert.InRange(maker.MatchedInputs, 1, 3));

		var coins = SampleMarket.Coins;
		Assert.Equal(16, coins.Count);
		Assert.Equal(582_541_186, coins.Sum(coin => coin.Amount));
		Assert.Equal(2, coins.GroupBy(coin => coin.TransactionId).Count(sameOrigin => sameOrigin.Count() == 2));
		Assert.Equal(14, coins.Select(coin => coin.TransactionId).Distinct().Count());

		Assert.Equal(2, SampleMarket.Taker.InputCount);
		Assert.Equal(10_000_000, SampleMarket.Taker.Amount);

		static string Hash(string name)
		{
			using var stream = SampleMarket.OpenResource(name);
			return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
		}
	}

	private static (CoinSortKey SortKey, bool Ascending) ReadSort(JsonElement sort) =>
		(sort[0].GetString() == "ap" ? CoinSortKey.Points : CoinSortKey.Amount, sort[1].GetString() == "asc");

	/// <summary>The numbers of coins, as the names that the coins of the sample wallet have here, in the order of their names.</summary>
	private static string[] ReadIds(JsonElement ids) =>
		[.. ids.EnumerateArray().Select(id => id.GetInt32().ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)];
}
