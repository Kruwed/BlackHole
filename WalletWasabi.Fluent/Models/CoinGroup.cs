using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;

namespace WalletWasabi.Fluent.Models;

/// <summary>
/// The coins of a wallet that have the same privacy level. Coin lists show the coins in these groups.
/// </summary>
public record CoinGroup(PrivacyLevel PrivacyLevel, ICoinsView Coins)
{
	public Money Amount => Coins.TotalAmount();

	public string Title => GetTitle(PrivacyLevel);

	public static string GetTitle(PrivacyLevel privacyLevel) =>
		privacyLevel switch
		{
			PrivacyLevel.Private => "Private Coins",
			PrivacyLevel.SemiPrivate => "Semi-private Coins",
			PrivacyLevel.NonPrivate => "Non-private Coins",
			_ => throw new ArgumentOutOfRangeException(nameof(privacyLevel))
		};
}
