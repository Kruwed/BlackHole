using System.IO;
using System.Runtime.InteropServices;

namespace WalletWasabi.BundledApps;

public static class BundledAppHelpers
{
	public static string GetBinaryFolder()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			throw new NotSupportedException("Operating system is not supported.");
		}

		string fullBaseDirectory = EnvironmentHelpers.GetFullBaseDirectory();
		string commonPartialPath = Path.Combine(fullBaseDirectory, "BundledApps", "Binaries");

		return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
			? Path.Combine(commonPartialPath, "linux-arm64")
			: Path.Combine(commonPartialPath, "linux-x64");
	}

	public static string GetBinaryPath(BundledApp app)
	{
		var fileName = app switch
		{
			BundledApp.Tor => "tor",
			BundledApp.Bitcoind => "bitcoind",
			_ => throw new NotSupportedException($"Bundled app '{app}' is not supported.")
		};

		return Path.Combine(GetBinaryFolder(), fileName);
	}
}
