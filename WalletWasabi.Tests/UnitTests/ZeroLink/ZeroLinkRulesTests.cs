using System.Linq;
using NBitcoin;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Serialization;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.ZeroLink;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ZeroLink;

/// <summary>
/// The rules of a ZeroLink round: every input holds one standard denomination, every output holds that denomination less
/// the fee of a transaction with one input and one output, and a participant has one input and one output.
/// </summary>
public class ZeroLinkRulesTests
{
	private static readonly Money Denomination = Money.Coins(0.1m);

	/// <summary>
	/// What a participant pays the fee of is a whole transaction with one Taproot input and one Taproot output: the
	/// 57.5 vB of the input and the 43 vB of the output, and the 10.5 vB that a transaction has besides those.
	/// </summary>
	[Fact]
	public void ParticipantPaysForATransactionWithOneInputAndOneOutput()
	{
		var transaction = CreateSignedTransaction(inputCount: 1, outputCount: 1);

		Assert.Equal(444, transaction.GetVirtualSize() * 4);
		Assert.Equal(111, transaction.GetVirtualSize());
		Assert.Equal(111, ZeroLinkRules.ParticipantVsize);

		// The credentials of the round count an input as 58 vB and an output as 43 vB.
		Assert.Equal(101, ZeroLinkRules.VsizeAllocationPerAlice);
	}

	/// <summary>
	/// Every input of a round is spent the same way, by one signature of 64 bytes: the one that the default signature
	/// hash of Taproot gives. A signature that names its hash has a byte more, and its input would be larger than the
	/// others.
	/// </summary>
	[Fact]
	public void InputIsSpentByOneSignatureOf64Bytes()
	{
		Assert.Equal(TaprootSigHash.Default, ZeroLinkRules.SigHash);

		Assert.True(ZeroLinkRules.IsInputWitness(new WitScript(Op.GetPushOp(new byte[64]))));

		Assert.False(ZeroLinkRules.IsInputWitness(new WitScript(Op.GetPushOp(new byte[65]))));
		Assert.False(ZeroLinkRules.IsInputWitness(new WitScript(Op.GetPushOp(new byte[64]), Op.GetPushOp(new byte[33]))));
		Assert.False(ZeroLinkRules.IsInputWitness(WitScript.Empty));
	}

	[Theory]
	[InlineData(1, 1)]
	[InlineData(2, 2)]
	[InlineData(3, 2)]
	[InlineData(100, 100)]
	[InlineData(252, 252)]
	[InlineData(253, 252)] // From 253 on a count takes three bytes instead of one.
	[InlineData(253, 253)]
	[InlineData(400, 400)]
	public void SizeOfTheTransactionOfARoundIsCountedExactly(int inputCount, int outputCount)
	{
		var transaction = CreateSignedTransaction(inputCount, outputCount);

		Assert.Equal(transaction.GetVirtualSize(), ZeroLinkRules.GetTransactionVsize(inputCount, outputCount));
	}

	[Theory]
	[InlineData(1_000, 111)] // 1 sat/vB
	[InlineData(2_500, 278)] // 2.5 sat/vB: 277.5 sats, rounded up
	[InlineData(100, 12)] // 0.1 sat/vB: 11.1 sats
	[InlineData(1_001, 112)]
	[InlineData(134_714, 14_954)]
	[InlineData(10_000_000, 1_110_000)]
	public void FeeOfAParticipantIsRoundedUpToAWholeSatoshi(long feePerK, long expectedFee)
	{
		var feeRate = new FeeRate(Money.Satoshis(feePerK));

		Assert.Equal(Money.Satoshis(expectedFee), ZeroLinkRules.GetFee(feeRate));
		Assert.Equal(Denomination - Money.Satoshis(expectedFee), ZeroLinkRules.GetOutputAmount(Denomination, feeRate));
	}

	/// <summary>
	/// A round has what a transaction has besides its inputs and outputs once, and every participant pays for it. So
	/// its transaction never pays less than the fee rate of the round, however many take part.
	/// </summary>
	[Theory]
	[InlineData(100)]
	[InlineData(1_000)]
	[InlineData(1_001)]
	[InlineData(2_500)]
	[InlineData(134_714)]
	public void TransactionOfARoundPaysAtLeastTheFeeRate(long feePerK)
	{
		var feeRate = new FeeRate(Money.Satoshis(feePerK));
		var fee = ZeroLinkRules.GetFee(feeRate).Satoshi;

		foreach (var participants in new[] { 1, 2, 3, 10, 100, 252, 253, 254, 400, 900 })
		{
			var vsize = ZeroLinkRules.GetTransactionVsize(participants, participants);

			Assert.True(participants * fee * 1000 >= feePerK * vsize, $"{participants} participants at {feePerK} sats per kvB");
		}

		// One participant alone would pay exactly for its transaction, and a hundred overpay what ninety-nine of them
		// have no overhead of their own for: 99 times 10.5 vB.
		Assert.Equal(111, ZeroLinkRules.GetTransactionVsize(1, 1));
		Assert.Equal((100 * 111) - 1039, ZeroLinkRules.GetTransactionVsize(100, 100));
	}

	[Theory]
	[InlineData(5_000)]
	[InlineData(6_561)] // 3^8
	[InlineData(8_192)] // 2^13
	[InlineData(10_000)]
	[InlineData(13_122)] // 2 * 3^8
	[InlineData(20_000)]
	[InlineData(1_594_323)] // 3^13
	[InlineData(10_000_000)]
	[InlineData(134_217_728)] // 2^27
	[InlineData(100_000_000_000)]
	public void DenominationsOfTheDecomposerAreStandard(long satoshis)
	{
		Assert.True(ZeroLinkRules.IsStandardDenomination(Money.Satoshis(satoshis)));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(-10_000)]
	[InlineData(5_001)]
	[InlineData(7_000)]
	[InlineData(9_999_889)] // A denomination less a fee: what a ZeroLink round gives is not standard itself.
	[InlineData(12_345_678)]
	[InlineData(30_000_000)]
	[InlineData(15_000)] // 3 * 5 * 10^3
	public void OtherAmountsAreNotStandard(long satoshis)
	{
		Assert.False(ZeroLinkRules.IsStandardDenomination(Money.Satoshis(satoshis)));
	}

	[Fact]
	public void RoundTakesOneInputAmountAndGivesOneOutputAmount()
	{
		var config = new WabiSabiConfig { MaxInputCountByRound = 40 };
		var feeRate = new FeeRate(2.5m);

		var parameters = ZeroLinkRules.CreateRoundParameters(config, feeRate, Denomination);

		Assert.Equal(new MoneyRange(Denomination, Denomination), parameters.AllowedInputAmounts);
		Assert.Equal(Money.Satoshis(10_000_000 - 278), parameters.AllowedOutputAmounts.Min);
		Assert.Equal(parameters.AllowedOutputAmounts.Min, parameters.AllowedOutputAmounts.Max);
		Assert.Equal([ScriptType.Taproot], parameters.AllowedInputTypes);
		Assert.Equal([ScriptType.Taproot], parameters.AllowedOutputTypes);
		Assert.Equal(101, parameters.MaxVsizeAllocationPerAlice);

		// The numbers of inputs and the timeouts are the ones of the other rounds of the coordinator.
		Assert.Equal(20, parameters.MinInputCountByRound);
		Assert.Equal(40, parameters.MaxInputCountByRound);
		Assert.Equal(config.StandardInputRegistrationTimeout, parameters.StandardInputRegistrationTimeout);

		Assert.True(ZeroLinkRules.TryGetDenomination(parameters, out var denomination));
		Assert.Equal(Denomination, denomination);
	}

	/// <summary>A round of one participant is no coinjoin, whatever the configuration says.</summary>
	[Fact]
	public void RoundIsNotHeldWithFewerThanTwoInputs()
	{
		var config = new WabiSabiConfig { MaxInputCountByRound = 2, MinInputCountByRoundMultiplier = 0.1 };
		Assert.Equal(1, config.MinInputCountByRound);

		Assert.Equal(2, ZeroLinkRules.CreateRoundParameters(config, new FeeRate(1m), Denomination).MinInputCountByRound);
		Assert.Equal(2, ZeroLinkRules.CreateRoundParameters(config, new FeeRate(1m), Denomination, minInputCountByRound: 1).MinInputCountByRound);
	}

	/// <summary>
	/// Nothing marks a ZeroLink round but its parameters. A round that differs from the style in one point is not one,
	/// and a client does not treat it as one.
	/// </summary>
	[Fact]
	public void StyleOfItsParametersIsWhatMakesAZeroLinkRound()
	{
		var config = new WabiSabiConfig();
		var feeRate = new FeeRate(2.5m);
		var parameters = ZeroLinkRules.CreateRoundParameters(config, feeRate, Denomination);
		var outputAmount = parameters.AllowedOutputAmounts.Min;

		Assert.True(ZeroLinkRules.IsZeroLinkRound(parameters));

		// The round of a coordinator that takes any amount.
		Assert.False(ZeroLinkRules.IsZeroLinkRound(RoundParameters.Create(config, feeRate)));

		// More than one input amount, or an amount that is not a standard denomination.
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { AllowedInputAmounts = new MoneyRange(Denomination, Denomination + Money.Satoshis(1)) }));
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with
		{
			AllowedInputAmounts = new MoneyRange(Money.Satoshis(10_000_001), Money.Satoshis(10_000_001)),
			AllowedOutputAmounts = new MoneyRange(Money.Satoshis(10_000_001 - 278), Money.Satoshis(10_000_001 - 278))
		}));

		// Outputs of another amount than the denomination less the fee: a satoshi more, a satoshi less, or a range.
		foreach (var range in new[]
		{
			new MoneyRange(outputAmount + Money.Satoshis(1), outputAmount + Money.Satoshis(1)),
			new MoneyRange(outputAmount - Money.Satoshis(1), outputAmount - Money.Satoshis(1)),
			new MoneyRange(outputAmount - Money.Satoshis(1), outputAmount),
			new MoneyRange(Denomination, Denomination)
		})
		{
			Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { AllowedOutputAmounts = range }));
		}

		// The fee rate of the round decides the output amount, so another fee rate with the same amounts is no ZeroLink round.
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { MiningFeeRate = new FeeRate(3m) }));

		// Other kinds of inputs or outputs.
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { AllowedInputTypes = [ScriptType.P2WPKH, ScriptType.Taproot] }));
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { AllowedOutputTypes = [ScriptType.P2WPKH] }));

		// Room for more than one input and one output.
		Assert.False(ZeroLinkRules.IsZeroLinkRound(parameters with { MaxVsizeAllocationPerAlice = 255 }));
	}

	/// <summary>
	/// An input is worth one output and no more: what is left of its credentials after its output is the fee of the
	/// overhead, and it has no size left for another output either.
	/// </summary>
	[Theory]
	[InlineData(100)]
	[InlineData(1_000)]
	[InlineData(2_500)]
	[InlineData(134_714)]
	public void InputPaysForOneOutput(long feePerK)
	{
		var feeRate = new FeeRate(Money.Satoshis(feePerK));
		var parameters = ZeroLinkRules.CreateRoundParameters(new WabiSabiConfig(), feeRate, Denomination);
		using Key key = new();
		var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		var amountCredentials = new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Denomination, script)).EffectiveValue(feeRate);
		var costOfAnOutput = new TxOut(parameters.AllowedOutputAmounts.Min, script).EffectiveCost(feeRate);

		Assert.True(amountCredentials >= costOfAnOutput);
		Assert.True(amountCredentials < 2 * costOfAnOutput);

		// What is left is the fee of about 10 vB: never less than nothing.
		var left = amountCredentials - costOfAnOutput;
		Assert.InRange(left.Satoshi, feePerK * 10 / 1000, (feePerK * 10 / 1000) + 3);

		var vsizeCredentials = parameters.MaxVsizeAllocationPerAlice - script.EstimateInputVsize();
		Assert.Equal(script.EstimateOutputVsize(), vsizeCredentials);
	}

	[Fact]
	public void RoundOfAnAmountThatIsNotStandardCannotBeMade()
	{
		var exception = Assert.Throws<ArgumentException>(() => ZeroLinkRules.CreateRoundParameters(new WabiSabiConfig(), new FeeRate(1m), Money.Coins(0.3m)));

		Assert.Contains("0.30 BTC is not a standard denomination", exception.Message);
	}

	/// <summary>A denomination that the fee eats, or leaves dust of, has no round until the fee rate comes down.</summary>
	[Theory]
	[InlineData(5_000, 1_000, true)] // 5,000 sats at 1 sat/vB: 4,889 sats are left.
	[InlineData(5_000, 40_000, true)] // At 40 sat/vB: 560 sats.
	[InlineData(5_000, 43_000, false)] // At 43 sat/vB: 227 sats, which is dust.
	[InlineData(5_000, 45_045, false)] // The fee is exactly the denomination.
	[InlineData(5_000, 100_000, false)] // The fee is more than the denomination.
	[InlineData(10_000_000, 100_000, true)]
	public void DenominationHasARoundWhileTheFeeLeavesAnOutput(long denomination, long feePerK, bool expected)
	{
		var feeRate = new FeeRate(Money.Satoshis(feePerK));

		Assert.Equal(expected, ZeroLinkRules.CanHoldRound(Money.Satoshis(denomination), feeRate));

		if (!expected)
		{
			Assert.Throws<ArgumentException>(() => ZeroLinkRules.CreateRoundParameters(new WabiSabiConfig(), feeRate, Money.Satoshis(denomination)));
		}
	}

	[Fact]
	public void TransactionOfInputsAndOutputsOfTheRoundIsByTheRules()
	{
		var (parameters, inputs, outputs) = CreateRound(participants: 3);

		Assert.Null(ZeroLinkRules.FindViolation(parameters, inputs, outputs));

		// A participant that registered no output only harms itself: the others have what the round promised them.
		Assert.Null(ZeroLinkRules.FindViolation(parameters, inputs, outputs[..2]));
	}

	[Fact]
	public void TransactionWithAnotherAmountIsAgainstTheRules()
	{
		var (parameters, inputs, outputs) = CreateRound(participants: 3);
		using Key key = new();
		var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		// An input of another amount: it would stand out, and so would whatever it pays for.
		Coin[] withLargerInput = [.. inputs[..2], new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Denomination + Money.Satoshis(1), script))];
		Assert.Equal("An input holds 0.10000001 BTC instead of 0.10 BTC.", ZeroLinkRules.FindViolation(parameters, withLargerInput, outputs));

		// An output of another amount, such as change or what a coordinator would take.
		TxOut[] withSmallerOutput = [.. outputs[..2], new TxOut(outputs[0].Value - Money.Satoshis(1), script)];
		Assert.Equal("An output holds 0.09999721 BTC instead of 0.09999722 BTC.", ZeroLinkRules.FindViolation(parameters, inputs, withSmallerOutput));
		Assert.StartsWith("An output holds 0.00001 BTC", ZeroLinkRules.FindViolation(parameters, inputs, [.. outputs[..2], new TxOut(Money.Satoshis(1_000), script)]));
	}

	[Fact]
	public void TransactionWithMoreOutputsThanInputsIsAgainstTheRules()
	{
		var (parameters, inputs, outputs) = CreateRound(participants: 3);

		Assert.Equal("There are 3 outputs for 2 inputs.", ZeroLinkRules.FindViolation(parameters, inputs[..2], outputs));
	}

	[Fact]
	public void TransactionWithOtherKindsOfInputsOrOutputsIsAgainstTheRules()
	{
		var (parameters, inputs, outputs) = CreateRound(participants: 2);
		using Key key = new();
		var segwitScript = key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);

		Assert.Equal("An input is not a Taproot one.", ZeroLinkRules.FindViolation(parameters, [inputs[0], new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Denomination, segwitScript))], outputs));
		Assert.Equal("An output is not a Taproot one.", ZeroLinkRules.FindViolation(parameters, inputs, [outputs[0], new TxOut(outputs[0].Value, segwitScript)]));
	}

	[Fact]
	public void TransactionOfARoundOfAnotherKindIsNotJudged()
	{
		var (_, inputs, outputs) = CreateRound(participants: 2);
		var parameters = RoundParameters.Create(new WabiSabiConfig(), new FeeRate(2.5m));

		Assert.Equal("The round is not a ZeroLink round.", ZeroLinkRules.FindViolation(parameters, inputs, outputs));
	}

	[Fact]
	public void DenominationsOfACoordinatorAreStandardOnesThatItRegisters()
	{
		Assert.Null(new WabiSabiConfig().FindZeroLinkDenominationProblem());
		Assert.Null(new WabiSabiConfig { ZeroLinkDenominations = [Money.Coins(0.1m), Money.Satoshis(1_594_323)] }.FindZeroLinkDenominationProblem());

		Assert.StartsWith(
			"0.30 BTC in ZeroLinkDenominations is not a standard denomination.",
			new WabiSabiConfig { ZeroLinkDenominations = [Money.Coins(0.1m), Money.Coins(0.3m)] }.FindZeroLinkDenominationProblem());

		// 0.00002 BTC is a standard denomination, but less than the coordinator registers.
		Assert.Equal(
			"0.00002 BTC in ZeroLinkDenominations is not between MinRegistrableAmount and MaxRegistrableAmount.",
			new WabiSabiConfig { ZeroLinkDenominations = [Money.Satoshis(2_000)] }.FindZeroLinkDenominationProblem());
		Assert.Equal(
			"100.00 BTC in ZeroLinkDenominations is not between MinRegistrableAmount and MaxRegistrableAmount.",
			new WabiSabiConfig { MaxRegistrableAmount = Money.Coins(50), ZeroLinkDenominations = [Money.Coins(100)] }.FindZeroLinkDenominationProblem());

		Assert.Equal(
			"ZeroLinkDenominations names a denomination twice.",
			new WabiSabiConfig { ZeroLinkDenominations = [Money.Coins(0.1m), Money.Satoshis(10_000_000)] }.FindZeroLinkDenominationProblem());

		Assert.StartsWith(
			"ZeroLinkDenominations needs AllowP2trInputs and AllowP2trOutputs",
			new WabiSabiConfig { AllowP2trOutputs = false, ZeroLinkDenominations = [Money.Coins(0.1m)] }.FindZeroLinkDenominationProblem());
	}

	/// <summary>
	/// The coordinator keeps the list in its file. A file without the list, which every file from before the ZeroLink
	/// rounds is, means no such rounds. A list that cannot be read means that the file is not taken, instead of the
	/// coordinator holding no ZeroLink rounds without a word.
	/// </summary>
	[Fact]
	public void DenominationsAreKeptInTheFileOfTheCoordinator()
	{
		const string EmptyList = "\"ZeroLinkDenominations\": []";

		var decode = JsonDecoder.FromString(Decode.WabiSabiConfig("Config.json"));
		var json = JsonEncoder.ToReadableString(new WabiSabiConfig { ZeroLinkDenominations = [Money.Coins(0.1m), Money.Satoshis(1_594_323)] }, Encode.WabiSabiConfig);

		Assert.Contains("\"ZeroLinkDenominations\": [", json);
		Assert.Equal([Money.Coins(0.1m), Money.Satoshis(1_594_323)], decode(json).Value.ZeroLinkDenominations);

		// A file of a coordinator without ZeroLink rounds, and a file from before there were any.
		var jsonWithoutRounds = JsonEncoder.ToReadableString(new WabiSabiConfig(), Encode.WabiSabiConfig);
		Assert.Contains(EmptyList, jsonWithoutRounds);
		Assert.Empty(decode(jsonWithoutRounds).Value.ZeroLinkDenominations);

		var jsonFromBefore = jsonWithoutRounds.Replace(EmptyList + ",", "");
		Assert.DoesNotContain("ZeroLinkDenominations", jsonFromBefore);
		Assert.Empty(decode(jsonFromBefore).Value.ZeroLinkDenominations);

		// An amount that is no amount, an amount without its quotation marks, and something that is no list.
		foreach (var unreadable in new[] { "[\"0.1\", \"abc\"]", "[0.1]", "\"0.1\"" })
		{
			var result = decode(jsonWithoutRounds.Replace(EmptyList, $"\"ZeroLinkDenominations\": {unreadable}"));

			Assert.False(result.IsOk);
			Assert.StartsWith("ZeroLinkDenominations cannot be read: it is a list of amounts in BTC, each one in quotation marks.", result.Error);
		}
	}

	/// <summary>The inputs and the outputs of a round of 0.1 BTC at 2.5 sat/vB, in which everybody registered an output.</summary>
	private static (RoundParameters Parameters, Coin[] Inputs, TxOut[] Outputs) CreateRound(int participants)
	{
		var parameters = ZeroLinkRules.CreateRoundParameters(new WabiSabiConfig(), new FeeRate(2.5m), Denomination);

		Script CreateScript()
		{
			using Key key = new();
			return key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
		}

		var inputs = Enumerable.Range(0, participants).Select(_ => new Coin(BitcoinFactory.CreateOutPoint(), new TxOut(Denomination, CreateScript()))).ToArray();
		var outputs = Enumerable.Range(0, participants).Select(_ => new TxOut(parameters.AllowedOutputAmounts.Min, CreateScript())).ToArray();

		return (parameters, inputs, outputs);
	}

	/// <summary>A transaction of Taproot inputs and outputs with the witnesses that a key path spend has: one signature of 64 bytes.</summary>
	private static Transaction CreateSignedTransaction(int inputCount, int outputCount)
	{
		using Key key = new();
		var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
		var transaction = Transaction.Create(Network.Main);

		for (var i = 0; i < inputCount; i++)
		{
			transaction.Inputs.Add(new OutPoint(uint256.One, i)).WitScript = new WitScript(Op.GetPushOp(new byte[64]));
		}

		for (var i = 0; i < outputCount; i++)
		{
			transaction.Outputs.Add(Money.Coins(0.1m), script);
		}

		return transaction;
	}
}
