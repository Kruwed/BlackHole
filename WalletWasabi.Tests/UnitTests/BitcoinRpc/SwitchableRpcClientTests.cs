using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BitcoinRpc;

/// <summary>
/// Tests for <see cref="SwitchableRpcClient"/>. That the requests really go to the other node is tested with real nodes, in the integration tests.
/// </summary>
public class SwitchableRpcClientTests
{
	[Fact]
	public void TalksToTheNodeItWasSwitchedTo()
	{
		var first = new RPCClient(RPCCredentialString.Parse("first:password"), new Uri("http://127.0.0.1:18443"), Network.RegTest);
		var second = new RPCClient(RPCCredentialString.Parse("second:password"), new Uri("http://127.0.0.1:18444"), Network.RegTest);

		var client = new SwitchableRpcClient(first);
		IRPCClient seenByOthers = client;

		Assert.Equal("first", seenByOthers.CredentialString.UserPassword.UserName);

		client.SwitchTo(second);

		// Whoever holds the client sees the other node, without being told.
		Assert.Equal("second", seenByOthers.CredentialString.UserPassword.UserName);
		Assert.Equal(Network.RegTest, seenByOthers.Network);

		client.SwitchTo(first);

		Assert.Equal("first", seenByOthers.CredentialString.UserPassword.UserName);
	}
}
