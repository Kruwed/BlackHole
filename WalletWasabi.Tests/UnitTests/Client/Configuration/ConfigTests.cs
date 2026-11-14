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
	/// The default credentials are the cookie file that Bitcoin Core writes into its data folder. NBitcoin knows the place
	/// of the file for every network but testnet4, which is what "testnet" means here.
	/// </summary>
	[Theory]
	[InlineData("main", ".cookie")]
	[InlineData("testnet", "testnet4/.cookie")]
	[InlineData("signet", "signet/.cookie")]
	[InlineData("regtest", "regtest/.cookie")]
	public void DefaultCredentialsAreTheCookieFileOfBitcoinCore(string networkName, string expectedPathEnd)
	{
		var network = Network.GetNetwork(networkName);
		Assert.NotNull(network);

		var cookieFilePath = RPCClient.TryGetDefaultCookieFilePath(network);
		Assert.NotNull(cookieFilePath);
		Assert.EndsWith(Path.Combine(".bitcoin", expectedPathEnd.Replace('/', Path.DirectorySeparatorChar)), cookieFilePath);

		// The client can be made without credentials. It threw on testnet4.
		var rpcClient = new RPCClient(new RPCCredentialString(), new Uri("http://localhost:1/"), network);
		Assert.True(rpcClient.CredentialString.UseDefault);
	}
}
