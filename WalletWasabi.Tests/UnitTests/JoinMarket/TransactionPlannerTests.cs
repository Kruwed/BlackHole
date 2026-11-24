using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The planner of a taker's coinjoin against the whole plans of the design, and against the rules that every plan keeps.
/// </summary>
public class TransactionPlannerTests
{
	/// <summary>
	/// How many sats beyond the limit a forfeit may be: where the last leftover is matched, what does not divide by the
	/// length of the chain that the shift travels is forfeited as well.
	/// </summary>
	private const long ForfeitSlack = 64;

	public static TheoryData<string> PlanFiles => MarketVectors.PlanFileData();

	/// <summary>
	/// Every plan of the design is reproduced exactly: the outputs of every participant in the order in which they were
	/// chosen, the remainders, what is forfeited, and everything that follows from them.
	/// </summary>
	[Theory]
	[MemberData(nameof(PlanFiles))]
	public void PlanIsTheOneOfTheDesign(string file)
	{
		using var document = MarketVectors.Open("planner", file);
		var (taker, makers) = MarketVectors.ReadPlanInput(document.RootElement.GetProperty("input"));
		var expected = document.RootElement.GetProperty("expected");

		var plan = TransactionPlanner.Plan(taker, makers);

		Assert.Equal(expected.GetProperty("feeRate").GetDecimal(), plan.FeeRate);
		Assert.Equal(expected.GetProperty("matchedInputs").GetInt32(), plan.MatchedInputs);
		Assert.Equal(expected.GetProperty("outputPoints").GetInt32(), plan.OutputPoints);
		Assert.Equal(expected.GetProperty("points").GetInt32(), plan.Points);
		Assert.Equal(expected.GetProperty("participants").GetInt32(), plan.Participants);

		var expectedMakers = expected.GetProperty("makers").EnumerateArray().ToArray();
		Assert.Equal(expectedMakers.Length, plan.Makers.Count);

		for (var i = 0; i < expectedMakers.Length; i++)
		{
			var maker = plan.Makers[i];
			var alias = expectedMakers[i].GetProperty("alias").GetString();

			Assert.Equal(alias, maker.Alias);
			Assert.Equal(expectedMakers[i].GetProperty("countedInputs").GetInt32(), maker.CountedInputs);
			Assert.Equal(expectedMakers[i].GetProperty("amountSats").GetInt64(), maker.Amount);
			Assert.True(MarketVectors.Longs(expectedMakers[i].GetProperty("picks")).SequenceEqual(maker.Picks), $"The outputs of {alias} are {string.Join(", ", maker.Picks)}.");
			Assert.Equal(expectedMakers[i].GetProperty("change").GetInt64(), maker.Change);
			Assert.Equal(expectedMakers[i].GetProperty("forfeit").GetInt64(), maker.Forfeit);
			Assert.True(MarketVectors.Longs(expectedMakers[i].GetProperty("pinned")).SequenceEqual(maker.Pinned), $"The pinned outputs of {alias} are {string.Join(", ", maker.Pinned)}.");
			Assert.Equal(MarketVectors.Longs(expectedMakers[i].GetProperty("payments")), maker.Payments);
		}

		var expectedTaker = expected.GetProperty("taker");
		Assert.Equal(expectedTaker.GetProperty("inputs").GetInt32(), plan.Taker.InputCount);
		Assert.Equal(expectedTaker.GetProperty("amountSats").GetInt64(), plan.Taker.Amount);
		Assert.True(MarketVectors.Longs(expectedTaker.GetProperty("picks")).SequenceEqual(plan.Taker.Picks), $"The outputs of the taker are {string.Join(", ", plan.Taker.Picks)}.");
		Assert.Equal(expectedTaker.GetProperty("change").GetInt64(), plan.Taker.Change);
		Assert.Equal(expectedTaker.GetProperty("forfeit").GetInt64(), plan.Taker.Forfeit);
		Assert.Equal(expectedTaker.GetProperty("totalSats").GetInt64(), plan.Taker.Total);

		var expectedTally = expected.GetProperty("tally").EnumerateArray().Select(entry => (entry[0].GetInt64(), entry[1].GetInt32())).ToArray();
		Assert.Equal(expectedTally, plan.Tally);

		var expectedCounts = expected.GetProperty("counts");
		Assert.Equal(MarketRulesTests.ReadCounts(expectedCounts), plan.Counts);
		Assert.Equal(expectedCounts.GetProperty("unmatchedOutputsShown").GetInt32(), plan.Counts.UnmatchedOutputs);

		MarketRulesTests.AssertFees(expected.GetProperty("fees"), plan.Fees);
		MarketRulesTests.AssertSettlement(expected.GetProperty("settlement"), plan.Settlement);
		MarketVectors.AssertClose(expected.GetProperty("takerCostSats").GetDecimal(), plan.TakerCost, "What the taker pays in all");

		var planning = expected.GetProperty("planning");
		MarketVectors.AssertClose(planning.GetProperty("objective").GetDouble(), plan.Objective, "The cost that the plan was weighed with");
		Assert.Equal(MarketVectors.Ints(planning.GetProperty("chainOrder")), plan.ChainOrder);
	}

	/// <summary>
	/// What every plan keeps, over five hundred selections of makers of the sample book that are drawn the same way
	/// every time, with takers of several amounts.
	/// </summary>
	[Fact]
	public void EveryPlanKeepsTheRules()
	{
		var random = new Random(20261123);
		var book = SampleMarket.Makers;
		long[] takerAmounts = [10_000_000, 10_000_000, 5_000_000, 25_000_000, 100_000_000, 170_151_398, 100_000, 50_000_000, 320_141_990, 9_950_000, 33_333_333];
		var plans = 0;

		for (var i = 0; i < 500; i++)
		{
			// Mostly small selections, as a taker makes them, and a large one now and then.
			var count = i % 25 == 24 ? random.Next(41, 90) : random.Next(1, 41);
			var makers = book
				.OrderBy(_ => random.Next())
				.Take(count)
				.OrderBy(maker => maker.Alias, StringComparer.Ordinal)
				.Select(maker => maker.ToPlannedMaker())
				.ToArray();
			var taker = new PlannedTaker(random.Next(1, 6), takerAmounts[i % takerAmounts.Length]);

			var plan = TransactionPlanner.Plan(taker, makers);
			AssertKeepsTheRules(taker, makers, plan, $"Selection {i} of {count} makers with a taker of {taker.Amount} sats");
			plans++;
		}

		Assert.Equal(500, plans);
	}

	/// <summary>The same makers and the same taker give the same plan, every time and on any thread: nothing in it is left to chance.</summary>
	[Fact]
	public async Task SameSelectionGivesTheSamePlanAsync()
	{
		var makers = SampleMarket.Makers
			.Where(maker => maker.Id % 7 == 3)
			.OrderBy(maker => maker.Alias, StringComparer.Ordinal)
			.Select(maker => maker.ToPlannedMaker())
			.ToArray();

		var first = TransactionPlanner.Plan(SampleMarket.Taker, makers);
		var second = TransactionPlanner.Plan(SampleMarket.Taker, makers);
		var onAnotherThread = await Task.Run(() => TransactionPlanner.Plan(SampleMarket.Taker, makers), TestContext.Current.CancellationToken);
		var together = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => TransactionPlanner.Plan(SampleMarket.Taker, makers), TestContext.Current.CancellationToken)));

		Assert.True(makers.Length > 30);

		foreach (var plan in together.Append(second).Append(onAnotherThread))
		{
			AssertSamePlan(first, plan);
		}
	}

	/// <summary>
	/// The whole sample book, 250 makers, is planned in a time that a taker can wait for. The limit is far above what
	/// it takes: it is there to tell if something grows with the square of the book that should not.
	/// </summary>
	[Fact]
	public void WholeBookIsPlannedInTime()
	{
		var makers = SampleMarket.Makers
			.OrderBy(maker => maker.Alias, StringComparer.Ordinal)
			.Select(maker => maker.ToPlannedMaker())
			.ToArray();

		// The first plan of a process pays for the compilation of the planner.
		TransactionPlanner.Plan(SampleMarket.Taker, [.. makers.Take(5)]);

		var watch = Stopwatch.StartNew();
		var plan = TransactionPlanner.Plan(SampleMarket.Taker, makers);
		watch.Stop();

		Assert.Equal(250, plan.Makers.Count);
		Assert.Equal(plan.MatchedInputs, plan.Points);
		Assert.Equal(0, plan.Counts.UnmatchedOutputs);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The whole book took {watch.Elapsed.TotalSeconds:0.0} seconds to plan.");
	}

	/// <summary>Without makers there is nothing to plan: the taker has no outputs, and only its own inputs and the overhead are billed, at no fee rate.</summary>
	[Fact]
	public void PlanWithoutMakersIsEmpty()
	{
		var plan = TransactionPlanner.Plan(SampleMarket.Taker, []);

		Assert.Equal(0m, plan.FeeRate);
		Assert.Equal(0, plan.Points);
		Assert.Equal(1, plan.Participants);
		Assert.Empty(plan.Taker.Picks);
		Assert.Empty(plan.Tally);
		Assert.Equal(0m, plan.TakerCost);
	}

	/// <summary>The standard values that a participant can always take: 1, 1.5, 2, 2.5, 3, 4, 5 and 8 times a power of ten, none below the smallest output.</summary>
	[Fact]
	public void StandardValuesAreThoseOfTheDesign()
	{
		var values = TransactionPlanner.StandardValues;

		Assert.Equal(59, values.Count);
		Assert.Equal(400, values[0]);
		Assert.Equal(8_000_000_000, values[^1]);
		Assert.Equal(values.Order(), values);
		Assert.Contains(150_000L, values);
		Assert.Contains(2_500_000_000L, values);
		Assert.DoesNotContain(300L, values);
		Assert.DoesNotContain(600_000L, values);
	}

	/// <summary>A rate or a fee rate with one decimal is the same number to the planner as it is in the design, which reads it as a binary fraction.</summary>
	[Fact]
	public void RatesReachThePlannerAsTheDesignReadsThem()
	{
		Assert.Equal(0.7, (double)0.7m);
		Assert.Equal(0.1, (double)0.1m);
		Assert.Equal(3.7, (double)3.7m);

		for (var tenths = 0; tenths <= 1000; tenths++)
		{
			var rate = tenths / 10m;
			Assert.Equal(double.Parse(rate.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture), (double)rate);
		}
	}

	private static void AssertKeepsTheRules(PlannedTaker taker, PlannedMaker[] makers, TransactionPlan plan, string what)
	{
		var counts = plan.Tally.ToDictionary(entry => entry.Value, entry => entry.Count);
		var forfeitLimit = Math.Max(MarketRules.Dust - 1, (long)Math.Ceiling(TransactionPlanner.LoneVb * (double)plan.FeeRate)) + ForfeitSlack;

		Assert.Equal(makers.Length, plan.Makers.Count);

		for (var i = 0; i < makers.Length; i++)
		{
			var maker = plan.Makers[i];
			long[] outputs = [.. maker.Outputs];

			// The outputs of a maker, its remainder and what it forfeits add up to its liquidity less its payment outputs.
			Assert.True(outputs.Sum() + maker.Forfeit == makers[i].Liquidity - makers[i].Payments.Sum(), $"{what}: the outputs of {maker.Alias} do not add up.");

			// No output is below the smallest one, a maker has four outputs at most and holds no value twice, payments included.
			Assert.True(outputs.All(value => value >= MarketRules.Dust), $"{what}: {maker.Alias} has an output below the smallest one.");
			Assert.True(outputs.Length <= MarketRules.MaxMakerOutputs, $"{what}: {maker.Alias} has {outputs.Length} outputs.");
			Assert.True(outputs.Concat(maker.Payments).Distinct().Count() == outputs.Length + maker.Payments.Count, $"{what}: {maker.Alias} holds a value twice.");

			// Nobody holds more than one output that is alone with its value.
			Assert.True(outputs.Count(value => counts[value] == 1) <= 1, $"{what}: {maker.Alias} holds more than one lone output.");

			Assert.True(maker.Forfeit >= 0 && maker.Forfeit <= forfeitLimit, $"{what}: {maker.Alias} forfeits {maker.Forfeit} sats.");
			Assert.True(maker.Pinned.All(maker.Picks.Contains), $"{what}: {maker.Alias} has a pinned output that is not among its outputs.");
		}

		long[] takerOutputs = [.. plan.Taker.Outputs];

		// The outputs of the taker and what it forfeits add up to its amount and what it expects to gain, which is not negative.
		Assert.True(plan.Taker.Total >= taker.Amount, $"{what}: the taker gets less than it brings.");
		Assert.True(takerOutputs.All(value => value >= MarketRules.Dust), $"{what}: the taker has an output below the smallest one.");
		Assert.True(takerOutputs.Length <= MarketRules.MaxTakerOutputs, $"{what}: the taker has {takerOutputs.Length} outputs.");
		Assert.True(takerOutputs.Distinct().Count() == takerOutputs.Length, $"{what}: the taker holds a value twice.");
		Assert.True(takerOutputs.Count(value => counts[value] == 1) <= 1, $"{what}: the taker holds more than one lone output.");
		Assert.True(plan.Taker.Forfeit >= 0 && plan.Taker.Forfeit <= forfeitLimit, $"{what}: the taker forfeits {plan.Taker.Forfeit} sats.");

		// The tally is that of all the outputs, and the points follow from it.
		var all = plan.Makers.SelectMany(maker => maker.Outputs.Concat(maker.Payments)).Concat(takerOutputs).ToArray();
		Assert.True(all.Length == plan.Tally.Sum(entry => entry.Count), $"{what}: the tally does not count every output.");
		Assert.Equal((int)WalletWasabi.Blockchain.Analysis.AnonymityPoints.SumOutputPoints(all), plan.OutputPoints);
		Assert.Equal(Math.Min(plan.MatchedInputs, plan.OutputPoints), plan.Points);
		Assert.Equal(makers.Sum(maker => maker.MatchedInputs), plan.MatchedInputs);
		Assert.Equal(makers.Max(maker => maker.MinFeeRate), plan.FeeRate);
	}

	private static void AssertSamePlan(TransactionPlan expected, TransactionPlan actual)
	{
		Assert.Equal(expected.Makers.Count, actual.Makers.Count);

		for (var i = 0; i < expected.Makers.Count; i++)
		{
			Assert.Equal(expected.Makers[i].Picks, actual.Makers[i].Picks);
			Assert.Equal(expected.Makers[i].Change, actual.Makers[i].Change);
			Assert.Equal(expected.Makers[i].Forfeit, actual.Makers[i].Forfeit);
			Assert.Equal(expected.Makers[i].Pinned, actual.Makers[i].Pinned);
		}

		Assert.Equal(expected.Taker.Picks, actual.Taker.Picks);
		Assert.Equal(expected.Taker.Change, actual.Taker.Change);
		Assert.Equal(expected.Taker.Forfeit, actual.Taker.Forfeit);
		Assert.Equal(expected.Tally, actual.Tally);
		Assert.Equal(expected.Objective, actual.Objective);
		Assert.Equal(expected.ChainOrder, actual.ChainOrder);
		Assert.Equal(expected.TakerCost, actual.TakerCost);
	}
}
