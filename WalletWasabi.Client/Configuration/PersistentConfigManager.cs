using NBitcoin;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Serialization;

namespace WalletWasabi.Client.Configuration;

public static class PersistentConfigManager
{
	private static readonly RandomStringGenerator GenerateRandomString = RandomnessProviders.Secure.CreateRandomStringGenerator();

	public static readonly PersistentConfig DefaultMainNetConfig = new (
		Network : Network.Main,
		CoordinatorUri : Constants.CoordinatorUri,
		UseTor : GetDefaultTorMode(),
		TerminateTorOnExit : false,
		TorBridges : [],
		BitcoinRpcCredentialString : string.Empty,
		BitcoinRpcUri : Constants.DefaultMainNetBitcoinRpcUri,
		JsonRpcServerEnabled : false,
		JsonRpcUser : GenerateRandomString(12),
		JsonRpcPassword : GenerateRandomString(12),
		JsonRpcServerPrefixes : new (["http://127.0.0.1:37128/", "http://localhost:37128/"]),
		CoordinatorIdentifier : "CoinJoinCoordinatorIdentifier",
		MaxCoinJoinMiningFeeRate : Constants.DefaultMaxCoinJoinMiningFeeRate,
		AbsoluteMinInputCount : Constants.DefaultAbsoluteMinInputCount,
		MaxDaysInMempool : Constants.DefaultMaxDaysInMempool,
		ExperimentalFeatures: [],
		ConfigVersion : 4);

	public static readonly PersistentConfig DefaultRegTestConfig = DefaultMainNetConfig with
	{
		Network = Network.RegTest,
		CoordinatorUri = Constants.RegTestCoordinatorUri,
		BitcoinRpcUri = Constants.DefaultRegTestBitcoinRpcUri,
		JsonRpcServerEnabled = true,
		AbsoluteMinInputCount = Constants.AbsoluteMinInputCount,
	};

	public static readonly PersistentConfig DefaultSignetConfig = DefaultRegTestConfig with
	{
		Network = Bitcoin.Instance.Signet,
		CoordinatorUri = Constants.SignetCoordinatorUri,
		BitcoinRpcUri = Constants.DefaultSignetBitcoinRpcUri,
	};

	/// <summary>The networks that the application works on.</summary>
	public static readonly ImmutableArray<Network> Networks = [Network.Main, Bitcoin.Instance.Signet, Network.RegTest];

	/// <summary>
	/// Tells the network that the application starts on: the one that was given at the start or, failing that, the one in the
	/// network file of the data folder. A data folder without that file is one for mainnet. The first start writes the network into the file.
	/// </summary>
	/// <exception cref="NotSupportedException">The application does not work on the network. Such a network is not written into the file.</exception>
	public static Network LoadOrCreateNetwork(string networkFilePath, string? givenNetworkName)
	{
		var networkFileExists = File.Exists(networkFilePath);
		var networkName = givenNetworkName ?? (networkFileExists ? File.ReadAllText(networkFilePath).Trim() : null);
		var network = networkName is null ? Network.Main : Network.GetNetwork(networkName) ?? Network.Main;

		if (!Networks.Contains(network))
		{
			var whereItIsSet = givenNetworkName is null ? $" The network is set in the file '{networkFilePath}'." : "";
			throw new NotSupportedException($"Network '{networkName}' is not supported. The supported networks are main, signet and regtest.{whereItIsSet}");
		}

		if (!networkFileExists)
		{
			UpdateNetwork(networkFilePath, network);
		}

		return network;
	}

	public static string ToFile(string filePath, PersistentConfig obj)
	{
		string jsonString = JsonEncoder.ToReadableString(obj, PersistentConfigEncode.PersistentConfig);
		File.WriteAllText(filePath, jsonString, Encoding.UTF8);

		return jsonString;
	}

	public static void UpdateNetwork(string filePath, Network network)
	{
		var networkFilePath = Path.Combine(Path.GetDirectoryName(filePath) ?? string.Empty, "network");
		File.WriteAllText(networkFilePath, network.ToString());
	}

	public static IPersistentConfig LoadFile(string filePath)
	{
		try
		{
			using var cfgFile = File.Open(filePath, FileMode.Open, FileAccess.Read);
			var decoder = JsonDecoder.FromStream(PersistentConfigDecode.PersistentConfig);
			var decodingResult = decoder(cfgFile);
			return decodingResult.Match(cfg => cfg, error => throw new InvalidOperationException(error));
		}
		catch (FileNotFoundException)
		{
			var defaultConfig = GetDefaultPersistentConfigByFileName(filePath);

			ToFile(filePath, defaultConfig);
			UpdateNetwork(filePath, defaultConfig.Network);

			Logger.LogInfo($"File did not exist. Created at path: '{filePath}'.");
			return defaultConfig;
		}
		catch (Exception ex)
		{
			Logger.LogInfo($"Failed to load '{filePath}'. The config file is corrupted.");
			Logger.LogWarning(ex);

			var defaultFilePath = Path.ChangeExtension(filePath, ".Default.json");
			var defaultConfig = GetDefaultPersistentConfigByFileName(filePath);
			ToFile(defaultFilePath, defaultConfig);
			Logger.LogInfo($"Default config was stored to '{defaultFilePath}'. Use the config to start fresh.");

			string msg = $"Failed to load '{filePath}' config.\n\nDefault config was stored to '{defaultFilePath}'. Use the config to start fresh.";
			throw new InvalidDataException(msg, ex);
		}

		static PersistentConfig GetDefaultPersistentConfigByFileName(string configFilePath) =>
			Path.GetFileName(configFilePath) switch
			{
				"Config.json" => DefaultMainNetConfig,
				"Config.RegTest.json" => DefaultRegTestConfig,
				"Config.Signet.json" => DefaultSignetConfig,
				_ => throw new ArgumentException($"The file '{configFilePath}' is not a valid config file name.")
			};
	}

	private static string GetDefaultTorMode()
	{
		// On Tails and Whonix, Tor is already running system-wide
		// We should only connect to it, not start our own instance
		if (PlatformInformation.IsTailsOS())
		{
			Logger.LogInfo("Detected Tails operating system. Setting Tor mode to 'Connect Only' by default.");
			return "EnabledOnlyRunning";
		}
		else if (PlatformInformation.IsWhonix())
		{
			Logger.LogInfo("Detected Whonix operating system. Setting Tor mode to 'Connect Only' by default.");
			return "EnabledOnlyRunning";
		}

		return "Enabled";
	}
}
