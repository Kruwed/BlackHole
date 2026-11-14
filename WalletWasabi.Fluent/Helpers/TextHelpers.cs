using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;

namespace WalletWasabi.Fluent.Helpers;

public static partial class TextHelpers
{
	public static string AddSIfPlural(int n) => n > 1 ? "s" : "";

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRegex();

	public static string ToBtcWithUnit(this Money money)
	{
		return money.ToFormattedString();
	}

	public static string ToFormattedString(this Money money)
	{
		const int WholeGroupSize = 3;

		var moneyString = money.ToString(fplus: false, false);

		moneyString = moneyString.Insert(moneyString.Length - 3, " ");
		moneyString = moneyString.Insert(moneyString.Length - 7, " ");

		var startIndex = moneyString.IndexOf('.') - WholeGroupSize;

		if (startIndex > 0)
		{
			for (var i = startIndex; i > 0; i -= WholeGroupSize)
			{
				moneyString = moneyString.Insert(i, " ");
			}
		}

		return moneyString;
	}

	public static string CollapseWhitespace(this string text) => WhitespaceRegex().Replace(text, " ").Trim();

	public static string TotalTrim(this string text)
	{
		return text
			.Replace("\r", "")
			.Replace("\n", "")
			.Replace("\t", "")
			.Replace(" ", "");
	}

	public static string GetConfirmationText(uint confirmations)
	{
		return $"Confirmed ({confirmations} confirmation{AddSIfPlural((int)confirmations)})";
	}

	public static string FormatPercentageDiff(double n)
	{
		var precision = 0.01m;
		var withFriendlyDecimals = (n * 100).WithFriendlyDecimals();

		if (Math.Abs(withFriendlyDecimals) < precision)
		{
			var threshold = n > 0 ? "+" + precision : "-" + precision;
			return "less than " + threshold.ToString(CultureInfo.InvariantCulture) + "%";
		}
		else
		{
			var diffPart = withFriendlyDecimals.ToString();
			var numericPart = n > 0 ? "+" + diffPart : diffPart;
			return numericPart + "%";
		}
	}
}
