using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The order book of the takers on the sample book: how makers are ranked, what the filters cut and what the strategy
/// triangle of a taker sets. The expected results were read off the prototype of the design.
/// </summary>
public class OrderBookRulesTests
{
	public static TheoryData<int> FilterCases => MarketVectors.CaseIndexes("order-book", "filters.json");

	public static TheoryData<int> StrategyCases => MarketVectors.CaseIndexes("order-book", "taker-strategy.json");

	/// <summary>The standing of every maker of the sample book on every criterion, its score and its rank.</summary>
	[Fact]
	public void MakersAreRankedAsInTheDesign()
	{
		using var document = MarketVectors.Open("order-book", "ranking.json");
		var expected = document.RootElement.GetProperty("makers").EnumerateArray().ToArray();
		var makers = SampleMarket.Makers;

		// The criteria and their weights are listed in the order of the design.
		Assert.Equal(
			document.RootElement.GetProperty("criteria").EnumerateArray().Select(key => MarketVectors.ReadCriterion(key.GetString()!)),
			BookRules.Criteria);
		Assert.Equal(MarketVectors.Ints(document.RootElement.GetProperty("weights")), BookRules.RankWeights);

		var rankings = BookRules.Rank(makers);

		Assert.Equal(250, expected.Length);
		Assert.Equal(expected.Length, rankings.Length);

		for (var i = 0; i < expected.Length; i++)
		{
			var id = expected[i].GetProperty("id").GetInt64();
			var standings = expected[i].GetProperty("standings").EnumerateArray().Select(standing => standing.GetDouble()).ToArray();

			Assert.Equal(id, makers[i].Id);
			Assert.Equal(id, rankings[i].Id);

			for (var criterion = 0; criterion < standings.Length; criterion++)
			{
				MarketVectors.AssertClose(standings[criterion], rankings[i].Standings[criterion], $"The standing of maker {id} on {BookRules.Criteria[criterion]}");
			}

			MarketVectors.AssertClose(expected[i].GetProperty("score").GetDouble(), rankings[i].Score, $"The score of maker {id}");
			Assert.Equal(expected[i].GetProperty("rank").GetInt32(), rankings[i].Rank);
		}
	}

	/// <summary>The limit that every position of a filter stands for, and the position that a typed limit lands on.</summary>
	[Theory]
	[MemberData(nameof(FilterCases))]
	public void FilterCutsTheBookAsInTheDesign(int index)
	{
		using var document = MarketVectors.Open("order-book", "filters.json");
		var testCase = document.RootElement.GetProperty("cases")[index];
		var criterion = MarketVectors.ReadCriterion(testCase.GetProperty("criterion").GetString()!);
		var values = BookRules.SortValues(SampleMarket.Makers, criterion);
		var tick = testCase.GetProperty("tick").GetInt32();

		if (testCase.TryGetProperty("typedValue", out var typed))
		{
			Assert.Equal(tick, BookRules.GetTick(values, BookRules.IsLowerBetter(criterion), typed.GetDecimal()));
		}
		else
		{
			var cutoff = testCase.GetProperty("cutoff");

			Assert.Equal(cutoff.ValueKind == JsonValueKind.Null ? null : cutoff.GetDecimal(), BookRules.GetCutoff(values, tick));
		}
	}

	/// <summary>
	/// What a point of the triangle of a taker makes of the filters, and which makers pass them. The first case is
	/// the middle of the triangle, which the screen opens with: it admits 48 of the 250 makers.
	/// </summary>
	[Theory]
	[MemberData(nameof(StrategyCases))]
	public void TakerStrategySetsTheFiltersAsInTheDesign(int index)
	{
		using var document = MarketVectors.Open("order-book", "taker-strategy.json");
		var testCase = document.RootElement.GetProperty("cases")[index];
		var makers = SampleMarket.Makers;

		var weights = testCase.GetProperty("weights").EnumerateArray().Select(weight => weight.GetDouble()).ToArray();
		var filters = TakerStrategy.GetFilters(makers, new TriangleWeights(weights[0], weights[1], weights[2]), MarketVectors.Ints(testCase.GetProperty("ticksBefore")));

		Assert.Equal(MarketVectors.Ints(testCase.GetProperty("ticks")), filters.Ticks);
		Assert.Equal(
			testCase.GetProperty("cutoffs").EnumerateArray().Select(cutoff => cutoff.ValueKind == JsonValueKind.Null ? (decimal?)null : cutoff.GetDecimal()),
			filters.Cutoffs);

		var passing = makers.Where(maker => BookRules.Passes(maker, filters.Cutoffs)).ToArray();

		Assert.Equal(testCase.GetProperty("passingIds").EnumerateArray().Select(id => id.GetInt64()), passing.Select(maker => maker.Id));
		Assert.Equal(testCase.GetProperty("makersPassing").GetInt32(), passing.Length);
		Assert.Equal(testCase.GetProperty("liquidityPassing").GetString(), MarketFormat.Btc(passing.Sum(maker => maker.Liquidity), decimals: 2));
	}

	/// <summary>The screen of the taker opens at the middle of the triangle, where 48 of the 250 makers of the sample book pass, with 76.79 of 418.91 BTC.</summary>
	[Fact]
	public void MiddleOfTheTriangleAdmitsFortyEightMakersOfTheSampleBook()
	{
		var makers = SampleMarket.Makers;
		var filters = TakerStrategy.GetFilters(makers, TriangleWeights.Centre, currentTicks: null);
		var passing = makers.Where(maker => BookRules.Passes(maker, filters.Cutoffs)).ToArray();

		Assert.Equal(48, passing.Length);
		Assert.Equal("76.79", MarketFormat.Btc(passing.Sum(maker => maker.Liquidity), decimals: 2));
		Assert.Equal("418.91", MarketFormat.Btc(makers.Sum(maker => maker.Liquidity), decimals: 2));
	}

	/// <summary>The triangle never touches the liquidity: the taker sets that filter by hand, and it stays where it was.</summary>
	[Fact]
	public void TriangleLeavesTheLiquidityFilterAlone()
	{
		var liquidity = BookRules.IndexOf(BookCriterion.Liquidity);
		var ticks = new int[BookRules.Criteria.Count];
		ticks[liquidity] = 120;

		var filters = TakerStrategy.GetFilters(SampleMarket.Makers, new TriangleWeights(1, 0, 0), ticks);

		Assert.Equal(120, filters.Ticks[liquidity]);
		Assert.NotNull(filters.Cutoffs[liquidity]);
	}

	/// <summary>The spread has no share of its own: its limit is the limit of the sell rate less the limit of the buy rate, so it admits whoever the two rates admit.</summary>
	[Fact]
	public void SpreadFollowsTheTwoRates()
	{
		var makers = SampleMarket.Makers;

		foreach (var weights in (TriangleWeights[])[new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), TriangleWeights.Centre, new(0.2, 0.5, 0.3)])
		{
			var filters = TakerStrategy.GetFilters(makers, weights, null);
			var sell = filters.Cutoffs[BookRules.IndexOf(BookCriterion.SellRate)]!.Value;
			var buy = filters.Cutoffs[BookRules.IndexOf(BookCriterion.BuyRate)]!.Value;

			Assert.Equal(sell - buy, filters.Cutoffs[BookRules.IndexOf(BookCriterion.Spread)]);

			var withoutSpread = filters.Cutoffs.ToArray();
			withoutSpread[BookRules.IndexOf(BookCriterion.Spread)] = null;

			Assert.Equal(
				makers.Where(maker => BookRules.Passes(maker, withoutSpread)).Select(maker => maker.Id),
				makers.Where(maker => BookRules.Passes(maker, filters.Cutoffs)).Select(maker => maker.Id));
		}
	}

	/// <summary>A book without bonded makers gives the triangle nothing to measure by: it sets no filter then.</summary>
	[Fact]
	public void TriangleSetsNothingWithoutBondedMakers()
	{
		var unbonded = SampleMarket.Makers.Where(maker => !maker.IsBonded).ToArray();
		var filters = TakerStrategy.GetFilters(unbonded, TriangleWeights.Centre, null);

		Assert.All(filters.Ticks, tick => Assert.Equal(0, tick));
		Assert.All(filters.Cutoffs, cutoff => Assert.Null(cutoff));

		var empty = TakerStrategy.GetFilters([], TriangleWeights.Centre, null);
		Assert.All(empty.Cutoffs, cutoff => Assert.Null(cutoff));
	}

	/// <summary>A position in the drawing of the triangle gives three weights. A position outside counts as the nearest way into the triangle.</summary>
	[Fact]
	public void PositionInTheTriangleGivesTheWeightsOfTheDesign()
	{
		using var document = MarketVectors.Open("order-book", "taker-strategy.json");
		var cases = document.RootElement.GetProperty("trianglePoints").GetProperty("cases").EnumerateArray().ToArray();

		Assert.Equal(12, cases.Length);

		foreach (var testCase in cases)
		{
			var expected = testCase.GetProperty("weights").EnumerateArray().Select(weight => weight.GetDouble()).ToArray();
			var weights = StrategyTriangle.GetWeights(testCase.GetProperty("x").GetDouble(), testCase.GetProperty("y").GetDouble());

			Assert.Equal(expected, new[] { weights.Top, weights.Left, weights.Right });
		}
	}

	[Fact]
	public void MarkerOfTheTriangleIsDrawnWhereItsWeightsPutIt()
	{
		Assert.Equal(StrategyTriangle.Top, StrategyTriangle.GetPosition(new TriangleWeights(1, 0, 0)));
		Assert.Equal(StrategyTriangle.Left, StrategyTriangle.GetPosition(new TriangleWeights(0, 1, 0)));
		Assert.Equal(StrategyTriangle.Right, StrategyTriangle.GetPosition(new TriangleWeights(0, 0, 1)));

		var (x, y) = StrategyTriangle.GetPosition(TriangleWeights.Centre);
		Assert.Equal(130, x, 9);
		Assert.Equal(136, y, 9);
	}

	[Fact]
	public void WeightsAreSpelledOutWithoutTheCornersThatCountNothing()
	{
		Assert.Equal(
			"33% Privacy · 33% Costs · 33% Speed",
			StrategyTriangle.Describe(TriangleWeights.Centre, "Privacy", "Costs", "Speed"));
		Assert.Equal(
			"50% AP · 50% Remix",
			StrategyTriangle.Describe(new TriangleWeights(0.5, 0, 0.5), "AP", "Sats", "Remix"));
		Assert.Equal("100% Costs", StrategyTriangle.Describe(new TriangleWeights(0, 1, 0), "Privacy", "Costs", "Speed"));
	}

	/// <summary>A maker passes when it is at least as good as every limit that is set: not above where lower is better, not below otherwise.</summary>
	[Fact]
	public void MakerPassesWhenItIsAsGoodAsEveryLimit()
	{
		var maker = SampleMarket.Makers.Single(candidate => candidate.Id == 1);
		var cutoffs = new decimal?[BookRules.Criteria.Count];

		Assert.True(BookRules.Passes(maker, cutoffs));

		cutoffs[BookRules.IndexOf(BookCriterion.FeeRate)] = maker.MinFeeRate;
		cutoffs[BookRules.IndexOf(BookCriterion.BuyRate)] = maker.BuyRate;
		cutoffs[BookRules.IndexOf(BookCriterion.Bond)] = maker.GetValue(BookCriterion.Bond);
		Assert.True(BookRules.Passes(maker, cutoffs));

		cutoffs[BookRules.IndexOf(BookCriterion.BuyRate)] = maker.BuyRate + 0.1m;
		Assert.False(BookRules.Passes(maker, cutoffs));

		cutoffs[BookRules.IndexOf(BookCriterion.BuyRate)] = null;
		cutoffs[BookRules.IndexOf(BookCriterion.FeeRate)] = maker.MinFeeRate - 0.05m;
		Assert.False(BookRules.Passes(maker, cutoffs));
	}

	/// <summary>A book of one maker has a best maker and no worst one: it stands at the top of every criterion.</summary>
	[Fact]
	public void MakerThatIsAloneInTheBookStandsAtTheTop()
	{
		var maker = SampleMarket.Makers[0];
		var ranking = Assert.Single(BookRules.Rank([maker]));

		Assert.All(ranking.Standings, standing => Assert.Equal(1, standing));
		Assert.Equal(1, ranking.Score);
		Assert.Equal(1, ranking.Rank);
	}

	/// <summary>An offer of this wallet is not in the book that it is measured against: it stands by how many makers of the book are better than it.</summary>
	[Fact]
	public void OwnOfferStandsByTheMakersThatAreBetter()
	{
		var makers = SampleMarket.Makers;
		var sorted = BookRules.Criteria.Select(criterion => BookRules.SortValues(makers, criterion)).ToArray();

		var best = new BookMaker(100_001, "own", MinFeeRate: 0.1m, BuyRate: 100m, SellRate: 0m, Spread: -100m, Bond: 10_000_000_000, Liquidity: 100_000_000_000, 3, 0, 5, [], IsOwn: true);
		Assert.All(BookRules.GetStandingsAgainst(best, sorted), standing => Assert.Equal(1, standing));

		var worst = new BookMaker(100_002, "own", MinFeeRate: 100m, BuyRate: 0m, SellRate: 1000m, Spread: 1000m, Bond: 0, Liquidity: 1, 3, 0, 5, [], IsOwn: true);
		var standings = BookRules.GetStandingsAgainst(worst, sorted);

		Assert.Equal(0, standings[BookRules.IndexOf(BookCriterion.FeeRate)]);
		Assert.Equal(0, standings[BookRules.IndexOf(BookCriterion.SellRate)]);
		Assert.Equal(0, standings[BookRules.IndexOf(BookCriterion.Liquidity)]);

		// Nobody of the book pays less than nothing for a point, and 34 makers have no bond either.
		Assert.True(standings[BookRules.IndexOf(BookCriterion.Bond)] > 0);
	}

	[Theory]
	[InlineData(0.5, 1)]
	[InlineData(1.5, 2)]
	[InlineData(2.5, 3)]
	[InlineData(2.4999, 2)]
	[InlineData(0, 0)]
	[InlineData(151.2, 151)]
	public void HalvesAreRoundedUp(double value, double expected)
	{
		Assert.Equal(expected, BookRules.RoundHalfUp(value));
	}

	[Fact]
	public void NumbersOfTheMarketAreWrittenAsInTheDesign()
	{
		Assert.Equal("0.10000000", MarketFormat.Btc(10_000_000));
		Assert.Equal("1.35832068", MarketFormat.Btc(135_832_068));
		Assert.Equal("—", MarketFormat.BtcOrNone(0));
		Assert.Equal("0.34708409", MarketFormat.BtcOrNone(34_708_409));

		Assert.Equal("0.1", MarketFormat.FeeRate(0.1m));
		Assert.Equal("1.0", MarketFormat.FeeRate(1m));
		Assert.Equal("12.5", MarketFormat.FeeRate(12.5m));

		Assert.Equal("0", MarketFormat.Rate(0m));
		Assert.Equal("2", MarketFormat.Rate(2.0m));
		Assert.Equal("3.7", MarketFormat.Rate(3.7m));
		Assert.Equal("0.8", MarketFormat.Rate(0.75m));

		Assert.Equal("1,082", MarketFormat.Whole(1082m));
		Assert.Equal("1,102", MarketFormat.Whole(1102.1m));
		Assert.Equal("26", MarketFormat.Whole(25.5m));
		Assert.Equal("0", MarketFormat.Whole(0.4m));

		Assert.Equal("+6", MarketFormat.Signed(6m));
		Assert.Equal("−26", MarketFormat.Signed(-25.5m));
		Assert.Equal("0", MarketFormat.Signed(0m));
		Assert.Equal("−17", MarketFormat.Signed(-17m));

		Assert.Equal("0.9 sat/vB", MarketFormat.Criterion(BookCriterion.FeeRate, 0.9m));
		Assert.Equal("0.26305214 BTC", MarketFormat.Criterion(BookCriterion.Bond, 0.26305214m));
		Assert.Equal("— BTC", MarketFormat.Criterion(BookCriterion.Bond, 0m));
		Assert.Equal("4.10483747 BTC", MarketFormat.Criterion(BookCriterion.Liquidity, 4.10483747m));
		Assert.Equal("2.5 sats", MarketFormat.Criterion(BookCriterion.Spread, 2.5m));
		Assert.Equal("1 sat", MarketFormat.Criterion(BookCriterion.Spread, 1m));
		Assert.Equal("3.3 sats/AP", MarketFormat.Criterion(BookCriterion.SellRate, 3.3m));
		Assert.Equal("1 sat/AP", MarketFormat.Criterion(BookCriterion.BuyRate, 1m));
	}

	[Fact]
	public void TypedNumbersAreRead()
	{
		Assert.Equal(10_000_000, MarketFormat.ParseBtc("0.1"));
		Assert.Equal(291_270_593, MarketFormat.ParseBtc(" 2.91270593 "));
		Assert.Equal(0, MarketFormat.ParseBtc("0"));
		Assert.Null(MarketFormat.ParseBtc("0.123456789"));
		Assert.Null(MarketFormat.ParseBtc(""));
		Assert.Null(MarketFormat.ParseBtc("1,5"));
		Assert.Null(MarketFormat.ParseBtc("-1"));
		Assert.Null(MarketFormat.ParseBtc("1e3"));

		Assert.Equal(1.5m, MarketFormat.ParseNumber("1.5"));
		Assert.Equal(0m, MarketFormat.ParseNumber("0"));
		Assert.Null(MarketFormat.ParseNumber("abc"));
		Assert.Null(MarketFormat.ParseNumber("-2"));
	}
}
