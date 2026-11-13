using System.IO;
using System.Runtime.InteropServices;

namespace WalletWasabi.BundledApps;

public static class BundledAppHelpers
{
	public static OSPlatform GetCurrentPlatform()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return OSPlatform.Linux;
		}
		else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return OSPlatform.OSX;
		}
		else
		{
			throw new NotSupportedException("Platform is not supported.");
		}
	}

	public static string GetBinaryFolder(BundledApp app, OSPlatform? platform = null)
	{
		platform ??= GetCurrentPlatform();

		string fullBaseDirectory = EnvironmentHelpers.GetFullBaseDirectory();
		string commonPartialPath = Path.Combine(fullBaseDirectory, "BundledApps", "Binaries");

		string path;
		if (platform == OSPlatform.Linux)
		{
			path = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
				? Path.Combine(commonPartialPath, "linux-arm64")
				: Path.Combine(commonPartialPath, "linux-x64");
		}
		else if (platform == OSPlatform.OSX)
		{
			// Tor is universal; bitcoind is x86_64-only (Rosetta).
			path = Path.Combine(commonPartialPath, "osx64");
		}
		else
		{
			throw new NotSupportedException("Operating system is not supported.");
		}

		return path;
	}

	public static string GetBinaryPath(BundledApp app)
	{
		var platform = GetCurrentPlatform();
		var binaryFolder = GetBinaryFolder(app, platform);

		var fileName = app switch
		{
			BundledApp.Tor => "tor",
			BundledApp.Bitcoind => "bitcoind",
			_ => throw new NotSupportedException($"Bundled app '{app}' is not supported.")
		};

		return Path.Combine(binaryFolder, fileName);
	}
}
