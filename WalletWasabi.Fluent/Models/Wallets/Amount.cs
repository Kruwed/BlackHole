using NBitcoin;
using WalletWasabi.Fluent.Helpers;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>
/// Encapsulates a BTC amount for display purposes.
/// </summary>
public class Amount
{
	public static readonly Amount Zero = new(Money.Zero);

	public Amount(Money? money)
	{
		Btc = money ?? Money.Zero;
	}

	public Money Btc { get; }

	public bool HasBalance => Btc != Money.Zero;

	public string FormattedBtc => Btc.ToFormattedString();
}
