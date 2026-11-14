using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BitcoinRpc;

/// <summary>
/// Tests for <see cref="RpcSettings"/>: the address and the credentials of the node, the way the user writes them.
/// </summary>
public class RpcSettingsTests
{
	[Theory]
	[InlineData("http://localhost:8332", "http://localhost:8332/")]
	[InlineData("  http://127.0.0.1:18443/ ", "http://127.0.0.1:18443/")]
	[InlineData("https://node.example.org:8332/", "https://node.example.org:8332/")]
	[InlineData("HTTP://192.168.1.5:8332", "http://192.168.1.5:8332/")]
	[InlineData("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuvwx.onion:8332", "http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuvwx.onion:8332/")]
	public void ReadsHttpAddresses(string text, string expected)
	{
		Assert.True(RpcSettings.TryParseUri(text, out var uri));
		Assert.Equal(expected, uri.ToString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("localhost:8332")] // An absolute address with the scheme "localhost".
	[InlineData("127.0.0.1:8332")]
	[InlineData("localhost")]
	[InlineData("/var/run/bitcoind.sock")]
	[InlineData("ftp://localhost:8332")]
	[InlineData("tcp://localhost:8332")]
	public void RefusesWhatIsNotAnHttpAddress(string? text)
	{
		Assert.False(RpcSettings.TryParseUri(text, out var uri));
		Assert.Null(uri);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("  ")]
	public void NoCredentialsStandForTheCookieFileOfBitcoinCore(string? text)
	{
		Assert.True(RpcSettings.TryParseCredentials(text, out var credentials));
		Assert.True(credentials.UseDefault);
	}

	[Fact]
	public void ReadsUserAndPassword()
	{
		Assert.True(RpcSettings.TryParseCredentials(" satoshi:pass:word ", out var credentials));

		Assert.False(credentials.UseDefault);
		Assert.Equal("satoshi", credentials.UserPassword.UserName);
		Assert.Equal("pass:word", credentials.UserPassword.Password);
	}

	[Fact]
	public void ReadsCookieFile()
	{
		Assert.True(RpcSettings.TryParseCredentials("cookiefile=/data/bitcoin/.cookie", out var credentials));

		Assert.False(credentials.UseDefault);
		Assert.Equal("/data/bitcoin/.cookie", credentials.CookieFile);
	}

	[Theory]
	[InlineData("satoshi")]
	[InlineData("no credentials")]
	public void RefusesWhatAreNoCredentials(string text)
	{
		Assert.False(RpcSettings.TryParseCredentials(text, out var credentials));
		Assert.Null(credentials);
	}

	[Fact]
	public void KnowsOnionServices()
	{
		Assert.True(RpcSettings.IsOnion(new Uri("http://abcdefghijklmnopqrstuvwxyz234567abcdefghijklmnopqrstuvwx.onion:8332")));
		Assert.True(RpcSettings.IsOnion(new Uri("http://ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQRSTUVWX.ONION")));
		Assert.False(RpcSettings.IsOnion(new Uri("http://localhost:8332")));
		Assert.False(RpcSettings.IsOnion(new Uri("http://onion.example.org:8332")));
	}

	[Fact]
	public void DefaultAddressIsTheOneOfBitcoinCore()
	{
		Assert.Equal(Constants.DefaultMainNetBitcoinRpcUri, RpcSettings.GetDefaultUri(Network.Main));
		Assert.Equal(Constants.DefaultTestNetBitcoinRpcUri, RpcSettings.GetDefaultUri(Network.TestNet));
		Assert.Equal(Constants.DefaultSignetBitcoinRpcUri, RpcSettings.GetDefaultUri(Bitcoin.Instance.Signet));
		Assert.Equal(Constants.DefaultRegTestBitcoinRpcUri, RpcSettings.GetDefaultUri(Network.RegTest));

		// Every default is an address that can be asked.
		foreach (var network in new[] { Network.Main, Network.TestNet, Bitcoin.Instance.Signet, Network.RegTest })
		{
			Assert.True(RpcSettings.TryParseUri(RpcSettings.GetDefaultUri(network), out _));
		}
	}
}
