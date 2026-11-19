using NBitcoin;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BlockchainAnalysis;

/// <summary>
/// The rules of the anonymity points: which outputs are matched and what a transaction is worth.
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
		Assert.Equal(2, AnonymityPoints.CountMatchedOutputs(transaction));
	}

	[Fact]
	public void EveryAmountThatAppearsMoreThanOnceCounts()
	{
		// Three of one amount, two of another, and two outputs that stand alone.
		var transaction = CreateTransaction(0.1m, 0.02m, 0.1m, 0.5m, 0.02m, 0.1m, 0.3m);

		Assert.Equal(5, AnonymityPoints.CountMatchedOutputs(transaction));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 3));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 6));
	}

	[Fact]
	public void PaymentWithChangeHasNoMatchedOutputs()
	{
		var transaction = CreateTransaction(0.1m, 0.0734m);

		Assert.Equal(0, AnonymityPoints.CountMatchedOutputs(transaction));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 0));
		Assert.False(AnonymityPoints.IsMatchedOutput(transaction, 1));
	}

	[Fact]
	public void OutputThatTheTransactionDoesNotHaveIsNotMatched()
	{
		Assert.False(AnonymityPoints.IsMatchedOutput(CreateTransaction(0.1m, 0.1m), 2));
	}

	/// <summary>
	/// The lower number counts: a transaction is not worth more than it has matched outputs, and not more than it has
	/// inputs that were matched outputs before.
	/// </summary>
	[Theory]
	[InlineData(2, 3, 2)] // Three inputs, two of which were matched outputs, and three equal outputs plus two others.
	[InlineData(3, 2, 2)]
	[InlineData(3, 3, 3)]
	[InlineData(0, 5, 0)] // A first coinjoin: none of its inputs was a matched output.
	[InlineData(5, 0, 0)] // A payment that spends outputs of coinjoins.
	[InlineData(0, 0, 0)]
	public void TransactionIsWorthTheLowerOfItsMatchedInputsAndItsMatchedOutputs(int matchedInputs, int matchedOutputs, int expectedPoints)
	{
		Assert.Equal(expectedPoints, AnonymityPoints.Calculate(matchedInputs, matchedOutputs));
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
