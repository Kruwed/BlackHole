using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using Xunit;
using WalletWasabi.Fluent.Configuration;

namespace WalletWasabi.Tests.UnitTests;

public class StartWasabiOnSystemStartupTests
{
	[Fact]
	public async Task ModifyStartupOnDifferentSystemsTestAsync()
	{
		UiConfig originalConfig = GetUiConfig();
		try
		{
			await StartupHelper.ModifyStartupSettingAsync(true);

			Assert.True(File.Exists(LinuxStartupTestHelper.FilePath));
			Assert.Equal(LinuxStartupTestHelper.ExpectedDesktopFileContent, LinuxStartupTestHelper.GetFileContent());

			await StartupHelper.ModifyStartupSettingAsync(false);

			Assert.False(File.Exists(LinuxStartupTestHelper.FilePath));
		}
		finally
		{
			// Restore original setting for developers.
			await StartupHelper.ModifyStartupSettingAsync(originalConfig.RunOnSystemStartup);
		}
	}

	[Fact]
	public async Task RunOnSystemStartupGetsSetCorrectlyAsync()
	{
		// Imitate fresh UiConfig file.
		string workDir = await Common.GetEmptyWorkDirAsync();

		UiConfig config = UiConfig.LoadFile(Path.Combine(workDir, "UiConfig.json"));
		Assert.True(config.Oobe);
		Assert.False(config.RunOnSystemStartup);
	}

	private UiConfig GetUiConfig()
	{
		string dataDir = EnvironmentHelpers.GetDataDir(Path.Combine("WalletWasabi", "Client"));
		return UiConfig.LoadFile(Path.Combine(dataDir, "UiConfig.json"));
	}
}
