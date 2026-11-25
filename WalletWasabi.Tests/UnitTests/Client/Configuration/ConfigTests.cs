using NBitcoin;
using NBitcoin.RPC;
using System.IO;
using System.Linq;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client.Configuration;

public class ConfigTests
{
	[Fact]
	public void SwitchesOfTheNodeOptions()
	{
		var config = new Config(
			PersistentConfigManager.DefaultMainNetConfig,
			["--bitcoinrpcendpoint=http://node.example:8332/", "--blockonly=true", "--maxdaysinmempool=7"]);

		Assert.Equal("http://node.example:8332/", config.BitcoinRpcUri);
		Assert.True(config.BlockOnlyMode);
		Assert.Equal(7, config.DropUnconfirmedTransactionsAfterDays);
	}

	[Fact]
	public void HelpNamesTheSwitchesThatWork()
	{
		var names = Config.GetConfigOptionsMetadata().Select(x => x.ParameterName).ToArray();

		Assert.Contains("BitcoinRpcEndPoint", names);
		Assert.Contains("BlockOnly", names);
		Assert.Contains("MaxDaysInMempool", names);

		// These are names of properties that no switch has.
		Assert.DoesNotContain("BitcoinRpcUri", names);
		Assert.DoesNotContain("BlockOnlyMode", names);
		Assert.DoesNotContain("DropUnconfirmedTransactionsAfterDays", names);
	}

	[Fact]
	public void CommandLineWinsOverEnvironment()
	{
		// An option that nothing else reads, so other tests are not disturbed.
		const string Option = "OptionOfConfigTests";
		const string EnvironmentKey = "BLACKHOLE_OPTIONOFCONFIGTESTS";

		Assert.False(Config.TryGetOverrideValue(Option, [], out _));

		Config.EnvironmentVariables[EnvironmentKey] = "from the environment";
		try
		{
			Assert.True(Config.TryGetOverrideValue(Option, [], out var value));
			Assert.Equal("from the environment", value);

			Assert.True(Config.TryGetOverrideValue(Option, ["--optionofconfigtests=from the command line"], out value));
			Assert.Equal("from the command line", value);
		}
		finally
		{
			Config.EnvironmentVariables.Remove(EnvironmentKey);
		}

		// Variables of Wasabi Wallet mean nothing here.
		Config.EnvironmentVariables["WASABI_OPTIONOFCONFIGTESTS"] = "from Wasabi Wallet";
		try
		{
			Assert.False(Config.TryGetOverrideValue(Option, [], out _));
		}
		finally
		{
			Config.EnvironmentVariables.Remove("WASABI_OPTIONOFCONFIGTESTS");
		}
	}

	/// <summary>
	/// The offer of a market maker is posted to Nostr relays. Which ones is a setting: the ones of the config file,
	/// which start out as the ones that the application knows. Like every setting they can be given at the start
	/// instead.
	/// </summary>
	[Fact]
	public void OffersGoToTheRelaysOfTheSettings()
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.Equal(["wss://relay.primal.net", "wss://relay.damus.io", "wss://nos.lol"], config.NostrRelays);

		// The relays that the user set.
		var persistentConfig = PersistentConfigManager.DefaultMainNetConfig with { NostrRelays = new(["wss://mine.example"]) };
		config = new Config(persistentConfig, []);
		Assert.Equal(["wss://mine.example"], config.NostrRelays);
		Assert.False(config.IsOverridden);

		// None at all.
		config = new Config(PersistentConfigManager.DefaultMainNetConfig with { NostrRelays = [] }, []);
		Assert.Empty(config.NostrRelays);

		// Given at the start, they go before the ones of the file.
		config = new Config(persistentConfig, ["--nostrrelays=wss://one.example;ws://127.0.0.1:7777/"]);
		Assert.Equal(["wss://one.example", "ws://127.0.0.1:7777/"], config.NostrRelays);

		Assert.Contains("NostrRelays", Config.GetConfigOptionsMetadata().Select(x => x.ParameterName));
	}

	/// <summary>
	/// Whether the application runs a Nostr relay of its own is a setting: it runs none unless the config file says
	/// so. Like every setting it can be given at the start instead, and then it cannot be changed in the application.
	/// </summary>
	[Fact]
	public void RelayOfItsOwnIsRunIfTheSettingsSaySo()
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.False(config.RunNostrRelay);
		Assert.False(config.IsNostrRelayOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig with { RunNostrRelay = true }, []);
		Assert.True(config.RunNostrRelay);
		Assert.False(config.IsOverridden);
		Assert.False(config.IsNostrRelayOverridden);

		// Given at the start, it goes before what the file says.
		config = new Config(PersistentConfigManager.DefaultMainNetConfig with { RunNostrRelay = true }, ["--runnostrrelay=false"]);
		Assert.False(config.RunNostrRelay);
		Assert.True(config.IsOverridden);
		Assert.True(config.IsNostrRelayOverridden);
		Assert.False(config.IsNostrRelaysOverridden);

		Assert.Contains("RunNostrRelay", Config.GetConfigOptionsMetadata().Select(x => x.ParameterName));
	}

	/// <summary>
	/// Which events the relay takes is a setting too: the events of Black Hole unless the config file says otherwise,
	/// or the kinds of events that the user named.
	/// </summary>
	[Fact]
	public void RelayTakesTheEventsOfTheSettings()
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.Equal(WalletWasabi.NostrRelay.NostrRelaySettings.Default, config.NostrRelaySettings);

		var custom = PersistentConfigManager.DefaultMainNetConfig with { NostrRelayEvents = "Custom", NostrRelayKinds = new(["1", "30000-39999"]) };
		config = new Config(custom, []);
		Assert.Equal(WalletWasabi.NostrRelay.RelayEventChoice.Custom, config.NostrRelaySettings.Events);
		Assert.Equal("1, 30000-39999", config.NostrRelaySettings.Kinds.ToString());
		Assert.Null(config.NostrRelaySettings.Problem);
		Assert.False(config.IsNostrRelayOverridden);

		// Given at the start, they go before what the file says, and cannot be changed in the application.
		config = new Config(custom, ["--nostrrelaykinds=0;3"]);
		Assert.Equal("0, 3", config.NostrRelaySettings.Kinds.ToString());
		Assert.True(config.IsNostrRelayOverridden);

		config = new Config(custom, ["--nostrrelayevents=blackhole"]);
		Assert.Equal(WalletWasabi.NostrRelay.RelayEventChoice.BlackHole, config.NostrRelaySettings.Events);
		Assert.True(config.IsNostrRelayOverridden);

		// What cannot be read is the choice of a new installation, and a list without a kind is none to run a relay with.
		config = new Config(PersistentConfigManager.DefaultMainNetConfig with { NostrRelayEvents = "Everything", NostrRelayKinds = new(["note"]) }, []);
		Assert.Equal(WalletWasabi.NostrRelay.NostrRelaySettings.Default, config.NostrRelaySettings);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig with { NostrRelayEvents = "Custom" }, []);
		Assert.NotNull(config.NostrRelaySettings.Problem);

		Assert.Contains("NostrRelayEvents", Config.GetConfigOptionsMetadata().Select(x => x.ParameterName));
		Assert.Contains("NostrRelayKinds", Config.GetConfigOptionsMetadata().Select(x => x.ParameterName));
	}

	/// <summary>
	/// Whose events the relay takes is a setting as well: from anybody unless the config file says otherwise, or from
	/// keys with a fidelity bond of a size behind them and from the keys of a list.
	/// </summary>
	[Fact]
	public void RelayTakesEventsFromTheKeysOfTheSettings()
	{
		using var key = TestRelayEvent.NewKey();
		var hex = TestRelayEvent.GetPublicKey(key);
		var npub = WalletWasabi.NostrRelay.RelayKey.ToNpub(hex);

		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.Equal(WalletWasabi.NostrRelay.RelaySenderRules.Anyone, config.NostrRelaySettings.Senders);
		Assert.False(config.NostrRelayBondFilter);

		var filtered = PersistentConfigManager.DefaultMainNetConfig with { NostrRelayBondFilter = true, NostrRelayMinBond = "0.25", NostrRelayWhitelist = new([npub]) };
		config = new Config(filtered, []);
		Assert.Equal(new WalletWasabi.NostrRelay.RelaySenderRules(true, 25_000_000, [hex]), config.NostrRelaySettings.Senders);
		Assert.False(config.IsNostrRelayOverridden);

		// Given at the start, they go before what the file says, and cannot be changed in the application.
		config = new Config(filtered, ["--nostrrelayminbond=1.5"]);
		Assert.Equal(150_000_000, config.NostrRelaySettings.Senders.MinBond);
		Assert.True(config.IsNostrRelayOverridden);

		config = new Config(filtered, ["--nostrrelaybondfilter=false"]);
		Assert.False(config.NostrRelaySettings.Senders.IsBondRequired);
		Assert.Equal(0, config.NostrRelaySettings.Senders.RequiredShare);
		Assert.True(config.IsNostrRelayOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig, [$"--nostrrelaywhitelist={hex};{npub}"]);
		Assert.Equal([hex], config.NostrRelaySettings.Senders.Whitelist.ToArray());
		Assert.True(config.IsNostrRelayOverridden);

		// What is given at the start and cannot be read is refused: a minimum that is none would let everybody with any bond in.
		Assert.Throws<System.ArgumentException>(() => new Config(filtered, ["--nostrrelayminbond=0,5"]));
		Assert.Throws<System.ArgumentException>(() => new Config(filtered, ["--nostrrelayminbond=a quarter"]));
		Assert.Throws<System.ArgumentException>(() => new Config(filtered, [$"--nostrrelaywhitelist={npub};nobody"]));

		// In the file, an amount that cannot be read is no minimum, and a key that is none is left out.
		config = new Config(filtered with { NostrRelayMinBond = "a quarter", NostrRelayWhitelist = new([npub, "nobody"]) }, []);
		Assert.Equal(new WalletWasabi.NostrRelay.RelaySenderRules(true, 0, [hex]), config.NostrRelaySettings.Senders);

		foreach (var option in new[] { "NostrRelayBondFilter", "NostrRelayMinBond", "NostrRelayWhitelist" })
		{
			Assert.Contains(option, Config.GetConfigOptionsMetadata().Select(x => x.ParameterName));
		}
	}

	/// <summary>
	/// Settings can only be changed in the application while none of them was given on the command line or in the environment.
	/// Options that are not settings, like the log level, do not get in the way.
	/// </summary>
	[Theory]
	[InlineData("--loglevel=debug")]
	[InlineData("--logmodes=console,file")]
	[InlineData("--blockonly=true")]
	[InlineData("--rpconionenabled=true")]
	[InlineData("--torfolder=/opt/tor")]
	[InlineData("--torsocksport=9050")]
	[InlineData("--torcontrolport=9051")]
	[InlineData("--network=regtest")]
	public void OptionsThatAreNotSettingsLeaveTheSettingsChangeable(string option)
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, [option]);
		Assert.False(config.IsOverridden);

		// Together with a setting they do not help: the setting was given.
		config = new Config(PersistentConfigManager.DefaultMainNetConfig, [option, "--usetor=disabled"]);
		Assert.True(config.IsOverridden);
	}

	[Theory]
	[InlineData("--coordinatoruri=https://coordinator.example/")]
	[InlineData("--usetor=disabled")]
	[InlineData("--terminatetoronexit=true")]
	[InlineData("--torbridges=obfs4 192.0.2.1:443")]
	[InlineData("--bitcoinrpcendpoint=http://node.example:8332/")]
	[InlineData("--bitcoinrpccredentialstring=user:password")]
	[InlineData("--jsonrpcserverenabled=true")]
	[InlineData("--jsonrpcuser=user")]
	[InlineData("--jsonrpcpassword=password")]
	[InlineData("--jsonrpcserverprefixes=http://127.0.0.1:37128/")]
	[InlineData("--coordinatoridentifier=Identifier")]
	[InlineData("--maxcoinjoinminingfeerate=10")]
	[InlineData("--absolutemininputcount=30")]
	[InlineData("--maxdaysinmempool=7")]
	[InlineData("--experimentalfeatures=feature")]
	[InlineData("--nostrrelays=wss://relay.example")]
	public void SettingsThatAreGivenAtTheStartLockTheSettings(string option)
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, [option]);
		Assert.True(config.IsOverridden);
	}

	/// <summary>
	/// The node can be changed in the application before there is a wallet, unless the node itself was given at the start.
	/// </summary>
	[Fact]
	public void TellsWhetherTheNodeWasGivenAtTheStart()
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.False(config.IsOverridden);
		Assert.False(config.IsBitcoinRpcOverridden);

		// Another setting locks the settings, but it says nothing about the node.
		config = new Config(PersistentConfigManager.DefaultMainNetConfig, ["--usetor=disabled"]);
		Assert.True(config.IsOverridden);
		Assert.False(config.IsBitcoinRpcOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig, ["--bitcoinrpcendpoint=http://node.example:8332/"]);
		Assert.True(config.IsOverridden);
		Assert.True(config.IsBitcoinRpcOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig, ["--bitcoinrpccredentialstring=user:password"]);
		Assert.True(config.IsOverridden);
		Assert.True(config.IsBitcoinRpcOverridden);
	}

	/// <summary>
	/// The same goes for the relays and the coordinator, which the settings connect to like to the node: what was given
	/// at the start cannot be changed there, and what was not given can, whatever else was given.
	/// </summary>
	[Fact]
	public void TellsWhetherTheRelaysAndTheCoordinatorWereGivenAtTheStart()
	{
		var config = new Config(PersistentConfigManager.DefaultMainNetConfig, []);
		Assert.False(config.IsCoordinatorUriOverridden);
		Assert.False(config.IsNostrRelaysOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig, ["--coordinatoruri=http://coordinator.example/"]);
		Assert.True(config.IsOverridden);
		Assert.True(config.IsCoordinatorUriOverridden);
		Assert.False(config.IsNostrRelaysOverridden);
		Assert.False(config.IsBitcoinRpcOverridden);

		config = new Config(PersistentConfigManager.DefaultMainNetConfig, ["--nostrrelays=wss://relay.example"]);
		Assert.True(config.IsOverridden);
		Assert.False(config.IsCoordinatorUriOverridden);
		Assert.True(config.IsNostrRelaysOverridden);
		Assert.False(config.IsBitcoinRpcOverridden);
	}

	/// <summary>
	/// The default credentials are the cookie file that Bitcoin Core writes into its data folder. NBitcoin knows the place
	/// of the file for every network of the application.
	/// </summary>
	[Theory]
	[InlineData("main", ".cookie")]
	[InlineData("signet", "signet/.cookie")]
	[InlineData("regtest", "regtest/.cookie")]
	public void DefaultCredentialsAreTheCookieFileOfBitcoinCore(string networkName, string expectedPathEnd)
	{
		var network = Network.GetNetwork(networkName);
		Assert.NotNull(network);

		var cookieFilePath = RPCClient.TryGetDefaultCookieFilePath(network);
		Assert.NotNull(cookieFilePath);
		Assert.EndsWith(Path.Combine(".bitcoin", expectedPathEnd.Replace('/', Path.DirectorySeparatorChar)), cookieFilePath);

		// The client can be made without credentials.
		var rpcClient = new RPCClient(new RPCCredentialString(), new Uri("http://localhost:1/"), network);
		Assert.True(rpcClient.CredentialString.UseDefault);
	}
}
