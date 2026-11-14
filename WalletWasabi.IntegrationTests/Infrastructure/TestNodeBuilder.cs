using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using WalletWasabi.Helpers;
using WalletWasabi.IntegrationTests.BitcoinCore;
using WalletWasabi.IntegrationTests.BitcoinCore.Endpointing;

namespace WalletWasabi.IntegrationTests.Infrastructure;

public static class TestNodeBuilder
{
	public static async Task<CoreNode> CreateAsync([CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "", string additionalFolder = "")
	{
		var dataDir = Path.Combine(GetWorkDir(callerFilePath, callerMemberName), additionalFolder);
		var nodeParameters = CreateDefaultCoreNodeParams(dataDir);

		return await CoreNode.CreateAsync(nodeParameters, CancellationToken.None).ConfigureAwait(false);
	}

	private static CoreNodeParams CreateDefaultCoreNodeParams(string dataDir)
	{
#pragma warning disable CA2000 // Dispose objects before losing scope - MemoryCache ownership transferred to CoreNodeParams
		var nodeParameters = new CoreNodeParams(
			Network.RegTest,
			dataDir,
			tryRestart: true,
			tryDeleteDataDir: true,
			EndPointStrategy.Random,
			EndPointStrategy.Random,
			txIndex: 1,
			prune: 0,
			disableWallet: 0,
			mempoolReplacement: "fee,optin",
			userAgent: $"/WasabiClient:{Constants.ClientVersion}/",
			fallbackFee: Money.Coins(0.0002m), // https://github.com/bitcoin/bitcoin/pull/16524
			new MemoryCache(new MemoryCacheOptions()));
#pragma warning restore CA2000
		nodeParameters.ListenOnion = 0;
		nodeParameters.Discover = 0;
		nodeParameters.DnsSeed = 0;
		nodeParameters.FixedSeeds = 0;
		nodeParameters.Upnp = 0;
		nodeParameters.NatPmp = 0;
		nodeParameters.PersistMempool = 0;
		nodeParameters.Port = PortFinder.GetRandomPorts(1)[0];
		return nodeParameters;
	}

	private static string GetWorkDir(string callerFilePath, string callerMemberName)
	{
		var dataDir = EnvironmentHelpers.GetDataDir(Path.Combine("WalletWasabi", "IntegrationTests"));
		return Path.Combine(dataDir, EnvironmentHelpers.ExtractFileName(callerFilePath), callerMemberName);
	}
}
