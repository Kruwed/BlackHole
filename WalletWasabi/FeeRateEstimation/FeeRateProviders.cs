using WalletWasabi.BitcoinRpc;

namespace WalletWasabi.FeeRateEstimation;

public delegate Task<FeeRateEstimations> FeeRateProvider(CancellationToken cancellationToken);

public static class FeeRateProviders
{
	/// <summary>
	/// The fee rate estimations of the user's node. No third party is asked about fees.
	/// </summary>
	public static FeeRateProvider RpcAsync(IRPCClient rpcClient) =>
		async cancellationToken =>
		{
			var estimations = await rpcClient.EstimateAllFeeAsync(cancellationToken).ConfigureAwait(false);
			Logger.LogInfo($"Fetched fee rate estimations from RPC node: {estimations}.");
			return estimations;
		};
}
