using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client.Decomposer;
using WalletWasabi.WabiSabi.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

public class AmountDecomposerTests
{
	private static readonly RandomnessProvider Random = RandomExtensions.CreateSeeded(seed: 0);

	[Theory]
	[InlineData(0, 0, 8)]
	[InlineData(0, 0, 1)]
	[InlineData(0, 0, 2)]
	[InlineData(0, 0, 3)]
	[InlineData(0, 1_000, 1)]
	[InlineData(0, 100_000, 2)]
	[InlineData(0, 1_000_000, 3)]
	[InlineData(0, 10_000_000, 8)]
	[InlineData(20, 0, 1)]
	[InlineData(100, 0, 2)]
	[InlineData(500, 0, 3)]
	[InlineData(5000, 0, 8)]
	[InlineData(0, 0, 8, true)]
	[InlineData(0, 0, 1, true)]
	[InlineData(0, 0, 2, true)]
	[InlineData(0, 0, 3, true)]
	[InlineData(0, 1_000, 1, true)]
	[InlineData(0, 100_000, 2, true)]
	[InlineData(0, 1_000_000, 3, true)]
	[InlineData(0, 10_000_000, 8, true)]
	[InlineData(20, 0, 1, true)]
	[InlineData(100, 0, 2, true)]
	[InlineData(500, 0, 3, true)]
	[InlineData(5000, 0, 8, true)]
	public void DecompositionsInvariantTest(decimal feeRateDecimal, long minOutputAmount, int maxAvailableOutputs, bool isTaprootEnabled = false)
	{
		var outputVirtualSize = isTaprootEnabled ? Constants.P2trOutputVirtualSize : Constants.P2wpkhOutputVirtualSize;
		var availableVsize = maxAvailableOutputs * outputVirtualSize;
		var feeRate = new FeeRate(feeRateDecimal);
		var feePerOutput = feeRate.GetFee(outputVirtualSize);
		var registeredCoinEffectiveValues = GenerateRandomCoins().Take(3).Select(c => c.EffectiveValue(feeRate)).ToList();
		var theirCoinEffectiveValues = GenerateRandomCoins().Take(30).Select(c => c.EffectiveValue(feeRate)).ToList();
		var allowedOutputAmountRange = new MoneyRange(Money.Satoshis(minOutputAmount), Money.Satoshis(ProtocolConstants.MaxAmountPerAlice));
		var allowedOutputTypes = isTaprootEnabled ? new List<ScriptType>() { ScriptType.Taproot, ScriptType.P2WPKH } : new List<ScriptType>() { ScriptType.P2WPKH };

		var amountDecomposer = new AmountDecomposer(feeRate, allowedOutputAmountRange.Min, allowedOutputAmountRange.Max, availableVsize, allowedOutputTypes, RandomnessProviders.Insecure);
		var allCoinEffectiveValues = theirCoinEffectiveValues.Concat(registeredCoinEffectiveValues);
		var outputValues = amountDecomposer.Decompose(registeredCoinEffectiveValues.Sum(), allCoinEffectiveValues);

		var totalEffectiveValue = registeredCoinEffectiveValues.Sum(x => x);
		var totalEffectiveCost = outputValues.Sum(x => x.EffectiveCost);

		if (!isTaprootEnabled)
		{
			Assert.InRange(outputValues.Count(), 1, maxAvailableOutputs);
		}
		else
		{
			// The number of outputs cannot be ensure because of random scriptype generation. Instead we verify the total.
			Assert.InRange(outputValues.Sum(x => x.ScriptType.EstimateOutputVsize()), 1, availableVsize);
		}

		Assert.True(totalEffectiveValue - totalEffectiveCost - minOutputAmount <= outputValues.Sum(x => x.EffectiveCost));
		Assert.All(outputValues, v => Assert.InRange(v.EffectiveCost.Satoshi, minOutputAmount, totalEffectiveValue));

		var containsTaproot = outputValues.Any(o => o.ScriptType == ScriptType.Taproot);

		if (!isTaprootEnabled)
		{
			Assert.False(containsTaproot);
		}
	}

	/// <summary>
	/// The user sets the most that a coinjoin may leave over. Whatever the round looks like, no more than that is left,
	/// no money is made up, and every output is one that the round allows.
	/// </summary>
	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(500)]
	[InlineData(3_000)]
	public void LeftoverStaysWithinTheLimitOfTheUser(long maxLeftoverSatoshis)
	{
		var maxLeftover = Money.Satoshis(maxLeftoverSatoshis);
		var minOutputAmount = Money.Satoshis(5_000);
		var maxOutputAmount = Money.Satoshis(ProtocolConstants.MaxAmountPerAlice);
		var allowedOutputTypes = new List<ScriptType> { ScriptType.Taproot };
		var random = RandomExtensions.CreateSeeded(seed: 7);
		var roundsWhereTheLimitMattered = 0;

		for (var round = 0; round < 60; round++)
		{
			var feeRate = new FeeRate((decimal)(int)random.GetInt64(1, 60));
			var availableVsize = (int)random.GetInt64(1, 9) * Constants.P2trOutputVirtualSize;
			var myInputs = Enumerable.Range(0, (int)random.GetInt64(1, 4)).Select(_ => Money.Satoshis(random.GetInt64(20_000, 50_000_000))).ToList();
			var theirInputs = Enumerable.Range(0, 30).Select(_ => Money.Satoshis(random.GetInt64(20_000, 500_000_000))).ToList();
			var myInputSum = myInputs.Sum();
			var allInputs = theirInputs.Concat(myInputs).ToList();

			// What the same round leaves over when the decomposer is given no limit.
			var unlimited = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round))
				.Decompose(myInputSum, allInputs)
				.ToList();
			if (myInputSum - unlimited.Sum(x => x.EffectiveCost) > maxLeftover)
			{
				roundsWhereTheLimitMattered++;
			}

			var outputs = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round), maxLeftover)
				.Decompose(myInputSum, allInputs)
				.ToList();

			var leftover = myInputSum - outputs.Sum(x => x.EffectiveCost);
			Assert.InRange(leftover.Satoshi, 0, maxLeftoverSatoshis);
			Assert.NotEmpty(outputs);
			Assert.InRange(outputs.Sum(x => x.ScriptType.EstimateOutputVsize()), 1, availableVsize);
			Assert.All(outputs, output => Assert.InRange(output.Amount.Satoshi, minOutputAmount.Satoshi, maxOutputAmount.Satoshi));
		}

		// The test would prove nothing if the limit never had to be enforced.
		Assert.True(roundsWhereTheLimitMattered > 5, $"The limit mattered in {roundsWhereTheLimitMattered} rounds only.");
	}

	/// <summary>
	/// A round may ask for a large smallest output. What cannot be put into such an output is a lot more than dust,
	/// and the limit of the user is what keeps it: no more than the limit is left over, however large the smallest output is.
	/// </summary>
	[Theory]
	[InlineData(100_000)]
	[InlineData(1_000_000)]
	[InlineData(50_000_000)]
	public void RoundsWithALargeSmallestOutputLeaveNoMoreThanTheLimit(long minOutputSatoshis)
	{
		var maxLeftover = Money.Satoshis(5_000);
		var minOutputAmount = Money.Satoshis(minOutputSatoshis);
		var maxOutputAmount = Money.Satoshis(ProtocolConstants.MaxAmountPerAlice);
		var allowedOutputTypes = new List<ScriptType> { ScriptType.Taproot };
		var random = RandomExtensions.CreateSeeded(seed: 13);
		var roundsThatWouldHaveLeftMore = 0;
		var mostLeftWithoutLimit = Money.Zero;

		for (var round = 0; round < 40; round++)
		{
			var feeRate = new FeeRate((decimal)(int)random.GetInt64(1, 60));
			var availableVsize = (int)random.GetInt64(1, 9) * Constants.P2trOutputVirtualSize;
			var myInputs = Enumerable.Range(0, (int)random.GetInt64(1, 4)).Select(_ => Money.Satoshis(random.GetInt64(2 * minOutputSatoshis, 40 * minOutputSatoshis))).ToList();
			var theirInputs = Enumerable.Range(0, 30).Select(_ => Money.Satoshis(random.GetInt64(2 * minOutputSatoshis, 400 * minOutputSatoshis))).ToList();
			var myInputSum = myInputs.Sum();
			var allInputs = theirInputs.Concat(myInputs).ToList();

			var unlimited = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round))
				.Decompose(myInputSum, allInputs)
				.ToList();
			var leftWithoutLimit = myInputSum - unlimited.Sum(x => x.EffectiveCost);
			mostLeftWithoutLimit = Money.Max(mostLeftWithoutLimit, leftWithoutLimit);
			if (leftWithoutLimit > maxLeftover)
			{
				roundsThatWouldHaveLeftMore++;
			}

			var outputs = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round), maxLeftover)
				.Decompose(myInputSum, allInputs)
				.ToList();

			var leftover = myInputSum - outputs.Sum(x => x.EffectiveCost);
			Assert.InRange(leftover.Satoshi, 0, maxLeftover.Satoshi);
			Assert.NotEmpty(outputs);
			Assert.InRange(outputs.Sum(x => x.ScriptType.EstimateOutputVsize()), 1, availableVsize);
			Assert.All(outputs, output => Assert.InRange(output.Amount.Satoshi, minOutputAmount.Satoshi, maxOutputAmount.Satoshi));
		}

		// Without the limit these rounds cost a lot more than the limit, or the test proves nothing.
		Assert.True(roundsThatWouldHaveLeftMore > 5, $"The limit mattered in {roundsThatWouldHaveLeftMore} rounds only.");
		Assert.True(mostLeftWithoutLimit > Money.Satoshis(minOutputSatoshis / 4), $"Without the limit at most {mostLeftWithoutLimit.Satoshi} satoshis were left.");
	}

	/// <summary>
	/// A round can be such that the limit cannot be kept: its only allowed output amount leaves most of a coin over and
	/// no output may take that. Without a limit the decomposer gives the rest away. With a limit it gives up the round.
	/// </summary>
	[Fact]
	public void RoundThatCannotKeepTheLimitIsGivenUp()
	{
		var feeRate = new FeeRate(2m);
		var onlyAllowedAmount = Money.Satoshis(1_000_000);
		var allowedOutputTypes = new List<ScriptType> { ScriptType.Taproot };
		var availableVsize = 8 * Constants.P2trOutputVirtualSize;
		var myInputSum = Money.Satoshis(1_900_000);
		var allInputs = new[] { Money.Satoshis(5_000_000), Money.Satoshis(3_000_000), Money.Satoshis(2_500_000), myInputSum };

		var unlimited = new AmountDecomposer(feeRate, onlyAllowedAmount, onlyAllowedAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: 1))
			.Decompose(myInputSum, allInputs)
			.ToList();
		Assert.True(myInputSum - unlimited.Sum(x => x.EffectiveCost) > Money.Satoshis(800_000));

		var limited = new AmountDecomposer(feeRate, onlyAllowedAmount, onlyAllowedAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: 1), Money.Satoshis(5_000));
		Assert.Throws<InvalidOperationException>(() => limited.Decompose(myInputSum, allInputs).ToList());
	}

	/// <summary>
	/// A limit that no decomposition exceeds changes nothing: the outputs are the ones that the user gets without a limit.
	/// </summary>
	[Fact]
	public void LimitAboveWhatARoundCanLeaveChangesNothing()
	{
		var minOutputAmount = Money.Satoshis(5_000);
		var maxOutputAmount = Money.Satoshis(ProtocolConstants.MaxAmountPerAlice);
		var allowedOutputTypes = new List<ScriptType> { ScriptType.Taproot };
		var random = RandomExtensions.CreateSeeded(seed: 11);

		for (var round = 0; round < 20; round++)
		{
			var feeRate = new FeeRate((decimal)(int)random.GetInt64(1, 60));
			var availableVsize = (int)random.GetInt64(1, 9) * Constants.P2trOutputVirtualSize;
			var myInputSum = Money.Satoshis(random.GetInt64(20_000, 50_000_000));
			var allInputs = Enumerable.Range(0, 30).Select(_ => Money.Satoshis(random.GetInt64(20_000, 500_000_000))).Append(myInputSum).ToList();

			var unlimited = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round))
				.Decompose(myInputSum, allInputs);
			var limited = new AmountDecomposer(feeRate, minOutputAmount, maxOutputAmount, availableVsize, allowedOutputTypes, RandomExtensions.CreateSeeded(seed: round), Money.Coins(1))
				.Decompose(myInputSum, allInputs);

			Assert.Equal(unlimited.Select(x => x.Amount), limited.Select(x => x.Amount));
		}
	}

	private static IEnumerable<Coin> GenerateRandomCoins()
	{
		using var key = new Key();
		var script = key.GetScriptPubKey(ScriptPubKeyType.Segwit);
		while (true)
		{
			var amount = Random.GetInt64(100_000, ProtocolConstants.MaxAmountPerAlice);
			yield return CreateCoin(script, amount);
		}
	}

	private static Coin CreateCoin(Script scriptPubKey, long amount)
	{
		var prevOut = BitcoinFactory.CreateOutPoint();
		var txOut = new TxOut(Money.Satoshis(amount), scriptPubKey);
		return new Coin(prevOut, txOut);
	}

	[Theory]
	[InlineData(2, 7, 2, 3, new long[] { 8, 4, 2 })]
	[InlineData(39, 728551029, 4999, 8, new long[] { 6973569112, 4294967606, 2324523244, 1162261777, 774841288, 536871222, 268435766, 134218038, 86093752, 50000310, 33554742, 20000310, 14349217, 10000310, 5000310, 3188956, 2097462, 1594633, 1063192, 531751, 354604, 262454, 200310, 131382, 100310, 65846, 50310, 39676, 33078, 20310, 16694, 13432, 10310 })]
	public void DecomposeTests(int expectedResultCount, long target, long tolerance, int maxCount, long[] stdDenoms)
	{
		var res = Decomposer.Decompose(target, tolerance, maxCount, stdDenoms);

		Assert.Equal(expectedResultCount, res.Count());
		Assert.All(res, x => Assert.True(x.Sum <= target && x.Sum >= target - tolerance));
	}
}
