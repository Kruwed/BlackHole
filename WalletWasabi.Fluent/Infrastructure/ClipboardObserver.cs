using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Userfacing;

namespace WalletWasabi.Fluent.Infrastructure;

internal class ClipboardObserver
{
	public static Money? ParseToBtc(string? text)
	{
		if (text is null)
		{
			return null;
		}

		return Money.TryParse(text, out var n) ? n : default;
	}

	public static string? ParseToBtc(string? text, Money balance)
	{
		// Ignore paste if there are invalid characters
		if (text is null || !CurrencyInput.IsValidDecimal(text, out _))
		{
			return null;
		}

		var money = ParseToBtc(text).Ensure(m => m <= balance);
		return money?.ToDecimal(MoneyUnit.BTC).FormattedBtcExactFractional(text);
	}
}
