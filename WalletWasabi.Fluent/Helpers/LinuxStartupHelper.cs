using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.Helpers;

public static class LinuxStartupHelper
{
	public const string DesktopFileName = "BlackHole.desktop";

	/// <summary>The folder of the desktop files that the desktop environment starts when the user logs in.</summary>
	public static string DefaultAutostartDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");

	public static async Task AddOrRemoveDesktopFileAsync(bool runOnSystemStartup, string? autostartDirectory = null)
	{
		string pathToDesktopFile = Path.Combine(autostartDirectory ?? DefaultAutostartDirectory, DesktopFileName);

		if (runOnSystemStartup)
		{
			IoHelpers.EnsureContainingDirectoryExists(pathToDesktopFile);

			string pathToExec = EnvironmentHelpers.GetExecutablePath();

			string pathToExecWithArgs = $"{pathToExec} {StartupHelper.SilentArgument}";

			IoHelpers.EnsureFileExists(pathToExec);

			string fileContents = string.Join(
				"\n",
				"[Desktop Entry]",
				$"Name={Constants.AppName}",
				"Type=Application",
				$"Exec={pathToExecWithArgs}",
				"Hidden=false",
				"Terminal=false",
				"X-GNOME-Autostart-enabled=true");

			await File.WriteAllTextAsync(pathToDesktopFile, fileContents).ConfigureAwait(false);
		}
		else if (File.Exists(pathToDesktopFile))
		{
			File.Delete(pathToDesktopFile);
		}
	}
}
