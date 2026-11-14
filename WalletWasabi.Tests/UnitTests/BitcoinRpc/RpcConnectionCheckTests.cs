using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BitcoinRpc;

/// <summary>
/// Tests for <see cref="RpcConnectionCheck"/>: a node is taken only if it answers and if it is on the network of the application.
/// </summary>
public class RpcConnectionCheckTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	[Fact]
	public async Task AcceptsNodeOnTheNetworkOfTheApplicationAsync()
	{
		var node = new FakeBitcoinNode(Network.RegTest);
		node.MineBlocks(3);

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.True(result.IsOk);
	}

	[Fact]
	public async Task RefusesNodeOnAnotherNetworkAsync()
	{
		var node = new FakeBitcoinNode(Network.RegTest);

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.Main, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node is on another network, not on Main.", result.Error);
	}

	[Fact]
	public async Task TellsThatCredentialsWereRefusedAsync()
	{
		var node = new FakeBitcoinNode(Network.RegTest) { Failure = new HttpRequestException("Unauthorized", inner: null, HttpStatusCode.Unauthorized) };
		node.RpcClient.CredentialString = RPCCredentialString.Parse("user:password");

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node refused the RPC credentials.", result.Error);
	}

	[Fact]
	public async Task TellsWhereTheCookieFileWasLookedForAsync()
	{
		// No credentials were given: all there is to try is the cookie file of a Bitcoin Core on this computer.
		var node = new FakeBitcoinNode(Network.RegTest) { Failure = new HttpRequestException("Unauthorized", inner: null, HttpStatusCode.Unauthorized) };
		Assert.True(node.RpcClient.CredentialString.UseDefault);

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Contains("cookie file", result.Error);
		Assert.Contains(RPCClient.TryGetDefaultCookieFilePath(Network.RegTest)!, result.Error);
	}

	[Fact]
	public async Task TellsThatTheComputerIsNotAllowedAsync()
	{
		var node = new FakeBitcoinNode(Network.RegTest) { Failure = new HttpRequestException("Forbidden", inner: null, HttpStatusCode.Forbidden) };

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node does not allow RPC requests from this computer.", result.Error);
	}

	[Fact]
	public async Task TellsWhyTheNodeCannotBeReachedAsync()
	{
		var node = new FakeBitcoinNode(Network.RegTest) { Failure = new HttpRequestException("Connection refused (127.0.0.1:18443)") };

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node cannot be reached (Connection refused (127.0.0.1:18443)).", result.Error);
	}

	[Fact]
	public async Task TellsWhatTheNodeAnsweredInsteadAsync()
	{
		// A node that is still starting answers like this.
		var node = new FakeBitcoinNode(Network.RegTest) { Failure = new RPCException(RPCErrorCode.RPC_IN_WARMUP, "Loading block index…", null) };

		var result = await RpcConnectionCheck.CheckAsync(node.RpcClient, Network.RegTest, Timeout, CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node refused a request (Loading block index…).", result.Error);
	}

	[Fact]
	public async Task GivesUpOnNodeThatDoesNotAnswerAsync()
	{
		var neverAnswers = new TaskCompletionSource<BlockchainInfo>();
		var rpcClient = new MockRpcClient { OnGetBlockchainInfoAsync = () => neverAnswers.Task };

		var result = await RpcConnectionCheck.CheckAsync(rpcClient, Network.RegTest, TimeSpan.FromMilliseconds(100), CancellationToken.None);

		Assert.False(result.IsOk);
		Assert.Equal("The Bitcoin node did not answer in time.", result.Error);
	}

	[Fact]
	public async Task StopsWhenNobodyWaitsForTheAnswerAsync()
	{
		var neverAnswers = new TaskCompletionSource<BlockchainInfo>();
		var rpcClient = new MockRpcClient { OnGetBlockchainInfoAsync = () => neverAnswers.Task };
		using var cts = new CancellationTokenSource();

		var check = RpcConnectionCheck.CheckAsync(rpcClient, Network.RegTest, Timeout, cts.Token);
		Assert.False(check.IsCompleted);

		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await check);
	}
}
