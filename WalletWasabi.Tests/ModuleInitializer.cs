using System.IO;
using System.Runtime.CompilerServices;
using WalletWasabi.Logging;
using WalletWasabi.Tests.Helpers;

namespace WalletWasabi.Tests;

public static class ModuleInitializer
{
	[ModuleInitializer]
	internal static void Initialize()
	{
		Logger.Configure(Path.Combine(Common.DataDir, "Logs.txt"), LogLevel.Info, [LogMode.Debug, LogMode.File]);
	}
}
