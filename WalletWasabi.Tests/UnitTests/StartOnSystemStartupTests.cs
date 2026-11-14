using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

public class StartOnSystemStartupTests
{
	/// <summary>
	/// The test works in its own folder. The autostart folder of the desktop environment and the data folder of the
	/// application belong to the user of the computer and are left alone.
	/// </summary>
	[Fact]
	public async Task DesktopFileIsAddedAndRemovedAsync()
	{
		string autostartDirectory = await Common.GetEmptyWorkDirAsync();
		string desktopFilePath = Path.Combine(autostartDirectory, LinuxStartupHelper.DesktopFileName);

		string expectedContent = string.Join(
			"\n",
			"[Desktop Entry]",
			$"Name={Constants.AppName}",
			"Type=Application",
			$"Exec={EnvironmentHelpers.GetExecutablePath()} {StartupHelper.SilentArgument}",
			"Hidden=false",
			"Terminal=false",
			"X-GNOME-Autostart-enabled=true");

		await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup: true, autostartDirectory);

		Assert.True(File.Exists(desktopFilePath));
		Assert.Equal(expectedContent, string.Join("\n", await File.ReadAllLinesAsync(desktopFilePath)));

		await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup: false, autostartDirectory);

		Assert.False(File.Exists(desktopFilePath));

		// Removing what is not there is fine.
		await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup: false, autostartDirectory);
		Assert.False(File.Exists(desktopFilePath));
	}

	[Fact]
	public async Task AutostartFolderIsCreatedOnlyWhenNeededAsync()
	{
		string workDir = await Common.GetEmptyWorkDirAsync();
		string autostartDirectory = Path.Combine(workDir, "autostart");

		// Nothing to remove: the folder that does not exist is not created.
		await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup: false, autostartDirectory);
		Assert.False(Directory.Exists(autostartDirectory));

		await StartupHelper.ModifyStartupSettingAsync(true, autostartDirectory);
		Assert.True(File.Exists(Path.Combine(autostartDirectory, LinuxStartupHelper.DesktopFileName)));
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
}
