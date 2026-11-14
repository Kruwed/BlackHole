using System.Diagnostics.CodeAnalysis;
using NBitcoin.RPC;
using WalletWasabi.Helpers;

namespace WalletWasabi.BitcoinRpc;

/// <summary>
/// Reads the two settings that tell where the user's node is and how to authenticate to it, the way the user writes them.
/// </summary>
public static class RpcSettings
{
	/// <summary>
	/// The address where Bitcoin Core listens for RPC requests on the given network when it runs on this computer and nothing else was configured.
	/// </summary>
	public static string GetDefaultUri(Network network) =>
		network == Network.Main ? Constants.DefaultMainNetBitcoinRpcUri
		: network == Network.TestNet ? Constants.DefaultTestNetBitcoinRpcUri
		: network == Network.RegTest ? Constants.DefaultRegTestBitcoinRpcUri
		: Constants.DefaultSignetBitcoinRpcUri;

	/// <summary>
	/// Reads the address of the RPC interface of a node: an absolute HTTP or HTTPS address, for example <c>http://localhost:8332</c>.
	/// </summary>
	public static bool TryParseUri(string? text, [NotNullWhen(true)] out Uri? uri)
	{
		// "localhost:8332" is an absolute address too, with the scheme "localhost". Nothing can be asked there.
		if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
		{
			uri = parsed;
			return true;
		}

		uri = null;
		return false;
	}

	/// <summary>
	/// Reads RPC credentials: <c>user:password</c>, or <c>cookiefile=</c> followed by the path of the cookie file of the node.
	/// Nothing at all stands for the cookie file in the default data folder of Bitcoin Core.
	/// </summary>
	public static bool TryParseCredentials(string? text, [NotNullWhen(true)] out RPCCredentialString? credentials)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			credentials = new RPCCredentialString();
			return true;
		}

		if (RPCCredentialString.TryParse(text.Trim(), out var parsed))
		{
			credentials = parsed;
			return true;
		}

		credentials = null;
		return false;
	}

	/// <summary>Tells whether the node is an onion service, which can only be reached through Tor.</summary>
	public static bool IsOnion(Uri uri) =>
		uri.DnsSafeHost.EndsWith(".onion", StringComparison.OrdinalIgnoreCase);
}
