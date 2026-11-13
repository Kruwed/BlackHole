using System.Threading.Tasks;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.Helpers;

public static class StartupHelper
{
	public const string SilentArgument = "startsilent";

	public static async Task ModifyStartupSettingAsync(bool runOnSystemStartup)
	{
		try
		{
			await LinuxStartupHelper.AddOrRemoveDesktopFileAsync(runOnSystemStartup).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// Suppress exception to avoid potential crashes.
			Logger.LogError($"{ex}");
		}
	}
}
