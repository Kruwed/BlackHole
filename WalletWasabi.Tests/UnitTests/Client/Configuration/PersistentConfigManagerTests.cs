using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.Serialization;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client.Configuration;

/// <summary>
/// Tests for <see cref="PersistentConfigManager"/>
/// </summary>
public class PersistentConfigManagerTests
{
	[Fact]
	public async Task ToFileAndLoadFileTestAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, $"{nameof(ToFileAndLoadFileTestAsync)}.json");

		// Create config and store it.
		PersistentConfig actualConfig = PersistentConfigManager.DefaultMainNetConfig;

		string storedJson = PersistentConfigManager.ToFile(configPath, actualConfig);
		PersistentConfigManager.UpdateNetwork(configPath, actualConfig.Network);

		var readConfig = PersistentConfigManager.LoadFile(configPath) as PersistentConfig;
		Assert.NotNull(readConfig);

		// Objects are supposed to be equal by value-equality rules.
		Assert.Equal(actualConfig, readConfig);

		// Check that JSON strings are equal as well.
		{
			// JsonRpcUser and JsonRpcPassword are randomly generated, so we use the actual values
			string expected = GetConfigString(actualConfig.JsonRpcUser, actualConfig.JsonRpcPassword);
			string actual = JsonEncoder.ToReadableString(readConfig, PersistentConfigEncode.PersistentConfig);

			AssertJsonStringsEqual(expected, actual);
			AssertJsonStringsEqual(expected, storedJson);
		}

		static string GetConfigString(string jsonRpcUser, string jsonRpcPassword)
			=> $$"""
			{
			  "CoordinatorUri": "",
			  "UseTor": "Enabled",
			  "TerminateTorOnExit": false,
			  "TorBridges": [],
			  "BitcoinRpcCredentialString": "",
			  "BitcoinRpcEndPoint": "http://localhost:8332",
			  "JsonRpcServerEnabled": false,
			  "JsonRpcUser": "{{jsonRpcUser}}",
			  "JsonRpcPassword": "{{jsonRpcPassword}}",
			  "JsonRpcServerPrefixes": [
			    "http://127.0.0.1:37128/",
			    "http://localhost:37128/"
			  ],
			  "CoordinatorIdentifier": "CoinJoinCoordinatorIdentifier",
			  "MaxCoinJoinMiningFeeRate": 50.0,
			  "AbsoluteMinInputCount": 21,
			  "MaxDaysInMempool": 30,
			  "ExperimentalFeatures": [],
			  "NostrRelays": [
			    "wss://relay.primal.net",
			    "wss://relay.damus.io",
			    "wss://nos.lol"
			  ],
			  "ConfiguredConnections": [],
			  "RunNostrRelay": false,
			  "NostrRelayEvents": "BlackHole",
			  "NostrRelayKinds": [],
			  "ConfigVersion": 4
			}
			""";

		static void AssertJsonStringsEqual(string expected, string actual)
			=> Assert.Equal(expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
	}

	/// <summary>
	/// A file of an earlier version still says whether the GPU is to be used. It is read all the same, what it says
	/// about the other settings is kept, and the setting that is gone is left out when the file is saved again.
	/// </summary>
	[Fact]
	public async Task ReadsAFileThatStillHasTheGpuSettingAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, "Config.json");

		File.WriteAllText(
			configPath,
			"""
			{
			  "CoordinatorUri": "https://coordinator.example/",
			  "UseTor": "EnabledOnlyRunning",
			  "TerminateTorOnExit": true,
			  "TorBridges": [],
			  "BitcoinRpcCredentialString": "user:password",
			  "BitcoinRpcEndPoint": "http://node.example:8332/",
			  "JsonRpcServerEnabled": false,
			  "JsonRpcUser": "rpcuser",
			  "JsonRpcPassword": "rpcpassword",
			  "JsonRpcServerPrefixes": [
			    "http://127.0.0.1:37128/"
			  ],
			  "EnableGpu": false,
			  "CoordinatorIdentifier": "CoinJoinCoordinatorIdentifier",
			  "MaxCoinJoinMiningFeeRate": 12.5,
			  "AbsoluteMinInputCount": 33,
			  "MaxDaysInMempool": 45,
			  "ExperimentalFeatures": [],
			  "ConfigVersion": 4
			}
			""");

		var config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));

		Assert.Equal("https://coordinator.example/", config.CoordinatorUri);
		Assert.Equal("EnabledOnlyRunning", config.UseTor);
		Assert.True(config.TerminateTorOnExit);
		Assert.Equal("user:password", config.BitcoinRpcCredentialString);
		Assert.Equal("http://node.example:8332/", config.BitcoinRpcUri);
		Assert.Equal("rpcuser", config.JsonRpcUser);
		Assert.Equal(12.5m, config.MaxCoinJoinMiningFeeRate);
		Assert.Equal(33, config.AbsoluteMinInputCount);
		Assert.Equal(45, config.MaxDaysInMempool);

		string savedJson = PersistentConfigManager.ToFile(configPath, config);
		Assert.DoesNotContain("EnableGpu", savedJson);
		Assert.Contains("\"AbsoluteMinInputCount\": 33", savedJson);
	}

	/// <summary>
	/// The Nostr relays are a setting. A file from before they were one names none: it is read with the relays that the
	/// application knows, and they are written into it when it is saved. A file that names relays keeps them, also
	/// when it names none at all.
	/// </summary>
	[Fact]
	public async Task NostrRelaysAreKeptInTheConfigFileAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, "Config.json");

		// A file without the setting: the one that the application writes, less the relays.
		var json = System.Text.Json.Nodes.JsonNode.Parse(PersistentConfigManager.ToFile(configPath, PersistentConfigManager.DefaultMainNetConfig))!.AsObject();
		Assert.True(json.Remove("NostrRelays"));
		File.WriteAllText(configPath, json.ToJsonString());

		var config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Equal(Constants.DefaultNostrRelays, config.NostrRelays);
		Assert.Contains("wss://relay.damus.io", PersistentConfigManager.ToFile(configPath, config));

		// The relays of the user.
		PersistentConfigManager.ToFile(configPath, config with { NostrRelays = new ValueList<string>(["wss://one.example", "ws://127.0.0.1:7777/"]) });
		config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Equal(["wss://one.example", "ws://127.0.0.1:7777/"], config.NostrRelays);

		// No relay at all is a choice too, and not the same as a file that does not say.
		PersistentConfigManager.ToFile(configPath, config with { NostrRelays = [] });
		config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Empty(config.NostrRelays);
	}

	/// <summary>
	/// The config file keeps note of what the user has connected the application to: Tor, the node, Nostr and the
	/// coordinator. A new installation has connected to nothing, and so has a file from before the note was taken.
	/// </summary>
	[Fact]
	public async Task ConfiguredConnectionsAreKeptInTheConfigFileAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, "Config.json");

		Assert.Empty(PersistentConfigManager.DefaultMainNetConfig.ConfiguredConnections);
		Assert.Empty(PersistentConfigManager.DefaultSignetConfig.ConfiguredConnections);
		Assert.Empty(PersistentConfigManager.DefaultRegTestConfig.ConfiguredConnections);

		// A file without the note: the one that the application writes, less the note.
		var json = System.Text.Json.Nodes.JsonNode.Parse(PersistentConfigManager.ToFile(configPath, PersistentConfigManager.DefaultMainNetConfig))!.AsObject();
		Assert.True(json.Remove("ConfiguredConnections"));
		File.WriteAllText(configPath, json.ToJsonString());

		var config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Empty(config.ConfiguredConnections);

		// What the user has connected to, in the order in which they did.
		PersistentConfigManager.ToFile(configPath, config with { ConfiguredConnections = new ValueList<string>(["Tor", "Node"]) });
		config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Equal(["Tor", "Node"], config.ConfiguredConnections);

		// Two configurations that differ in it differ, and two that agree in it are the same.
		Assert.NotEqual(config, config with { ConfiguredConnections = new ValueList<string>(["Tor", "Node", "Nostr"]) });
		Assert.Equal(config, config with { ConfiguredConnections = new ValueList<string>(["Tor", "Node"]) });
	}

	/// <summary>
	/// The config file says whether the application runs a Nostr relay of its own. A new installation runs none, and
	/// neither does one whose file is from before the application could.
	/// </summary>
	[Fact]
	public async Task RunningARelayIsKeptInTheConfigFileAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, "Config.json");

		Assert.False(PersistentConfigManager.DefaultMainNetConfig.RunNostrRelay);
		Assert.False(PersistentConfigManager.DefaultSignetConfig.RunNostrRelay);
		Assert.False(PersistentConfigManager.DefaultRegTestConfig.RunNostrRelay);

		// A file that does not say: the one that the application writes, less the setting.
		var json = System.Text.Json.Nodes.JsonNode.Parse(PersistentConfigManager.ToFile(configPath, PersistentConfigManager.DefaultMainNetConfig with { RunNostrRelay = true }))!.AsObject();
		Assert.True(json.Remove("RunNostrRelay"));
		File.WriteAllText(configPath, json.ToJsonString());

		var config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.False(config.RunNostrRelay);

		PersistentConfigManager.ToFile(configPath, config with { RunNostrRelay = true });
		config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.True(config.RunNostrRelay);

		// A change of it is a change of the configuration.
		Assert.NotEqual(config, config with { RunNostrRelay = false });
	}

	/// <summary>
	/// The config file says which events the relay takes: the events of Black Hole, or the kinds that the user named,
	/// and it keeps those kinds also while the relay takes the others. A file that does not say stands for the events
	/// of Black Hole.
	/// </summary>
	[Fact]
	public async Task EventsOfTheRelayAreKeptInTheConfigFileAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string configPath = Path.Combine(workDirectory, "Config.json");

		Assert.Equal("BlackHole", PersistentConfigManager.DefaultMainNetConfig.NostrRelayEvents);
		Assert.Empty(PersistentConfigManager.DefaultMainNetConfig.NostrRelayKinds);

		var custom = PersistentConfigManager.DefaultMainNetConfig with { NostrRelayEvents = "Custom", NostrRelayKinds = new ValueList<string>(["1", "30000-39999"]) };
		PersistentConfigManager.ToFile(configPath, custom);

		var config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Equal("Custom", config.NostrRelayEvents);
		Assert.Equal(["1", "30000-39999"], config.NostrRelayKinds);
		Assert.Equal(custom, config);

		// A file that does not say: the one that the application writes, less the two settings.
		var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
		Assert.True(json.Remove("NostrRelayEvents"));
		Assert.True(json.Remove("NostrRelayKinds"));
		File.WriteAllText(configPath, json.ToJsonString());

		config = Assert.IsType<PersistentConfig>(PersistentConfigManager.LoadFile(configPath));
		Assert.Equal("BlackHole", config.NostrRelayEvents);
		Assert.Empty(config.NostrRelayKinds);
	}

	/// <summary>
	/// The application works on mainnet, signet and regtest. Each of them has a config file of its own, and the two
	/// that are for testing differ from mainnet in the same settings.
	/// </summary>
	[Fact]
	public void EveryNetworkHasItsConfigFile()
	{
		Assert.Equal([Network.Main, Bitcoin.Instance.Signet, Network.RegTest], PersistentConfigManager.Networks);

		PersistentConfig[] defaults =
		[
			PersistentConfigManager.DefaultMainNetConfig,
			PersistentConfigManager.DefaultSignetConfig,
			PersistentConfigManager.DefaultRegTestConfig
		];
		Assert.Equal(PersistentConfigManager.Networks, defaults.Select(config => config.Network));
		Assert.Equal(["Config.json", "Config.Signet.json", "Config.RegTest.json"], defaults.Select(config => config.GetConfigFileName()));

		foreach (var config in defaults)
		{
			var isForTesting = config.Network != Network.Main;

			Assert.Equal(RpcSettings.GetDefaultUri(config.Network), config.BitcoinRpcUri);
			Assert.Equal("", config.BitcoinRpcCredentialString);
			Assert.Equal(isForTesting, config.JsonRpcServerEnabled);
			Assert.Equal(isForTesting ? Constants.AbsoluteMinInputCount : Constants.DefaultAbsoluteMinInputCount, config.AbsoluteMinInputCount);
		}
	}

	/// <summary>
	/// A network that is given at the start counts for that start. Without one, the application starts on the network
	/// that the data folder was first used with.
	/// </summary>
	[Fact]
	public async Task StartsOnTheGivenNetworkOrOnTheOneOfTheDataFolderAsync()
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string networkFilePath = Path.Combine(workDirectory, "network");

		// The first start of a data folder writes the network, here the one that is given.
		Assert.Equal(Bitcoin.Instance.Signet, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, "signet"));
		Assert.Equal(Bitcoin.Instance.Signet, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, givenNetworkName: null));

		// Another network that is given later counts for that start only.
		Assert.Equal(Network.RegTest, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, "regtest"));
		Assert.Equal(Bitcoin.Instance.Signet, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, givenNetworkName: null));

		// A new data folder is one for mainnet when no network is given.
		File.Delete(networkFilePath);
		Assert.Equal(Network.Main, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, givenNetworkName: null));
		Assert.Equal(Network.Main, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, givenNetworkName: null));
	}

	/// <summary>
	/// The application does not work on testnet4 any more, which is what it called "testnet". A start on it is refused,
	/// whether the network is given at the start or the data folder was used with it by a version that still had it.
	/// </summary>
	[Theory]
	[InlineData("testnet")]
	[InlineData("testnet4")]
	[InlineData("TestNet4")]
	public async Task RefusesToStartOnTestnetAsync(string networkName)
	{
		string workDirectory = await Common.GetEmptyWorkDirAsync();
		string networkFilePath = Path.Combine(workDirectory, "network");

		// Given at the start. A network that is refused is not written into the data folder.
		var exception = Assert.Throws<NotSupportedException>(() => PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, networkName));
		Assert.Equal($"Network '{networkName}' is not supported. The supported networks are main, signet and regtest.", exception.Message);
		Assert.False(File.Exists(networkFilePath));

		// Left in the data folder by a version that had the network. The message tells where the network is set.
		File.WriteAllText(networkFilePath, networkName);
		exception = Assert.Throws<NotSupportedException>(() => PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, givenNetworkName: null));
		Assert.StartsWith($"Network '{networkName}' is not supported.", exception.Message);
		Assert.EndsWith($"The network is set in the file '{networkFilePath}'.", exception.Message);

		// Such a data folder can still be started on a network that is given, and that does not change the folder.
		Assert.Equal(Network.Main, PersistentConfigManager.LoadOrCreateNetwork(networkFilePath, "main"));
		Assert.Equal(networkName, File.ReadAllText(networkFilePath));
	}

	// Test for migration 2.6.0 -> 2.8.0
	[Fact]
	public void MigrateFromV2_6_0WithUseBitcoinRpcTrue()
	{
		// Test migration when UseBitcoinRpc=true with valid RPC URI
		// The RPC credentials and URI should be preserved
		var v2_6_0Config = new PersistentConfig_2_6_0(
			Network: Network.Main,
			IndexerUri: "https://api.wasabiwallet.io/",
			CoordinatorUri: "https://wasabiwallet.io/",
			UseTor: "Enabled",
			TerminateTorOnExit: false,
			TorBridges: [],
			DownloadNewVersion: true,
			UseBitcoinRpc: true,
			BitcoinRpcCredentialString: "user:password",
			BitcoinRpcUri: "http://localhost:8332",
			JsonRpcServerEnabled: false,
			JsonRpcUser: "wasabi",
			JsonRpcPassword: "secret",
			JsonRpcServerPrefixes: new ValueList<string>(["http://127.0.0.1:37128/"]),
			DustThreshold: Money.Coins(0.00001m),
			EnableGpu: true,
			CoordinatorIdentifier: "CoinJoinCoordinatorIdentifier",
			ExchangeRateProvider: "MempoolSpace",
			FeeRateEstimationProvider: "BlockstreamInfo",
			ExternalTransactionBroadcaster: "MempoolSpace",
			MaxCoinJoinMiningFeeRate: 50.0m,
			AbsoluteMinInputCount: 21,
			MaxDaysInMempool: 30,
			ExperimentalFeatures: new ValueList<string>(["taproot"]),
			ConfigVersion: 3
		);

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// Verify the migration
		Assert.Equal(4, migratedConfig.ConfigVersion); // Version should be updated to e

		// RPC credentials should be preserved when UseBitcoinRpc=true and URI is valid
		Assert.Equal("user:password", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal("http://localhost:8332", migratedConfig.BitcoinRpcUri);
	}

	private static readonly PersistentConfig_2_6_0 Template_2_6_0 = new(
		Network: Network.Main,
		IndexerUri: "https://api.wasabiwallet.io/",
		CoordinatorUri: "https://wasabiwallet.io/",
		UseTor: "Enabled",
		TerminateTorOnExit: false,
		TorBridges: [],
		DownloadNewVersion: true,
		UseBitcoinRpc: false,
		BitcoinRpcCredentialString: "",
		BitcoinRpcUri: "", // Empty/invalid URI
		JsonRpcServerEnabled: false,
		JsonRpcUser: "",
		JsonRpcPassword: "",
		JsonRpcServerPrefixes: new ValueList<string>(["http://127.0.0.1:37128/"]),
		DustThreshold: Money.Coins(0.00001m),
		EnableGpu: true,
		CoordinatorIdentifier: "CoinJoinCoordinatorIdentifier",
		ExchangeRateProvider: "MempoolSpace",
		FeeRateEstimationProvider: "BlockstreamInfo",
		ExternalTransactionBroadcaster: "MempoolSpace",
		MaxCoinJoinMiningFeeRate: 50.0m,
		AbsoluteMinInputCount: 21,
		MaxDaysInMempool: 30,
		ExperimentalFeatures: [],
		ConfigVersion: 3
	);

	[Fact]
	public void MigrateFromV2_6_0WithUseBitcoinRpcFalseAndInvalidUri()
	{
		// Test migration when UseBitcoinRpc=false AND BitcoinRpcUri is invalid/empty
		// This should trigger migration to Wasabi RPC service
		var v2_6_0Config = Template_2_6_0 with
		{
			IndexerUri = "https://api.wasabiwallet.io/",
			UseBitcoinRpc = false,
			BitcoinRpcCredentialString = "",
			BitcoinRpcUri = "", // Empty/invalid URI
		};

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// When UseBitcoinRpc=false and URI is invalid, should migrate to Wasabi RPC
		Assert.Equal("", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal(Constants.DefaultMainNetBitcoinRpcUri, migratedConfig.BitcoinRpcUri);
		Assert.Equal(4, migratedConfig.ConfigVersion);
	}

	[Fact]
	public void MigrateFromV2_6_0WithUseBitcoinRpcFalseButValidUri()
	{
		// Test migration when UseBitcoinRpc=false BUT BitcoinRpcUri is valid
		// The valid URI should be preserved
		var v2_6_0Config = Template_2_6_0 with
		{
			IndexerUri = "https://api.wasabiwallet.io/",
			UseBitcoinRpc = false,
			BitcoinRpcCredentialString = "myuser:mypass",
			BitcoinRpcUri = Constants.DefaultMainNetBitcoinRpcUri,
		};

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// When URI is valid, it should be preserved even if UseBitcoinRpc=false
		Assert.Equal("", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal(Constants.DefaultMainNetBitcoinRpcUri, migratedConfig.BitcoinRpcUri);
		Assert.Equal(4, migratedConfig.ConfigVersion);
	}

	[Fact]
	public void MigrateFromV2_6_0_IndexerUriIsDropped()
	{
		// Test that IndexerUri field is dropped during migration
		var v2_6_0Config = Template_2_6_0 with
		{
			IndexerUri = "https://api.wasabiwallet.io/",
			UseBitcoinRpc = true,
			BitcoinRpcCredentialString = "user:password",
			BitcoinRpcUri = "http://localhost:8332",
		};

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// When URI is valid, it should be preserved even if UseBitcoinRpc=false
		Assert.Equal("user:password", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal("http://localhost:8332", migratedConfig.BitcoinRpcUri);
		Assert.Equal(4, migratedConfig.ConfigVersion);
	}

	[Fact]
	public void MigrateFromV2_6_0_AllFieldsPreserved()
	{
		// Comprehensive test that all non-removed fields are preserved during migration.
		// What chose external services (new versions, fee rate estimations, broadcasting) is dropped.
		var v2_6_0Config = new PersistentConfig_2_6_0(
			Network: Network.RegTest,
			IndexerUri: "https://api.wasabiwallet.co/", // Should be dropped
			CoordinatorUri: "https://coordinator.test/",
			UseTor: "EnabledOnlyRunning",
			TerminateTorOnExit: true,
			TorBridges: new ValueList<string>(["bridge1", "bridge2"]),
			DownloadNewVersion: false, // Should be dropped
			UseBitcoinRpc: true, // Should be dropped
			BitcoinRpcCredentialString: "cookiefile=/path/to/cookie",
			BitcoinRpcUri: "http://10.0.0.5:18332",
			JsonRpcServerEnabled: true,
			JsonRpcUser: "rpcuser",
			JsonRpcPassword: "rpcpass",
			JsonRpcServerPrefixes: new ValueList<string>(["http://0.0.0.0:37128/", "https://localhost:37129/"]),
			DustThreshold: Money.Coins(0.0001m),
			EnableGpu: false,
			CoordinatorIdentifier: "CustomCoordinator",
			ExchangeRateProvider: "Binance",
			FeeRateEstimationProvider: "Custom", // Should be dropped
			ExternalTransactionBroadcaster: "Custom", // Should be dropped
			MaxCoinJoinMiningFeeRate: 100.5m,
			AbsoluteMinInputCount: 50,
			MaxDaysInMempool: 60,
			ExperimentalFeatures: new ValueList<string>(["feature1", "feature2"]),
			ConfigVersion: 3
		);

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// Verify all preserved fields
		Assert.Equal(Network.RegTest, migratedConfig.Network);
		Assert.Equal("https://coordinator.test/", migratedConfig.CoordinatorUri);
		Assert.Equal("EnabledOnlyRunning", migratedConfig.UseTor);
		Assert.True(migratedConfig.TerminateTorOnExit);
		Assert.Equal(2, migratedConfig.TorBridges.Count());
		Assert.Contains("bridge1", migratedConfig.TorBridges);
		Assert.Contains("bridge2", migratedConfig.TorBridges);
		Assert.Equal("cookiefile=/path/to/cookie", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal("http://10.0.0.5:18332", migratedConfig.BitcoinRpcUri);
		Assert.True(migratedConfig.JsonRpcServerEnabled);
		Assert.Equal("rpcuser", migratedConfig.JsonRpcUser);
		Assert.Equal("rpcpass", migratedConfig.JsonRpcPassword);
		Assert.Equal(2, migratedConfig.JsonRpcServerPrefixes.Count());
		Assert.Equal("CustomCoordinator", migratedConfig.CoordinatorIdentifier);
		Assert.Equal(100.5m, migratedConfig.MaxCoinJoinMiningFeeRate);
		Assert.Equal(50, migratedConfig.AbsoluteMinInputCount);
		Assert.Equal(60, migratedConfig.MaxDaysInMempool);

		// ExperimentalFeatures should be reset to empty (as per migration logic)
		Assert.Empty(migratedConfig.ExperimentalFeatures);

		// ConfigVersion should be updated
		Assert.Equal(4, migratedConfig.ConfigVersion);
	}

	[Fact]
	public void MigrateFromV2_6_0_WithMalformedUri()
	{
		// Test migration when BitcoinRpcUri contains a malformed URI (not a valid absolute URI)
		var v2_6_0Config = Template_2_6_0 with
		{
			UseBitcoinRpc = false,
			BitcoinRpcCredentialString = "",
			BitcoinRpcUri = "not-a-valid-uri", // Malformed - not an absolute URI
		};

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		// Should migrate to Wasabi RPC service because UseBitcoinRpc=false and URI is not well-formed
		Assert.Equal("", migratedConfig.BitcoinRpcCredentialString);
		Assert.Equal(Constants.DefaultMainNetBitcoinRpcUri, migratedConfig.BitcoinRpcUri);
	}

	[Fact]
	public void MigrateFromV2_6_0_ConfigVersionUpdated()
	{
		// Test that ConfigVersion is correctly updated from 2 to 4
		var v2_6_0Config = Template_2_6_0 with {
			ConfigVersion = 2 // v2.6.0 uses ConfigVersion 3
		};

		var migratedConfig = WasabiApplication.UpdateFrom260To280(v2_6_0Config);

		Assert.Equal(2, v2_6_0Config.ConfigVersion);
		Assert.Equal(4, migratedConfig.ConfigVersion); // Should be 4 in v2.8.0
	}
}
