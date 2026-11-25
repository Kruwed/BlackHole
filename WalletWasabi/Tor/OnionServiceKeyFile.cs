using System.IO;
using WalletWasabi.Tor.Control.Messages;

namespace WalletWasabi.Tor;

/// <summary>
/// The file that keeps the key of an onion service, so that the service has the same address every time it is made.
/// Whoever has the file can run a service at that address, and deleting it gives the service a new address.
/// </summary>
public sealed class OnionServiceKeyFile
{
	public OnionServiceKeyFile(string path)
	{
		Path = path;
	}

	public string Path { get; }

	/// <summary>Reads the key.</summary>
	/// <returns>The key, or null if there is no file yet.</returns>
	/// <exception cref="InvalidDataException">There is a file, and what is in it is not a key that Tor made.</exception>
	/// <exception cref="IOException">The file cannot be read.</exception>
	/// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
	public string? Read()
	{
		if (!File.Exists(Path))
		{
			return null;
		}

		var key = File.ReadAllText(Path).Trim();

		// A new key would be a new address, and whoever knows the old address would look for the service in vain. So
		// a file that cannot be used is a problem to be told, and not a reason to start over.
		return OnionServiceOptions.IsPrivateKey(key)
			? key
			: throw new InvalidDataException("What the file holds is not the key of an onion service.");
	}

	/// <summary>Writes the key. Only the user of this computer may read the file, where the system knows of such a rule.</summary>
	/// <exception cref="ArgumentException">It is not a key that Tor made.</exception>
	public void Write(string key)
	{
		if (!OnionServiceOptions.IsPrivateKey(key))
		{
			throw new ArgumentException("It is not the key of an onion service.", nameof(key));
		}

		if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder && !Directory.Exists(folder))
		{
			if (OperatingSystem.IsWindows())
			{
				Directory.CreateDirectory(folder);
			}
			else
			{
				Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			}
		}

		// The key is written whole, or the file stays what it was.
		var temporaryPath = Path + ".new";
		var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };

		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		using (var stream = new FileStream(temporaryPath, options))
		using (var writer = new StreamWriter(stream))
		{
			writer.Write(key);
		}

		File.Move(temporaryPath, Path, overwrite: true);
	}
}
