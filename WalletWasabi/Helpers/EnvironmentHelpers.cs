using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WalletWasabi.Helpers;

public static class EnvironmentHelpers
{
	// appName, dataDir
	private static ConcurrentDictionary<string, string> DataDirDict { get; } = new();

	// Do not change the output of this function. Backwards compatibility depends on it.
	public static string GetDataDir(string appName)
	{
		if (DataDirDict.TryGetValue(appName, out string? dataDir))
		{
			return dataDir;
		}

		var home = Environment.GetEnvironmentVariable("HOME");
		if (string.IsNullOrEmpty(home))
		{
			throw new DirectoryNotFoundException("Could not find suitable datadir.");
		}

		string directory = Path.Combine(home, "." + appName.ToLowerInvariant());

		if (Directory.Exists(directory))
		{
			DataDirDict.TryAdd(appName, directory);
			return directory;
		}

		Directory.CreateDirectory(directory);

		DataDirDict.TryAdd(appName, directory);
		return directory;
	}


	// This method removes the path and file extension.
	//
	// The compiler embeds the source path as it was on the build machine, so both
	// separator styles are handled regardless of the OS the application runs on.
	public static string ExtractFileName(string callerFilePath)
	{
		var lastSeparatorIndex = callerFilePath.LastIndexOf('\\');
		if (lastSeparatorIndex == -1)
		{
			lastSeparatorIndex = callerFilePath.LastIndexOf('/');
		}

		var fileName = callerFilePath;

		if (lastSeparatorIndex != -1)
		{
			lastSeparatorIndex++;
			fileName = callerFilePath[lastSeparatorIndex..]; // From lastSeparatorIndex until the end of the string.
		}

		var fileNameWithoutExtension = fileName.TrimEnd(".cs", StringComparison.InvariantCultureIgnoreCase);
		return fileNameWithoutExtension;
	}

	public static string GetFullBaseDirectory()
	{
		var fullBaseDirectory = Path.GetFullPath(AppContext.BaseDirectory);

		if (!fullBaseDirectory.StartsWith('/'))
		{
			fullBaseDirectory = fullBaseDirectory.Insert(0, "/");
		}

		return fullBaseDirectory;
	}

	public static string GetExecutablePath()
	{
		var fullBaseDir = GetFullBaseDirectory();
		var executableFileName = Path.Combine(fullBaseDir, Constants.ExecutableName);
		if (File.Exists(executableFileName))
		{
			return executableFileName;
		}
		var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? throw new NullReferenceException("Assembly or Assembly's Name was null.");
		return Path.Combine(fullBaseDir, assemblyName);
	}

	public static string GetDefaultBitcoinDataDir()
	{
		string directory = "";

		var home = Environment.GetEnvironmentVariable("HOME");
		if (!string.IsNullOrEmpty(home))
		{
			directory = Path.Combine(home, ".bitcoin");
		}

		return directory;
	}
}

public enum OS
{
	Linux,
	FreeBSD
}

public static class PlatformInformation
{
	public static OS GetOsPlatform()
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			return OS.Linux;
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
		{
			return OS.FreeBSD;
		}

		throw new NotSupportedException("Unknown OS platform.");
	}

	private static bool TryGetLinuxOSRelease([NotNullWhen(true)] out string? content)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/etc/os-release"))
		{
			try
			{
				content = File.ReadAllText("/etc/os-release");
				return true;
			}
			catch (Exception ex)
			{
				Logger.LogDebug($"Failed to read /etc/os-release for operating system detection: {ex.Message}");
			}
		}

		content = null;
		return false;
	}

	public static bool IsTailsOS() =>
		TryGetLinuxOSRelease(out var osReleaseContent) && (
			osReleaseContent.Contains("ID=tails", StringComparison.OrdinalIgnoreCase) ||
			osReleaseContent.Contains("NAME=\"Tails\"", StringComparison.OrdinalIgnoreCase));

	public static bool IsWhonix() =>
		TryGetLinuxOSRelease(out var osReleaseContent) && (
			osReleaseContent.Contains("ID=whonix", StringComparison.OrdinalIgnoreCase) ||
			osReleaseContent.Contains("NAME=\"Whonix\"", StringComparison.OrdinalIgnoreCase) ||
			osReleaseContent.Contains("ID_LIKE=debian whonix", StringComparison.OrdinalIgnoreCase));
}
