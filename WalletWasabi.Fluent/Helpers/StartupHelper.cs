using System.Threading.Tasks;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.Helpers;

public static class StartupHelper
{
	public const string SilentArgument = "startsilent";

	/// <param name="autostartDirectory">The folder of the desktop file; the one of the desktop environment when it is not given.</param>
	public static async Task ModifyStartupSettingAsync(bool runOnSystemStartup, string? autostartDirectory = null)
	{
		try
		{
			await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup, autostartDirectory).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// Suppress exception to avoid potential crashes.
			Logger.LogError($"{ex}");
		}
	}
}
