using WalletWasabi.WabiSabi.Client.CoinJoin.Client.Decomposer;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;

namespace WalletWasabi.WabiSabi.Client;

public class OutputProvider
{
	public OutputProvider(IDestinationProvider destinationProvider, RandomnessProvider random)
	{
		DestinationProvider = destinationProvider;
		Random = random;
	}

	internal IDestinationProvider DestinationProvider { get; }
	protected RandomnessProvider Random { get; }

	public virtual IEnumerable<TxOut> GetOutputs(
		uint256 roundId,
		RoundParameters roundParameters,
		ImmutableArray<OutPoint> registeredInputs,
		IEnumerable<Money> registeredCoinEffectiveValues,
		IEnumerable<Money> theirCoinEffectiveValues,
		int availableVsize,
		bool arePaymentsAllowed)
	{
		AmountDecomposer amountDecomposer = new(
			roundParameters.MiningFeeRate,
			roundParameters.CalculateMinReasonableOutputAmount(DestinationProvider.SupportedScriptTypes),
			roundParameters.AllowedOutputAmounts.Max,
			availableVsize,
			DestinationProvider.SupportedScriptTypes,
			Random);

		var allCoinEffectiveValues = theirCoinEffectiveValues.Concat(registeredCoinEffectiveValues);
		var outputValues = amountDecomposer.Decompose(registeredCoinEffectiveValues.Sum(), allCoinEffectiveValues).ToArray();
		return GetTxOuts(outputValues, DestinationProvider);
	}

	internal static IEnumerable<TxOut> GetTxOuts(IEnumerable<Output> outputValues, IDestinationProvider destinationProvider)
	{
		// Get as many destinations as outputs we need.
		var outputs = outputValues.ToArray();
		var destinations = new Stack<IDestination>(destinationProvider.GetNextDestinations(outputs.Length));

		List<TxOut> outputTxOuts = new();
		foreach (var output in outputs)
		{
			var destination = destinations.Pop();
			var txOut = new TxOut(output.Amount, destination.ScriptPubKey);
			outputTxOuts.Add(txOut);
		}
		return outputTxOuts;
	}
}
