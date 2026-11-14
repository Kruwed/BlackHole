using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Extensions;
using WalletWasabi.IntegrationTests.Infrastructure;
using Xunit;

namespace WalletWasabi.IntegrationTests.BitcoinCore.Tests;

/// <summary>
/// The application can be connected to another node while it runs. These tests ask a real node.
/// </summary>
public class NodeConnectionTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task RequestsGoToTheNodeThatTheClientWasSwitchedToAsync()
	{
		var coreNode = await TestNodeBuilder.CreateAsync();
		try
		{
			var credentials = coreNode.RpcClient.CredentialString;
			var nodeUri = coreNode.RpcEndPoint.ToUri("http");

			// Nothing listens where the client points to at first.
			var client = new SwitchableRpcClient(new RPCClient(credentials, UnusedAddress(), coreNode.Network));
			IRPCClient seenByOthers = client;

			await Assert.ThrowsAsync<HttpRequestException>(async () => await seenByOthers.GetBestBlockHashAsync());

			client.SwitchTo(new RPCClient(credentials, nodeUri, coreNode.Network));

			// Whoever holds the client reaches the node now, without being told.
			Assert.Equal(await coreNode.RpcClient.GetBestBlockHashAsync(), await seenByOthers.GetBestBlockHashAsync());
			Assert.Equal(coreNode.Network.GenesisHash, await seenByOthers.GetBlockHashAsync(0));
		}
		finally
		{
			await coreNode.TryStopAsync();
		}
	}

	[Fact]
	public async Task CheckTellsANodeThatWorksFromOneThatDoesNotAsync()
	{
		var coreNode = await TestNodeBuilder.CreateAsync();
		try
		{
			var network = coreNode.Network;
			var credentials = coreNode.RpcClient.CredentialString;
			var nodeUri = coreNode.RpcEndPoint.ToUri("http");

			// The node, with its credentials.
			var result = await RpcConnectionCheck.CheckAsync(Client(credentials, nodeUri), network, Timeout, CancellationToken.None);
			Assert.True(result.IsOk);

			// A wrong password.
			result = await RpcConnectionCheck.CheckAsync(Client(RPCCredentialString.Parse("nobody:wrong"), nodeUri), network, Timeout, CancellationToken.None);
			Assert.False(result.IsOk);
			Assert.Equal("The Bitcoin node refused the RPC credentials.", result.Error);

			// It is a regtest node: an application that is on mainnet must not take it.
			result = await RpcConnectionCheck.CheckAsync(Client(credentials, nodeUri), Network.Main, Timeout, CancellationToken.None);
			Assert.False(result.IsOk);
			Assert.Equal("The Bitcoin node is on another network, not on Main.", result.Error);

			// Nothing listens there.
			result = await RpcConnectionCheck.CheckAsync(Client(credentials, UnusedAddress()), network, Timeout, CancellationToken.None);
			Assert.False(result.IsOk);
			Assert.StartsWith("The Bitcoin node cannot be reached (", result.Error);
		}
		finally
		{
			await coreNode.TryStopAsync();
		}

		static IRPCClient Client(RPCCredentialString credentials, Uri uri) =>
			new RpcClientBase(new RPCClient(credentials, uri, Network.RegTest));
	}

	/// <summary>An address on this computer where nothing listens.</summary>
	private static Uri UnusedAddress()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();

		return new Uri($"http://127.0.0.1:{port}");
	}
}
