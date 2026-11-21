using System.Diagnostics.CodeAnalysis;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client.Decomposer;
using WalletWasabi.WabiSabi.Coordinator;

namespace WalletWasabi.ZeroLink;

/// <summary>
/// The rules of a ZeroLink round. A ZeroLink round is a WabiSabi round, with its credentials and its phases, that is
/// styled strictly: every input has the same amount, a standard denomination; every output has the same amount, the
/// denomination less the fee of a transaction with one input and one output; and a participant registers one input
/// and one output. So the transaction has no change, and nothing in it tells which inputs belong together.
/// </summary>
/// <remarks>
/// The style is the definition. A coordinator makes a ZeroLink round by giving it the parameters that
/// <see cref="CreateRoundParameters"/> makes, and a client knows a ZeroLink round by them:
/// <see cref="TryGetDenomination"/> tells. Nothing else marks the round, so what a client takes for a ZeroLink round
/// is one that the coordinator can only hold by these rules: the amounts are checked against the parameters with
/// every registration, and the parameters are part of what the identifier of the round is computed from.
/// </remarks>
public static class ZeroLinkRules
{
	/// <summary>
	/// The virtual size that every participant pays the fee of: a transaction with one Taproot input and one Taproot
	/// output. The input is 57.5 vB and the output 43 vB, and what a transaction has besides its inputs and outputs is
	/// 10.5 vB: the version, the lock time, the two counts, and the marker and the flag of SegWit. Together that is
	/// 444 weight units. A round has that overhead once, so its transaction pays a little more than the fee rate.
	/// </summary>
	public const int ParticipantVsize = 111;

	/// <summary>
	/// What the vsize credentials of a round count for one Taproot input and one Taproot output. A participant is
	/// given no more than that, so no input can pay for a second output.
	/// </summary>
	public const int VsizeAllocationPerAlice = Constants.P2trInputVirtualSize + Constants.P2trOutputVirtualSize;

	/// <summary>The inputs and the outputs of a round are Taproot ones.</summary>
	public static readonly ImmutableSortedSet<ScriptType> ScriptTypes = [ScriptType.Taproot];

	/// <summary>
	/// What the inputs of a round are signed with: the default of Taproot, which signs all of the transaction and
	/// leaves the signature at 64 bytes. With a signature hash that is named, the signature has a byte more and the
	/// input is 57.75 vB instead of 57.5 vB.
	/// </summary>
	public const TaprootSigHash SigHash = TaprootSigHash.Default;

	/// <summary>The size of the signature of an input: one of Schnorr, without a byte for the signature hash.</summary>
	public const int SignatureSize = 64;

	/// <summary>The fee that every participant of a round pays at the given fee rate, rounded up to a whole satoshi.</summary>
	public static Money GetFee(FeeRate feeRate) =>
		Money.Satoshis(((feeRate.FeePerK.Satoshi * ParticipantVsize) + 999) / 1000);

	/// <summary>The amount of every output of a round: the denomination less the fee.</summary>
	public static Money GetOutputAmount(Money denomination, FeeRate feeRate) =>
		denomination - GetFee(feeRate);

	/// <summary>
	/// Whether the amount is one of the standard denominations that WabiSabi decomposes amounts into: in satoshis, a
	/// power of two, a power of three or twice that, or one, two or five times a power of ten.
	/// </summary>
	public static bool IsStandardDenomination(Money amount) =>
		amount > Money.Zero && DenominationBuilder.CreateDenominations(amount, amount).Count == 1;

	/// <summary>
	/// Whether a round of the denomination can be held at the fee rate: what is left of the denomination after the fee
	/// has to be an output that nodes relay.
	/// </summary>
	public static bool CanHoldRound(Money denomination, FeeRate feeRate)
	{
		var outputAmount = GetOutputAmount(denomination, feeRate);

		return outputAmount > Money.Zero && !new TxOut(outputAmount, TaprootScriptForSizing).IsDust();
	}

	/// <summary>Makes the parameters of a ZeroLink round of the given denomination.</summary>
	/// <param name="config">What the coordinator is configured with: the timeouts and the numbers of inputs are the ones of its other rounds.</param>
	/// <param name="minInputCountByRound">The fewest inputs that the round is held with, if not the one of the configuration. A round of one input is no coinjoin, so it is two at least.</param>
	/// <exception cref="ArgumentException">The amount is not a standard denomination, or the fee leaves nothing of it.</exception>
	public static RoundParameters CreateRoundParameters(WabiSabiConfig config, FeeRate feeRate, Money denomination, int? minInputCountByRound = null)
	{
		if (!IsStandardDenomination(denomination))
		{
			throw new ArgumentException($"{denomination.ToString(fplus: false, trimExcessZero: true)} BTC is not a standard denomination.", nameof(denomination));
		}

		if (!CanHoldRound(denomination, feeRate))
		{
			throw new ArgumentException($"At {feeRate.SatoshiPerByte} sat/vB the fee leaves no output of {denomination.ToString(fplus: false, trimExcessZero: true)} BTC.", nameof(feeRate));
		}

		var outputAmount = GetOutputAmount(denomination, feeRate);

		return new RoundParameters(
			config.Network,
			feeRate,
			Math.Max(2, minInputCountByRound ?? config.MinInputCountByRound),
			Math.Max(2, config.MaxInputCountByRound),
			new MoneyRange(denomination, denomination),
			new MoneyRange(outputAmount, outputAmount),
			ScriptTypes,
			ScriptTypes,
			config.StandardInputRegistrationTimeout,
			config.ConnectionConfirmationTimeout,
			config.OutputRegistrationTimeout,
			config.TransactionSigningTimeout,
			config.BlameInputRegistrationTimeout,
			config.CoordinatorIdentifier,
			config.DelayTransactionSigning)
		{
			MaxVsizeAllocationPerAlice = VsizeAllocationPerAlice
		};
	}

	/// <summary>
	/// Tells whether the parameters are those of a ZeroLink round, and of which denomination. They are if the round
	/// takes one input amount, a standard denomination, and one output amount, that denomination less the fee of a
	/// participant at the fee rate of the round, if inputs and outputs are Taproot ones, and if a participant is given
	/// the size of one input and one output.
	/// </summary>
	public static bool TryGetDenomination(RoundParameters parameters, [NotNullWhen(true)] out Money? denomination)
	{
		denomination = null;

		var inputAmount = parameters.AllowedInputAmounts.Min;

		if (parameters.AllowedInputAmounts.Max != inputAmount
			|| parameters.AllowedOutputAmounts.Min != parameters.AllowedOutputAmounts.Max
			|| parameters.AllowedOutputAmounts.Min != GetOutputAmount(inputAmount, parameters.MiningFeeRate)
			|| !parameters.AllowedInputTypes.SetEquals(ScriptTypes)
			|| !parameters.AllowedOutputTypes.SetEquals(ScriptTypes)
			|| parameters.MaxVsizeAllocationPerAlice != VsizeAllocationPerAlice
			|| !IsStandardDenomination(inputAmount)
			|| !CanHoldRound(inputAmount, parameters.MiningFeeRate))
		{
			return false;
		}

		denomination = inputAmount;
		return true;
	}

	/// <summary>Whether the parameters are those of a ZeroLink round.</summary>
	public static bool IsZeroLinkRound(RoundParameters parameters) =>
		TryGetDenomination(parameters, out _);

	/// <summary>
	/// Whether a witness is what spends an input of a round: one item, the signature of 64 bytes. Every input of the
	/// transaction looks the same that way and has the same size, 57.5 vB.
	/// </summary>
	public static bool IsInputWitness(WitScript witness) =>
		witness.PushCount == 1 && witness.GetUnsafePush(0).Length == SignatureSize;

	/// <summary>
	/// The virtual size of the transaction of a round with the given numbers of inputs and outputs, once it is signed.
	/// </summary>
	public static int GetTransactionVsize(int inputCount, int outputCount)
	{
		// Without the witnesses: the version, the two counts, the inputs (outpoint, empty script, sequence), the outputs
		// (amount, script) and the lock time.
		var baseSize = 4 + VarIntLength(inputCount) + (41 * inputCount) + VarIntLength(outputCount) + (43 * outputCount) + 4;

		// The marker and the flag, and for every input its witness: one item, a signature of 64 bytes.
		var witnessSize = 2 + (66 * inputCount);

		return ((baseSize * 4) + witnessSize + 3) / 4;
	}

	/// <summary>
	/// Tells what is against the rules in the transaction of a round, if anything is. The coordinator asks before it
	/// has the transaction signed, and every participant asks before it signs.
	/// </summary>
	/// <param name="parameters">The parameters of the round.</param>
	/// <param name="inputs">The coins that the transaction spends.</param>
	/// <param name="outputs">The outputs of the transaction.</param>
	/// <returns>What is wrong, or null if the transaction is one of a ZeroLink round.</returns>
	public static string? FindViolation(RoundParameters parameters, IReadOnlyCollection<Coin> inputs, IReadOnlyCollection<TxOut> outputs)
	{
		if (!TryGetDenomination(parameters, out var denomination))
		{
			return "The round is not a ZeroLink round.";
		}

		var outputAmount = GetOutputAmount(denomination, parameters.MiningFeeRate);

		if (inputs.FirstOrDefault(x => x.Amount != denomination) is { } wrongInput)
		{
			return $"An input holds {Format(wrongInput.Amount)} BTC instead of {Format(denomination)} BTC.";
		}

		if (inputs.Any(x => !x.ScriptPubKey.IsScriptType(ScriptType.Taproot)))
		{
			return "An input is not a Taproot one.";
		}

		if (outputs.FirstOrDefault(x => x.Value != outputAmount) is { } wrongOutput)
		{
			return $"An output holds {Format(wrongOutput.Value)} BTC instead of {Format(outputAmount)} BTC.";
		}

		if (outputs.Any(x => !x.ScriptPubKey.IsScriptType(ScriptType.Taproot)))
		{
			return "An output is not a Taproot one.";
		}

		if (outputs.Count > inputs.Count)
		{
			return $"There are {outputs.Count} outputs for {inputs.Count} inputs.";
		}

		// Every input pays for more than its share of the transaction, so this only fails with a transaction that the
		// rules above did not make.
		var fee = inputs.Sum(x => x.Amount.Satoshi) - outputs.Sum(x => x.Value.Satoshi);
		var vsize = GetTransactionVsize(inputs.Count, outputs.Count);

		if (fee * 1000 < parameters.MiningFeeRate.FeePerK.Satoshi * vsize)
		{
			return $"The fee of {fee} sats is less than {parameters.MiningFeeRate.SatoshiPerByte} sat/vB for {vsize} vB.";
		}

		return null;
	}

	/// <summary>A script of the form of a Taproot output: what counts for the dust limit is its size and its kind.</summary>
	private static Script TaprootScriptForSizing { get; } = new(OpcodeType.OP_1, Op.GetPushOp(new byte[32]));

	private static string Format(Money amount) => amount.ToString(fplus: false, trimExcessZero: true);

	private static int VarIntLength(int count) => count < 0xfd ? 1 : 3;
}
