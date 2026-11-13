using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WalletWasabi.Fluent.Helpers;

public static class FileHelpers
{
	private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".txt",
		".json",
		".log"
	};

	public static void OpenFileInTextEditor(string filePath)
	{
		string fullPath = Path.GetFullPath(filePath);

		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException($"The {Path.GetFileName(fullPath)} file is not found.", fullPath);
		}

		if (!AllowedExtensions.Contains(Path.GetExtension(fullPath)))
		{
			throw new NotSupportedException($"Opening '{Path.GetExtension(fullPath)}' files in a text editor is not supported.");
		}

		// Opens the default handler for the file type, which is not guaranteed to be a text editor.
		var startInfo = new ProcessStartInfo("xdg-open") { ArgumentList = { fullPath } };

		try
		{
			using var process = Process.Start(startInfo);
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException($"Failed to launch '{startInfo.FileName}'.", ex);
		}
	}
}
