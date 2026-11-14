using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Models;

namespace WalletWasabi.Fluent.Helpers;

public static class CoinGroupHelper
{
	/// <summary>
	/// Groups the coins by their privacy level. Levels without coins are left out.
	/// </summary>
	public static IEnumerable<CoinGroup> GetCoinGroups(this IEnumerable<SmartCoin> coins, int privateAnonSetThreshold) =>
		coins
			.GroupBy(coin => coin.GetPrivacyLevel(privateAnonSetThreshold))
			.OrderBy(group => group.Key)
			.Select(group => new CoinGroup(group.Key, new CoinsView(group)));
}
