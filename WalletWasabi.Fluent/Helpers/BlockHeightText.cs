using System.Globalization;

namespace WalletWasabi.Fluent.Helpers;

/// <summary>
/// Writes block heights.
/// </summary>
public static class BlockHeightText
{
	public static string Format(uint height) => height.ToString("N0", CultureInfo.InvariantCulture);
}
