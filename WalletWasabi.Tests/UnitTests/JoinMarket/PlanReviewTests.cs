using System.Linq;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The plan of a transaction as the taker's side of the market shows it: the figures and the output values of the
/// Transaction Builder, and the lines of the Price Calculator.
/// </summary>
public class PlanReviewTests
{
	/// <summary>
	/// A taker with 0.09 BTC in two coins and one maker with 0.045 BTC in one coin of 1.5 points, at 1.5 sat/vB, a
	/// sell rate of 300 and a buy rate of 120. The two share the values 0.04 and 0.005, and the taker keeps a rest.
	/// </summary>
	private static readonly PlanReview Small = PlanReview.Create(
		TransactionPlanner.Plan(
			new PlannedTaker(InputCount: 2, Amount: 9_000_000),
			[new PlannedMaker("gilded-raven", MinFeeRate: 1.5m, BuyRate: 120m, SellRate: 300m, Points: 1.5m, MatchedInputs: 1, ExtraInputs: 0, Liquidity: 4_500_000, Payments: [])]),
		liquidity: 4_500_000);

	/// <summary>A value is white when three or more outputs have it, yellow for a pair and red when one output is alone with it.</summary>
	[Theory]
	[InlineData(1, ValueKind.Alone)]
	[InlineData(2, ValueKind.Pair)]
	[InlineData(3, ValueKind.SetOfThree)]
	[InlineData(48, ValueKind.SetOfThree)]
	public void ValueIsColouredByHowManyOutputsHaveIt(int count, ValueKind expected)
	{
		Assert.Equal(expected, PlanReview.GetKind(count));

		var row = new PlanOutputRow(1_000, count, "0.00001000", $"× {count}", "", PlanReview.GetKind(count));
		Assert.Equal(expected == ValueKind.Pair, row.IsPair);
		Assert.Equal(expected == ValueKind.Alone, row.IsAlone);
	}

	[Fact]
	public void BuilderTellsTheFiguresOfTheTransaction()
	{
		Assert.True(Small.HasMakers);
		Assert.Equal("1", Small.Points);
		Assert.Equal("2", Small.Participants);
		Assert.Equal("0.05", Small.Liquidity);

		// One input of the maker counts, and two pairs of outputs are worth half a point each.
		Assert.Equal("1", Small.MatchedIn);
		Assert.Equal("2", Small.MatchedOut);
		Assert.Equal("1", Small.UnmatchedOut);
	}

	/// <summary>The values of the outputs, the largest first, with how many outputs have each and whose they are.</summary>
	[Fact]
	public void BuilderListsTheValuesOfTheOutputsWithTheirOwners()
	{
		Assert.Equal(
			[
				new PlanOutputRow(4_500_000, 1, "0.04500000", "× 1", "Taker change", ValueKind.Alone),
				new PlanOutputRow(4_000_000, 2, "0.04000000", "× 2", "1 Maker · 1 Taker", ValueKind.Pair),
				new PlanOutputRow(500_000, 2, "0.00500000", "× 2", "1 Maker · 1 Taker", ValueKind.Pair)
			],
			Small.Outputs);
	}

	[Fact]
	public void BuilderOfTheWholeSelectionOfTheDesignHasSetsAndPairs()
	{
		var desk = new TakerDesk(SampleMarket.Makers, []);
		var selection = desk.GetSelection(desk.ToggleAll(desk.Open()), SampleMarket.Taker);
		var plan = TransactionPlanner.Plan(selection.Taker!, [.. selection.Makers.Select(maker => maker.ToPlannedMaker())]);
		var review = PlanReview.Create(plan, selection.Liquidity);

		Assert.Equal(("113", "49", "76.79"), (review.Points, review.Participants, review.Liquidity));
		Assert.Equal(review.Outputs.Select(row => row.Value).OrderDescending(), review.Outputs.Select(row => row.Value));
		Assert.All(review.Outputs, row => Assert.Equal(PlanReview.GetKind(row.Count), row.Kind));
		Assert.All(review.Outputs, row => Assert.Equal($"× {row.Count}", row.CountText));
		Assert.Contains(review.Outputs, row => row.Kind == ValueKind.SetOfThree);
		Assert.Contains(review.Outputs, row => row.Kind == ValueKind.Pair);

		// Every output of the transaction is in the list once.
		Assert.Equal(
			plan.Makers.Sum(maker => maker.Outputs.Count() + maker.Payments.Count) + plan.Taker.Outputs.Count(),
			review.Outputs.Sum(row => row.Count));
	}

	/// <summary>
	/// The lines of the fees, in the order of the rule book. The rest that the taker keeps has a line of its own, which
	/// is only there when there is such a rest. What the taker pays is red on the screen, and what the makers pay green.
	/// </summary>
	[Fact]
	public void CalculatorListsWhatIsBilledAndToWhom()
	{
		Assert.Equal(
			["Transaction overhead", "Taker inputs", "Taker change", "Matched inputs", "Matched outputs", "Unmatched outputs", "Extra Maker inputs", "Extra Maker outputs"],
			Small.FeeRows.Select(row => row.Rule));

		// Every line but the first tells what one of its things is billed, and how many of them there are.
		Assert.Equal(
			["", "57 vB × 2", "100 vB × 1", "57 vB × 1", "43 vB × 4", "100 vB × 0", "57 vB × 0", "43 vB × 0"],
			Small.FeeRows.Select(row => row.Detail));

		// At 1.5 sat/vB.
		Assert.Equal(["11 vB", "114 vB", "100 vB", "57 vB", "172 vB", "0 vB", "0 vB", "0 vB"], Small.FeeRows.Select(row => row.VirtualBytes));
		Assert.Equal(["17", "171", "150", "86", "258", "0", "0", "0"], Small.FeeRows.Select(row => row.Sats));
		Assert.Equal([false, false, false, false, false, false, true, true], Small.FeeRows.Select(row => row.IsPaidByMakers));

		// A taker without a rest has no such line.
		var whole = PlanReview.Create(TransactionPlanner.Plan(SampleMarket.Taker, []), liquidity: 0);
		Assert.DoesNotContain(whole.FeeRows, row => row.Rule == "Taker change" && row.Detail.EndsWith("× 0", System.StringComparison.Ordinal));

		Assert.Equal("1.5", Small.MiningFeeRate);
		Assert.Equal("454 vB", Small.TotalVirtualBytes);
		Assert.Equal("681", Small.MiningFee);
	}

	/// <summary>
	/// The maker had 1.5 points and has 1 afterwards: it loses half a point, and the taker pays its sell rate for that,
	/// 150 sats. A difference that is not whole is written with its half.
	/// </summary>
	[Fact]
	public void CalculatorSettlesThePointsOfEveryMaker()
	{
		var line = Assert.Single(Small.Settlement);

		Assert.Equal(new PlanSettlementRow("gilded-raven", "1.5", "−0.5", "300", -1, "−150"), line);
		Assert.True(line.IsSelling);
		Assert.False(line.IsBuying);

		Assert.Equal(("150", "0", "−150"), (Small.TakerPays, Small.MakersPay, Small.NetSettlement));
		Assert.Equal("Total you pay", Small.TotalLabel);
	}

	/// <summary>
	/// A maker without points sells at 0 and has nothing to lose: whatever the transaction is worth, it gains that, and
	/// pays its buy rate for it.
	/// </summary>
	[Fact]
	public void MakerWithoutPointsPaysForThePointsThatItGains()
	{
		var review = PlanReview.Create(
			TransactionPlanner.Plan(
				new PlannedTaker(InputCount: 2, Amount: 9_000_000),
				[new PlannedMaker("plain-otter", MinFeeRate: 1.5m, BuyRate: 120m, SellRate: 0m, Points: 0m, MatchedInputs: 1, ExtraInputs: 0, Liquidity: 4_500_000, Payments: [])]),
			liquidity: 4_500_000);

		var line = Assert.Single(review.Settlement);

		Assert.Equal("1", review.Points);
		Assert.Equal(new PlanSettlementRow("plain-otter", "0", "+1", "120", 1, "+120"), line);
		Assert.True(line.IsBuying);
		Assert.Equal(("0", "120", "+120"), (review.TakerPays, review.MakersPay, review.NetSettlement));
	}

	[Theory]
	[InlineData("0.5", "0.5")]
	[InlineData("2", "2")]
	[InlineData("0", "0")]
	[InlineData("-1.3", "−1.3")]
	[InlineData("-100", "−100")]
	[InlineData("-0.04", "0")]
	public void RateBelowZeroIsWrittenWithItsSign(string rate, string expected)
	{
		Assert.Equal(expected, MarketFormat.Rate(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)));
	}

	[Theory]
	[InlineData("-1.3", "-1.3")]
	[InlineData("−1.3", "-1.3")]
	[InlineData(" 2.5 ", "2.5")]
	[InlineData("0", "0")]
	public void LimitOfTheSpreadCanBeTypedBelowZero(string typed, string expected)
	{
		Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MarketFormat.ParseSignedNumber(typed));
		Assert.Null(MarketFormat.ParseSignedNumber("cheap"));

		// The other limits are not below zero.
		Assert.Equal(typed.Contains('-') || typed.Contains('−'), MarketFormat.ParseNumber(typed) is null);
	}

	[Theory]
	[InlineData("56", "+56")]
	[InlineData("-56", "−56")]
	[InlineData("0.5", "+0.5")]
	[InlineData("-0.5", "−0.5")]
	[InlineData("104.5645", "+104.6")]
	[InlineData("1250", "+1,250")]
	[InlineData("0", "0")]
	[InlineData("-0.04", "0")]
	public void DifferenceOfPointsIsWrittenWithItsSignAndItsHalf(string points, string expected)
	{
		Assert.Equal(expected, MarketFormat.SignedPoints(decimal.Parse(points, System.Globalization.CultureInfo.InvariantCulture)));
	}

	/// <summary>A taker that picked nobody has a transaction of its own, which mints nothing and settles nothing.</summary>
	[Fact]
	public void TakerAloneHasNothingToSettle()
	{
		var review = PlanReview.Create(TransactionPlanner.Plan(SampleMarket.Taker, []), liquidity: 0);

		Assert.False(review.HasMakers);
		Assert.Equal(("0", "1", "0.00"), (review.Points, review.Participants, review.Liquidity));
		Assert.Empty(review.Settlement);
		Assert.Equal(MarketFormat.None, review.MiningFeeRate);
		Assert.Equal(("0", "0", "0"), (review.TakerPays, review.MakersPay, review.NetSettlement));
	}
}
