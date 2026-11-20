using System.Linq;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tor;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client;

/// <summary>
/// How the application reaches the Nostr relays that the offer of the wallet is posted to.
/// </summary>
public class NostrPublisherTests
{
	/// <summary>
	/// With Tor, the relays are reached through its SOCKS port and in no other way: they do not get to see where the
	/// wallet is. Only when Tor is turned off are they reached directly. On regtest Tor is always off.
	/// </summary>
	[Theory]
	[InlineData("Main", "", true)]
	[InlineData("Main", "--usetor=enabled", true)]
	[InlineData("Main", "--usetor=enabledonlyrunning", true)]
	[InlineData("Main", "--usetor=disabled", false)]
	[InlineData("signet", "", true)]
	[InlineData("RegTest", "", false)]
	[InlineData("RegTest", "--usetor=enabled", false)]
	public void RelaysAreReachedThroughTorUnlessTorIsTurnedOff(string networkName, string option, bool expectedThroughTor)
	{
		var persistentConfig = PersistentConfigManager.DefaultMainNetConfig with { Network = NBitcoin.Network.GetNetwork(networkName)!, UseTor = "Enabled" };
		var config = new Config(persistentConfig, option == "" ? [] : [option]);
		var torSettings = new TorSettings(Common.GetWorkDir(), installationFolderPath: "", terminateOnExit: false, torMode: config.UseTor);

		var publisher = Global.CreateNostrPublisher(config, torSettings);

		Assert.Equal(expectedThroughTor ? torSettings.SocksEndpoint : null, publisher.TorSocksEndPoint);
		Assert.Equal(Constants.DefaultNostrRelays.Select(relay => new Uri(relay)), publisher.Relays);
	}

	[Fact]
	public void RelaysOfTheStartAreTheOnesThatAreAsked()
	{
		var config = new Config(PersistentConfigManager.DefaultRegTestConfig, ["--nostrrelays=ws://127.0.0.1:7777/;wss://relay.example"]);
		var torSettings = new TorSettings(Common.GetWorkDir(), installationFolderPath: "", terminateOnExit: false, torMode: config.UseTor);

		var publisher = Global.CreateNostrPublisher(config, torSettings);

		Assert.Equal([new Uri("ws://127.0.0.1:7777/"), new Uri("wss://relay.example")], publisher.Relays);
	}

	[Fact]
	public void AddressThatIsNoRelayStopsTheStart()
	{
		var config = new Config(PersistentConfigManager.DefaultRegTestConfig, ["--nostrrelays=https://relay.example"]);
		var torSettings = new TorSettings(Common.GetWorkDir(), installationFolderPath: "", terminateOnExit: false, torMode: config.UseTor);

		var exception = Assert.Throws<FormatException>(() => Global.CreateNostrPublisher(config, torSettings));
		Assert.Contains("https://relay.example", exception.Message);
	}
}
