using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.IntegrationTests.BitcoinCore;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Userfacing;

namespace WalletWasabi.IntegrationTests.Infrastructure;

/// <summary>
/// A Bitcoin Core that broadcasts privately, as the wallet takes the node of its user to do, with a network to
/// broadcast to. The node has <c>privatebroadcast=1</c> and a SOCKS5 proxy for its way to Tor. The proxy is one of
/// the tests, and whatever the node asks it for, it connects the node to a second Bitcoin Core: that one is the
/// network. The second node also keeps an ordinary connection to the first one, over which the first one hears its
/// transactions back.
/// </summary>
public class PrivateBroadcastTestFixture : IntegrationTestFixture
{
	private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

	private CoreNode? _peerNode;
	private TestSocks5Proxy? _proxy;
	private RPCClient? _node;

	/// <summary>
	/// The node of the wallet, asked without a cache in between. The tests look at its mempool from one second to
	/// the next, and <see cref="IntegrationTestFixture.WalletRpcClient"/> remembers an answer for a few seconds.
	/// </summary>
	public IRPCClient NodeRpcClient { get; private set; } = null!;

	/// <summary>
	/// Whether the Bitcoin Core of the tests can broadcast privately. It can since version 31. A build environment that
	/// brings an older one along has nothing to run these tests against, so they are skipped there.
	/// </summary>
	public bool IsPrivateBroadcastSupported { get; private set; }

	/// <summary>What a test says when it is skipped for that.</summary>
	public const string NotSupportedReason = "The Bitcoin Core of the tests is older than version 31 and cannot broadcast privately.";

	/// <summary>What the node takes for Tor. While it refuses connections, nothing leaves the node.</summary>
	public TestSocks5Proxy Proxy => _proxy ?? throw new InvalidOperationException("The fixture is not initialized.");

	public override async ValueTask InitializeAsync()
	{
		await base.InitializeAsync();

		var node = _node = CreateRpcClient(BitcoinCoreNode);
		var peer = CreateRpcClient(_peerNode!);
		NodeRpcClient = new RpcClientBase(node);

		try
		{
			await NodeRpcClient.GetPrivateBroadcastQueueAsync();
			IsPrivateBroadcastSupported = true;
		}
		catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND)
		{
			// An older Bitcoin Core. It took the setting without a word and broadcasts the way it always did.
			return;
		}

		// The second node gets the chain from the first one, and tells it afterwards what it hears of.
		await peer.AddNodeAsync(BitcoinCoreNode.P2pEndPoint, onetry: true);

		var blockCount = await node.GetBlockCountAsync();
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);

		while (await peer.GetBlockCountAsync() < blockCount)
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException("The second node did not get the chain of the first one.");
			}

			await Task.Delay(200);
		}

		await AddPeerAddressesAsync();
	}

	/// <summary>
	/// Tells the node of onion services to broadcast to. They are made up: the proxy leads to the second node
	/// whichever of them the node picks. The node does not pick an address twice within ten minutes, and while the
	/// proxy refuses connections it goes through all that it knows within seconds. So a test that needs the network
	/// brings new addresses along.
	/// </summary>
	public async Task AddPeerAddressesAsync(int count = 300)
	{
		var node = _node ?? throw new InvalidOperationException("The fixture is not initialized.");

		for (var i = 0; i < count; i++)
		{
			await node.SendCommandAsync("addpeeraddress", CreateOnionAddress(), 8333, false);
		}
	}

	public override async ValueTask DisposeAsync()
	{
		await base.DisposeAsync();

		if (_peerNode is { } peerNode)
		{
			_peerNode = null;
			await peerNode.TryStopAsync();
		}

		if (_proxy is { } proxy)
		{
			_proxy = null;
			await proxy.DisposeAsync();
		}
	}

	protected override async Task<CoreNode> CreateNodeAsync()
	{
		_peerNode = await TestNodeBuilder.CreateAsync(callerFilePath: "IntegrationTests", callerMemberName: "PrivateBroadcastPeerData");
		_proxy = new TestSocks5Proxy((IPEndPoint)_peerNode.P2pEndPoint);

		return await TestNodeBuilder.CreateAsync(
			callerFilePath: "IntegrationTests",
			callerMemberName: "PrivateBroadcastNodeData",
			configure: parameters =>
			{
				parameters.PrivateBroadcast = 1;
				parameters.Proxy = _proxy.EndPoint.ToString();
			});
	}

	private static RPCClient CreateRpcClient(CoreNode node) =>
		new(node.RpcClient.CredentialString.ToString(), node.RpcEndPoint.ToString(node.Network.DefaultPort), node.Network);

	/// <summary>The address of an onion service that does not exist: a random key with the checksum and the version that make it a valid address.</summary>
	private static string CreateOnionAddress()
	{
		var key = RandomNumberGenerator.GetBytes(32);
		byte[] version = [3];
		var checksum = SHA3_256.HashData([.. Encoding.ASCII.GetBytes(".onion checksum"), .. key, .. version]);
		byte[] address = [.. key, checksum[0], checksum[1], .. version];

		// Base32, five bits a letter. The 35 bytes of an address make 56 letters without a remainder.
		var bits = string.Concat(address.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
		var letters = Enumerable.Range(0, bits.Length / 5).Select(i => Base32Alphabet[Convert.ToInt32(bits.Substring(i * 5, 5), 2)]);

		return string.Concat(letters) + ".onion";
	}
}
