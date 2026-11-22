using NBitcoin;
using NBitcoin.RPC;
using System.IO;
using System.Linq;
using WalletWasabi.Client.Configuration;
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
