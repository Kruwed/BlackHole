using System.Diagnostics;

namespace WalletWasabi.BundledApps;

/// <summary>
/// Factory for <see cref="ProcessStartInfo"/> with pre-defined properties as needed in Wasabi Wallet.
/// </summary>
public class ProcessStartInfoFactory
{
	/// <summary>
	/// Creates new <see cref="ProcessStartInfo"/> instance using ArgumentList for proper escaping.
	/// </summary>
	/// <param name="processPath">Path to process.</param>
	/// <param name="arguments">Process arguments as separate elements (each will be properly escaped).</param>
	/// <returns><see cref="ProcessStartInfo"/> instance.</returns>
	public static ProcessStartInfo Make(string processPath, IReadOnlyList<string> arguments)
	{
		var p = new ProcessStartInfo
		{
			FileName = processPath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			WindowStyle = ProcessWindowStyle.Normal
		};

		foreach (var arg in arguments)
		{
			p.ArgumentList.Add(arg);
		}

		return p;
	}
}
