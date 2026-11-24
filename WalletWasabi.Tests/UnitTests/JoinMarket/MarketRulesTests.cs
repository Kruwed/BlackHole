using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.JoinMarket.Planning;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The rules of a taker's coinjoin with makers: what the outputs are worth, what is billed and to whom, and how
/// anonymity points are settled in sats. The expected numbers are those of the design.
/// </summary>
public class MarketRulesTests
{
	public static TheoryData<int> PointCases => MarketVectors.CaseIndexes("rules", "points.json");

	public static TheoryData<string> PlanFiles => MarketVectors.PlanFileData();

	/// <summary>The rule of the points by hand: what the outputs are worth, and what the transaction is worth with its matched inputs.</summary>
	[Theory]
	[MemberData(nameof(PointCases))]
	public void OutputsAreWorthWhatTheDesignSays(int index)
	{
		using var document = MarketVectors.Open("rules", "points.json");
		var testCase = document.RootElement.GetProperty("cases")[index];

		var outputPoints = AnonymityPoints.SumOutputPoints(MarketVectors.Longs(testCase.GetProperty("outputValues")));

		Assert.Equal(testCase.GetProperty("outputPoints").GetDecimal(), outputPoints);
		Assert.Equal(testCase.GetProperty("points").GetInt32(), MarketRules.GetPoints(testCase.GetProperty("matchedInputs").GetInt32(), (int)outputPoints));
	}

	/// <summary>The points of every plan of the design follow from the values of its outputs and the number of its matched inputs.</summary>
	[Theory]
	[MemberData(nameof(PlanFiles))]
	public void PointsOfAPlanFollowFromItsOutputValues(string file)
	{
		using var document = MarketVectors.Open("planner", file);
		var expected = document.RootElement.GetProperty("expected");

		var values = expected.GetProperty("tally").EnumerateArray()
			.SelectMany(entry => Enumerable.Repeat(entry[0].GetInt64(), entry[1].GetInt32()))
			.ToArray();

		var outputPoints = AnonymityPoints.SumOutputPoints(values);

		Assert.Equal(expected.GetProperty("outputPoints").GetDecimal(), outputPoints);
		Assert.Equal(expected.GetProperty("points").GetInt32(), MarketRules.GetPoints(expected.GetProperty("matchedInputs").GetInt32(), (int)outputPoints));
	}

	/// <summary>Every number of the fee table of every plan of the design follows from the counts of the plan.</summary>
	[Theory]
	[MemberData(nameof(PlanFiles))]
	public void FeesOfAPlanFollowFromItsCounts(string file)
	{
		using var document = MarketVectors.Open("planner", file);
		var expected = document.RootElement.GetProperty("expected");
		var expectedFees = expected.GetProperty("fees");

		var fees = MarketRules.GetFees(
			expected.GetProperty("feeRate").GetDecimal(),
			document.RootElement.GetProperty("input").GetProperty("taker").GetProperty("inputs").GetInt32(),
			expected.GetProperty("matchedInputs").GetInt32(),
			ReadCounts(expected.GetProperty("counts")),
			expectedFees.GetProperty("forfeitSats").GetInt64());

		AssertFees(expectedFees, fees);
	}

	/// <summary>The settlement of every plan of the design follows from its points and the rates and points of its makers, and so does what the taker pays in all.</summary>
	[Theory]
	[MemberData(nameof(PlanFiles))]
	public void SettlementOfAPlanFollowsFromItsPoints(string file)
	{
		using var document = MarketVectors.Open("planner", file);
		var expected = document.RootElement.GetProperty("expected");
		var (taker, makers) = MarketVectors.ReadPlanInput(document.RootElement.GetProperty("input"));

		var settlement = MarketRules.Settle(expected.GetProperty("points").GetInt32(), makers);
		AssertSettlement(expected.GetProperty("settlement"), settlement);

		var fees = MarketRules.GetFees(
			expected.GetProperty("feeRate").GetDecimal(),
			taker.InputCount,
			expected.GetProperty("matchedInputs").GetInt32(),
			ReadCounts(expected.GetProperty("counts")),
			expected.GetProperty("fees").GetProperty("forfeitSats").GetInt64());

		MarketVectors.AssertClose(expected.GetProperty("takerCostSats").GetDecimal(), MarketRules.GetTakerCost(fees, settlement), "What the taker pays in all");
	}

	/// <summary>The fee rate of a transaction is the highest of the lowest fee rates of its makers.</summary>
	[Fact]
	public void FeeRateIsTheHighestMinimumOfTheMakers()
	{
		Assert.Equal(1.0m, MarketRules.GetFeeRate([0.7m, 0.4m, 1.0m]));
		Assert.Equal(0.1m, MarketRules.GetFeeRate([0.1m]));
		Assert.Equal(0m, MarketRules.GetFeeRate([]));
	}

	[Theory]
	[InlineData(7, 8, 0, 0.6, -0.6)] // The maker had a point more than the transaction is worth: the taker pays its sell rate.
	[InlineData(7, 24, 1, 1.5, -25.5)]
	[InlineData(7, 4, 2, 3.7, 6)] // The maker gains three points and pays its buy rate for each.
	[InlineData(7, 7, 2, 3.7, 0)]
	[InlineData(1.5, 1, 2, 3, 1)] // Half a point is settled like any other part of a point.
	public void MakerBuysThePointsThatItGainsAndSellsTheOnesThatItLoses(decimal points, decimal originPoints, decimal buyRate, decimal sellRate, decimal expectedSats)
	{
		Assert.Equal(expectedSats, MarketRules.GetSettlement(points, originPoints, buyRate, sellRate));
	}

	/// <summary>
	/// The worked example of the rules: a taker with two inputs that hold 0.1 BTC, and three makers. The plan of the
	/// design gives the three makers and the taker ten outputs of four values.
	/// </summary>
	[Fact]
	public void WorkedExampleOfTheRules()
	{
		PlannedMaker[] makers =
		[
			PlannedMaker.FromInputCount("misty-wren", minFeeRate: 0.7m, buyRate: 0m, sellRate: 0.6m, points: 8, inputCount: 3, liquidity: 9_340_182, payments: []),
			PlannedMaker.FromInputCount("terse-badger", minFeeRate: 0.4m, buyRate: 1m, sellRate: 1.5m, points: 24, inputCount: 4, liquidity: 167_555_449, payments: []),
			PlannedMaker.FromInputCount("vivid-willow", minFeeRate: 1.0m, buyRate: 2m, sellRate: 3.7m, points: 4, inputCount: 3, liquidity: 155_172_250, payments: [])
		];

		// The highest of the lowest fee rates.
		var feeRate = MarketRules.GetFeeRate(makers.Select(maker => maker.MinFeeRate));
		Assert.Equal(1.0m, feeRate);

		// Three inputs of every maker count as matched. The fourth one of terse-badger is extra.
		var matchedInputs = makers.Sum(maker => maker.MatchedInputs);
		Assert.Equal(9, matchedInputs);
		Assert.Equal(1, makers.Sum(maker => maker.ExtraInputs));

		// The outputs of the plan: 0.1 BTC three times, 0.09340182 three times, 1.35832068 twice and 0.12383199 once.
		long[] outputs =
		[
			9_340_182,
			10_000_000, 9_340_182, 135_832_068,
			135_832_068, 10_000_000, 9_340_182, 12_383_199,
			10_000_000
		];

		// Two sets of three are worth three points each, the pair is worth one in all, and the output that is alone nothing.
		var outputPoints = (int)AnonymityPoints.SumOutputPoints(outputs);
		Assert.Equal(7, outputPoints);

		var points = MarketRules.GetPoints(matchedInputs, outputPoints);
		Assert.Equal(7, points);

		// Eight outputs share their value. One output of a maker is alone with its value.
		var counts = new PlanCounts(MatchedOutputs: 8, UnmatchedMakerOutputs: 1, UnmatchedTakerOutputs: 0, Payments: 0, MatchedPayments: 0, ExtraMakerInputs: 1);
		var fees = MarketRules.GetFees(feeRate, takerInputs: 2, matchedInputs, counts, forfeit: 0);

		// 11 + 2 x 57 + 9 x 57 + 8 x 43 + 100 = 1,082 virtual bytes to the taker, 57 of which go to terse-badger for its lone output.
		Assert.Equal(1082, fees.TakerVirtualBytes);
		Assert.Equal(1082m, fees.TakerFee);
		Assert.Equal(57m, fees.TakerToMakers);
		Assert.Equal(1025m, fees.TakerToMiners);

		// The extra input costs terse-badger 57 virtual bytes.
		Assert.Equal(57, fees.MakerVirtualBytes);
		Assert.Equal(57m, fees.MakerFee);
		Assert.Equal(1139m, fees.MiningFee);

		// misty-wren has 8 points against 7: the taker pays 1 x 0.6. terse-badger has 24: the taker pays 17 x 1.5.
		// vivid-willow has 4: it pays the taker 3 x 2.
		var settlement = MarketRules.Settle(points, makers);
		Assert.Equal([-0.6m, -25.5m, 6m], settlement.Rows.Select(row => row.Sats));
		Assert.Equal(6m, settlement.MakersPay);
		Assert.Equal(26.1m, settlement.TakerPays);
		Assert.Equal(-20.1m, settlement.Net);

		// 1,082 + 0 + 20.1.
		Assert.Equal(1102.1m, MarketRules.GetTakerCost(fees, settlement));
	}

	/// <summary>A pair is not an output that is alone: it is worth half a point each, and it is billed like any output that shares its value.</summary>
	[Fact]
	public void PairIsBilledLikeAnyOutputThatSharesItsValue()
	{
		var counts = new PlanCounts(MatchedOutputs: 2, UnmatchedMakerOutputs: 0, UnmatchedTakerOutputs: 0, Payments: 0, MatchedPayments: 0, ExtraMakerInputs: 0);
		var fees = MarketRules.GetFees(feeRate: 2m, takerInputs: 1, matchedInputs: 1, counts, forfeit: 0);

		Assert.Equal(2 * MarketRules.OutputVb, fees.Rows.Single(row => row.Rule == MarketRules.MatchedOutputsRule).VirtualBytes);
		Assert.Equal(0, fees.Rows.Single(row => row.Rule == MarketRules.UnmatchedOutputsRule).VirtualBytes);
		Assert.Equal(0m, fees.TakerToMakers);
	}

	/// <summary>The change of the taker is billed like an output that is alone with its value, and nobody is credited for it.</summary>
	[Fact]
	public void TakerChangeIsBilledWithoutACreditToAnybody()
	{
		var withChange = new PlanCounts(MatchedOutputs: 3, UnmatchedMakerOutputs: 0, UnmatchedTakerOutputs: 1, Payments: 0, MatchedPayments: 0, ExtraMakerInputs: 0);
		var fees = MarketRules.GetFees(feeRate: 1m, takerInputs: 2, matchedInputs: 3, withChange, forfeit: 0);

		var row = fees.Rows.Single(feeRow => feeRow.Rule == MarketRules.TakerChangeRule);
		Assert.Equal(MarketRules.UnmatchedOutputVb, row.VirtualBytes);
		Assert.Equal(FeePayer.Taker, row.PaidBy);
		Assert.Equal(0m, fees.TakerToMakers);
		Assert.Equal(fees.TakerFee, fees.TakerToMiners);

		// Without change the line is not in the table at all.
		var withoutChange = withChange with { UnmatchedTakerOutputs = 0 };
		Assert.DoesNotContain(MarketRules.GetFees(1m, 2, 3, withoutChange, 0).Rows, feeRow => feeRow.Rule == MarketRules.TakerChangeRule);
	}

	internal static PlanCounts ReadCounts(JsonElement counts) =>
		new(
			counts.GetProperty("matchedOutputs").GetInt32(),
			counts.GetProperty("makerUnmatchedOutputs").GetInt32(),
			counts.GetProperty("takerUnmatchedOutputs").GetInt32(),
			counts.GetProperty("payments").GetInt32(),
			counts.GetProperty("paymentsMatched").GetInt32(),
			counts.GetProperty("extraMakerInputs").GetInt32());

	internal static void AssertFees(JsonElement expected, FeeTable fees)
	{
		var expectedRows = expected.GetProperty("rows").EnumerateArray().ToArray();
		Assert.Equal(expectedRows.Length, fees.Rows.Count);

		for (var i = 0; i < expectedRows.Length; i++)
		{
			var row = fees.Rows[i];
			var rule = expectedRows[i].GetProperty("rule").GetString();

			Assert.Equal(rule, row.Rule);
			Assert.Equal(expectedRows[i].GetProperty("count").GetInt32(), row.Count);
			Assert.Equal(expectedRows[i].GetProperty("each").GetInt32(), row.Each);
			Assert.Equal(expectedRows[i].GetProperty("paidBy").GetString(), row.PaidBy == FeePayer.Taker ? "taker" : "maker");
			Assert.Equal(expectedRows[i].GetProperty("vb").GetInt32(), row.VirtualBytes);
			MarketVectors.AssertClose(expectedRows[i].GetProperty("sats").GetDecimal(), row.GetSats(fees.FeeRate), $"The sats of '{rule}'");
		}

		Assert.Equal(expected.GetProperty("totalVb").GetInt32(), fees.TotalVirtualBytes);
		Assert.Equal(expected.GetProperty("makerVb").GetInt32(), fees.MakerVirtualBytes);
		Assert.Equal(expected.GetProperty("takerVb").GetInt32(), fees.TakerVirtualBytes);
		Assert.Equal(expected.GetProperty("forfeitSats").GetInt64(), fees.Forfeit);
		MarketVectors.AssertClose(expected.GetProperty("takerFeeSats").GetDecimal(), fees.TakerFee, "The fee of the taker");
		MarketVectors.AssertClose(expected.GetProperty("makerFeeSats").GetDecimal(), fees.MakerFee, "The fee of the makers");
		MarketVectors.AssertClose(expected.GetProperty("miningFeeSats").GetDecimal(), fees.MiningFee, "The mining fee");
		MarketVectors.AssertClose(expected.GetProperty("minerSats").GetDecimal(), fees.TakerToMiners, "What the taker pays the miners");
		MarketVectors.AssertClose(expected.GetProperty("makerCreditSats").GetDecimal(), fees.TakerToMakers, "What the taker pays the makers for lone outputs");
	}

	internal static void AssertSettlement(JsonElement expected, Settlement settlement)
	{
		var expectedRows = expected.GetProperty("rows").EnumerateArray().ToArray();
		Assert.Equal(expectedRows.Length, settlement.Rows.Count);

		for (var i = 0; i < expectedRows.Length; i++)
		{
			var row = settlement.Rows[i];
			var alias = expectedRows[i].GetProperty("alias").GetString();

			Assert.Equal(alias, row.Alias);
			Assert.Equal(expectedRows[i].GetProperty("originPoints").GetDecimal(), row.OriginPoints);
			Assert.Equal(expectedRows[i].GetProperty("delta").GetDecimal(), row.Delta);

			var rate = expectedRows[i].GetProperty("rate");
			Assert.Equal(rate.ValueKind == JsonValueKind.Null ? null : rate.GetDecimal(), row.Rate);

			MarketVectors.AssertClose(expectedRows[i].GetProperty("sats").GetDecimal(), row.Sats, $"What {alias} pays");
		}

		MarketVectors.AssertClose(expected.GetProperty("makersPaySats").GetDecimal(), settlement.MakersPay, "What the makers pay");
		MarketVectors.AssertClose(expected.GetProperty("takerPaysSats").GetDecimal(), settlement.TakerPays, "What the taker pays");
		MarketVectors.AssertClose(expected.GetProperty("netSats").GetDecimal(), settlement.Net, "The net settlement");
	}
}
