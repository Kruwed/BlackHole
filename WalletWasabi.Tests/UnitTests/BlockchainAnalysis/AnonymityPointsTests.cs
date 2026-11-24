using NBitcoin;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BlockchainAnalysis;

/// <summary>
/// The rules of the anonymity points: which outputs are matched, what an output is worth and what a transaction is worth.
/// </summary>
public class AnonymityPointsTests
{
	[Fact]
	public void OutputIsMatchedWhenAnotherOutputOfTheTransactionHasItsAmount()
	{
		var transaction = CreateTransaction(0.1m, 0.05m, 0.1m);

		Assert.True(AnonymityPoints.IsMatchedOutput(transaction, 0));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 1));
		Assert.True(AnonymityPoints.IsMatchedOutput(transaction, 2));
	}

	/// <summary>
	/// An output is worth a point if two or more other outputs have its amount, half a point if exactly one other
	/// output has it, and nothing if it is alone with its amount.
	/// </summary>
	[Theory]
	[InlineData(1, 0)]
	[InlineData(2, 0.5)]
	[InlineData(3, 1)]
	[InlineData(4, 1)]
	[InlineData(100, 1)]
	public void OutputIsWorthAPointInASetOfThreeAndHalfAPointInAPair(int outputsWithTheAmount, decimal expectedWorth)
	{
		Assert.Equal(expectedWorth, AnonymityPoints.GetWorth(outputsWithTheAmount));
	}

	[Fact]
	public void EveryOutputIsWorthWhatItsAmountMakesIt()
	{
		// Three of one amount, two of another, and two outputs that stand alone.
		var transaction = CreateTransaction(0.1m, 0.02m, 0.1m, 0.5m, 0.02m, 0.1m, 0.3m);

		Assert.Equal(1m, AnonymityPoints.GetWorthOfOutput(transaction, 0));
		Assert.Equal(0.5m, AnonymityPoints.GetWorthOfOutput(transaction, 1));
		Assert.Equal(1m, AnonymityPoints.GetWorthOfOutput(transaction, 2));
		Assert.Equal(0m, AnonymityPoints.GetWorthOfOutput(transaction, 3));
		Assert.Equal(0.5m, AnonymityPoints.GetWorthOfOutput(transaction, 4));
		Assert.Equal(1m, AnonymityPoints.GetWorthOfOutput(transaction, 5));
		Assert.Equal(0m, AnonymityPoints.GetWorthOfOutput(transaction, 6));

		// The set of three is worth three points and the pair one.
		Assert.Equal(4m, AnonymityPoints.SumOutputPoints(transaction));

		// The outputs of a pair are matched, like those of the set of three, and the two others are not.
		Assert.True(AnonymityPoints.IsMatchedOutput(transaction, 1));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 3));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 6));
	}

	/// <summary>What the outputs of a transaction are worth together, by their amounts in satoshis.</summary>
	[Theory]
	[InlineData(new long[] { 50_000, 60_000, 70_000 }, 0)] // Nothing is shared.
	[InlineData(new long[] { 50_000, 50_000, 70_000 }, 1)] // One pair: half a point each.
	[InlineData(new long[] { 50_000, 50_000, 50_000 }, 3)] // One set of three: a point each.
	[InlineData(new long[] { 50_000, 50_000, 50_000, 70_000, 70_000, 90_000 }, 4)] // A set of three, a pair and one alone.
	[InlineData(new long[] { 10_000_000, 10_000_000, 10_000_000, 10_000_000 }, 4)] // Four of a kind: a point each.
	[InlineData(new long[] { 100_000, 100_000, 250_000, 250_000 }, 2)] // Two pairs.
	[InlineData(new long[] { 1_500_000, 1_500_000, 1_500_000, 42 }, 3)] // A third output lifts a pair from one point to three.
	[InlineData(new long[] { }, 0)]
	public void OutputsOfATransactionAreWorthAWholeNumberOfPoints(long[] outputAmounts, int expectedPoints)
	{
		Assert.Equal(expectedPoints, AnonymityPoints.SumOutputPoints(outputAmounts));
	}

	[Fact]
	public void PaymentWithChangeHasNoMatchedOutputs()
	{
		var transaction = CreateTransaction(0.1m, 0.0734m);

		Assert.Equal(0m, AnonymityPoints.SumOutputPoints(transaction));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 0));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 1));
	}

	[Fact]
	public void OutputThatTheTransactionDoesNotHaveIsNotMatchedAndWorthNothing()
	{
		var transaction = CreateTransaction(0.1m, 0.1m);

		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 2));
		Assert.Equal(0m, AnonymityPoints.GetWorthOfOutput(transaction, 2));
	}

	/// <summary>
	/// The lower number counts: a transaction is not worth more than its outputs are worth, and not more than its
	/// inputs were worth as outputs before.
	/// </summary>
	[Theory]
	[InlineData(2, 3, 2)] // Three inputs, two of which came out of sets of three, and three equal outputs plus two others.
	[InlineData(3, 2, 2)]
	[InlineData(3, 3, 3)]
	[InlineData(0, 5, 0)] // A first coinjoin: none of its inputs was a matched output.
	[InlineData(5, 0, 0)] // A payment that spends outputs of coinjoins.
	[InlineData(0, 0, 0)]
	[InlineData(2, 4, 2)] // Four inputs that each came out of a pair, and four equal outputs.
	[InlineData(1.5, 3, 1.5)] // An input out of a set of three and one out of a pair: the transaction is worth half a point more than one.
	[InlineData(2.5, 2, 2)]
	public void TransactionIsWorthTheLowerOfWhatItsInputsAndItsOutputsAreWorth(decimal inputPoints, decimal outputPoints, decimal expectedPoints)
	{
		Assert.Equal(expectedPoints, AnonymityPoints.Calculate(inputPoints, outputPoints));
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(0.5, true)]
	[InlineData(7, true)]
	[InlineData(7.5, true)]
	[InlineData(-0.5, false)]
	[InlineData(0.25, false)]
	[InlineData(3.1, false)]
	public void PointsAreAWholeNumberOfHalfPoints(decimal points, bool expectedValid)
	{
		Assert.Equal(expectedValid, AnonymityPoints.IsValid(points));

		if (expectedValid)
		{
			Assert.Equal(points, AnonymityPoints.FromHalfPoints(AnonymityPoints.ToHalfPoints(points)));
		}
		else
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => AnonymityPoints.ToHalfPoints(points));
		}
	}

	/// <summary>Whole points are written as whole numbers, so that whoever reads them out of the wallet gets "2" and "2.5", not "2.0".</summary>
	[Fact]
	public void WholePointsAreWholeNumbers()
	{
		Assert.Equal("0", AnonymityPoints.FromHalfPoints(0).ToString(System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal("2", AnonymityPoints.FromHalfPoints(4).ToString(System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal("2.5", AnonymityPoints.FromHalfPoints(5).ToString(System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal("1", AnonymityPoints.GetWorth(3).ToString(System.Globalization.CultureInfo.InvariantCulture));
		Assert.Equal("0.5", AnonymityPoints.GetWorth(2).ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	private static Transaction CreateTransaction(params decimal[] outputAmounts)
	{
		var transaction = Network.Main.CreateTransaction();
		transaction.Inputs.Add(BitcoinFactory.CreateOutPoint());

		foreach (var amount in outputAmounts)
		{
			transaction.Outputs.Add(Money.Coins(amount), BitcoinFactory.CreateScript());
		}

		return transaction;
	}
}
