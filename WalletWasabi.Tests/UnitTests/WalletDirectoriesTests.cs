using NBitcoin;
using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

public class WalletDirectoriesTests
{
	private async Task<string> CleanupWalletDirectoriesAsync(string baseDir)
	{
		var walletsPath = Path.Combine(baseDir, WalletDirectories.WalletsDirName);
		await IoHelpers.TryDeleteDirectoryAsync(walletsPath);

		return walletsPath;
	}

	[Fact]
	public async Task CreatesWalletDirectoriesAsync()
	{
		var baseDir = Common.GetWorkDir();
		string walletsPath = await CleanupWalletDirectoriesAsync(baseDir);

		_ = new WalletDirectories(Network.Main, baseDir);
		Assert.True(Directory.Exists(walletsPath));

		// Testing what happens if the directories are already exist.
		_ = new WalletDirectories(Network.Main, baseDir);
		Assert.True(Directory.Exists(walletsPath));
	}

	[Fact]
	public async Task TestPathsAsync()
	{
		var baseDir = Common.GetWorkDir();
		await CleanupWalletDirectoriesAsync(baseDir);

		var mainWd = new WalletDirectories(Network.Main, baseDir);
		Assert.Equal(Network.Main, mainWd.Network);
		Assert.Equal(Path.Combine(baseDir, "Wallets"), mainWd.WalletsDir);

		var testWd = new WalletDirectories(Network.TestNet, baseDir);
		Assert.Equal(Network.TestNet, testWd.Network);
		Assert.Equal(Path.Combine(baseDir, "Wallets", "TestNet4"), testWd.WalletsDir);

		var regWd = new WalletDirectories(Network.RegTest, baseDir);
		Assert.Equal(Network.RegTest, regWd.Network);
		Assert.Equal(Path.Combine(baseDir, "Wallets", "RegTest"), regWd.WalletsDir);
	}

	[Fact]
	public async Task ServesTheWalletFileAsync()
	{
		var baseDir = Common.GetWorkDir();
		await CleanupWalletDirectoriesAsync(baseDir);

		// There is a single wallet per network, so its file has a fixed name.
		var mainWd = new WalletDirectories(Network.Main, baseDir);
		Assert.Equal(Path.Combine(baseDir, "Wallets", "Wallet.json"), mainWd.WalletFilePath);

		var regWd = new WalletDirectories(Network.RegTest, baseDir);
		Assert.Equal(Path.Combine(baseDir, "Wallets", "RegTest", "Wallet.json"), regWd.WalletFilePath);

		// Asking for the path does not create the file.
		Assert.False(File.Exists(mainWd.WalletFilePath));
		Assert.False(File.Exists(regWd.WalletFilePath));
	}
}
