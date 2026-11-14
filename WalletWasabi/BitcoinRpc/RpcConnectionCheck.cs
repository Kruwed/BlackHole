using System.IO;
using System.Net;
using System.Net.Http;
using NBitcoin.RPC;
using WalletWasabi.Helpers;

namespace WalletWasabi.BitcoinRpc;

/// <summary>
/// Finds out whether the application can work with a node: the node must answer RPC requests, and it must be on the network of the application.
/// </summary>
public static class RpcConnectionCheck
{
	/// <param name="timeout">How long the node is given to answer.</param>
	/// <returns>Nothing if the node can be used, otherwise the reason why it cannot, in words for the user.</returns>
	public static async Task<Result<string>> CheckAsync(IRPCClient rpcClient, Network network, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(timeout);

		try
		{
			// A node answers this only to somebody who has the right credentials.
			await rpcClient.GetBlockchainInfoAsync(timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			// The first block tells the networks apart.
			var genesisHash = await rpcClient.GetBlockHashAsync(0, timeoutCts.Token).WaitAsync(timeoutCts.Token).ConfigureAwait(false);

			return genesisHash == network.GenesisHash
				? Result<string>.Ok()
				: Result<string>.Fail($"The Bitcoin node is on another network, not on {network}.");
		}
		catch (Exception ex)
		{
			// Whoever asked for the check has given up on it: there is nothing to tell.
			cancellationToken.ThrowIfCancellationRequested();

			return Result<string>.Fail(
				ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } && rpcClient.CredentialString.UseDefault
					? DescribeRefusedCookie(network)
					: RpcErrorTools.DescribeNodeFailure(ex));
		}
	}

	/// <summary>
	/// The node wants credentials and none were given, so all there was to try is the cookie file that a Bitcoin Core on this computer leaves in its default data folder.
	/// </summary>
	private static string DescribeRefusedCookie(Network network)
	{
		var cookieFilePath = RPCClient.TryGetDefaultCookieFilePath(network);

		return cookieFilePath is not null && File.Exists(cookieFilePath)
			? $"The Bitcoin node refused the credentials of the cookie file {cookieFilePath}."
			: $"The Bitcoin node wants credentials, and no cookie file was found at {cookieFilePath ?? "the default place"}.";
	}
}
