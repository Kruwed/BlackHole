using System.Linq;
using System.Text.Json;
using WalletWasabi.JoinMarket.Planning;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The three searches that the planner of a taker's coinjoin is made of, against the cases of the design. Which of
/// several plans of the same cost a search finds is a part of what is expected.
/// </summary>
public class DecomposerTests
{
	public static TheoryData<int> DecomposeCases => MarketVectors.CaseIndexes("search", "decompose.json");

	public static TheoryData<int> DecomposeWithCases => MarketVectors.CaseIndexes("search", "decompose-with.json");

	public static TheoryData<int> FitSumsCases => MarketVectors.CaseIndexes("search", "fit-sums.json");

	[Theory]
	[MemberData(nameof(DecomposeCases))]
	public void AmountIsSplitIntoTheOutputsOfTheDesign(int index)
	{
		using var document = MarketVectors.Open("search", "decompose.json");
		var testCase = document.RootElement.GetProperty("cases")[index];

		var found = Decomposer.Decompose(
			testCase.GetProperty("amount").GetInt64(),
			MarketVectors.Longs(testCase.GetProperty("eligible")),
			MarketVectors.ReadOptions(testCase.GetProperty("options")));

		AssertDecomposition(testCase.GetProperty("expected"), found);
	}

	[Theory]
	[MemberData(nameof(DecomposeWithCases))]
	public void AmountIsSplitWithOutputsOfLonelyValuesAsInTheDesign(int index)
	{
		using var document = MarketVectors.Open("search", "decompose-with.json");
		var testCase = document.RootElement.GetProperty("cases")[index];

		var found = Decomposer.DecomposeWith(
			testCase.GetProperty("amount").GetInt64(),
			MarketVectors.Longs(testCase.GetProperty("eligible")),
			MarketVectors.ReadOptions(testCase.GetProperty("options")),
			MarketVectors.Longs(testCase.GetProperty("lonely")),
			testCase.GetProperty("maxClones").GetInt32(),
			MarketVectors.Longs(testCase.GetProperty("forced")));

		AssertDecomposition(testCase.GetProperty("expected"), found);
	}

	/// <summary>The sets come in the order of the design: the plans that follow from them are tried in that order.</summary>
	[Theory]
	[MemberData(nameof(FitSumsCases))]
	public void SumsThatFitAreFoundInTheOrderOfTheDesign(int index)
	{
		using var document = MarketVectors.Open("search", "fit-sums.json");
		var testCase = document.RootElement.GetProperty("cases")[index];

		var found = Decomposer.FitSums(
			testCase.GetProperty("target").GetInt64(),
			MarketVectors.Longs(testCase.GetProperty("values")),
			testCase.GetProperty("maxPicks").GetInt32(),
			testCase.GetProperty("lo").GetDouble(),
			testCase.GetProperty("hi").GetDouble(),
			testCase.GetProperty("limit").GetInt32());

		var expected = testCase.GetProperty("expected").EnumerateArray().Select(MarketVectors.Longs).ToArray();

		Assert.Equal(expected.Length, found.Count);

		for (var i = 0; i < expected.Length; i++)
		{
			Assert.Equal(expected[i], found[i]);
		}
	}

	/// <summary>An amount that no output and no remainder can carry, and that is too much to leave to the miners, has no plan.</summary>
	[Fact]
	public void AmountThatNothingCanCarryHasNoPlan()
	{
		var options = new DecomposeOptions
		{
			FeeRate = 1,
			MaxOutputs = 0,
			Branch = [4],
			ForfeitMax = 200,
			ChangeVb = _ => 200
		};

		Assert.Null(Decomposer.Decompose(1_000_000, [500_000], options));
	}

	/// <summary>A few sats that are worth less than the output that would carry them go to the miners.</summary>
	[Fact]
	public void SmallLeftoverGoesToTheMiners()
	{
		var options = new DecomposeOptions
		{
			FeeRate = 1,
			MaxOutputs = 2,
			Branch = [4, 4],
			ForfeitMax = 200,
			ChangeVb = _ => 200
		};

		var found = Decomposer.Decompose(1_000_150, [1_000_000, 500_000], options);

		Assert.NotNull(found);
		Assert.Equal([1_000_000L], found.Picks);
		Assert.Equal(0, found.Change);
		Assert.Equal(150, found.Forfeit);
	}

	private static void AssertDecomposition(JsonElement expected, Decomposition? found)
	{
		if (expected.ValueKind == JsonValueKind.Null)
		{
			Assert.Null(found);
			return;
		}

		Assert.NotNull(found);
		Assert.Equal(MarketVectors.Longs(expected.GetProperty("picks")), found.Picks);
		Assert.Equal(expected.GetProperty("change").GetInt64(), found.Change);
		Assert.Equal(expected.GetProperty("forfeit").GetInt64(), found.Forfeit);
		MarketVectors.AssertClose(expected.GetProperty("cost").GetDouble(), found.Cost, "The cost of the plan");
	}
}
