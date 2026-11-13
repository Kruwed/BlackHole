using System.Collections.Generic;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Helpers;

public static class WalletHelpers
{
	public static List<LabelsByWallet> GetLabelsByWallets(IEnumerable<Wallet> wallets)
	{
		var result = new List<LabelsByWallet>();

		foreach (var wallet in wallets)
		{
			var (changeLabels, receiveLabels) = wallet.KeyManager.GetLabels();
			result.Add(new LabelsByWallet(wallet.WalletId, changeLabels, receiveLabels));
		}

		return result;
	}

	public record LabelsByWallet(WalletId WalletId, List<string> ChangeLabels, List<string> ReceiveLabels);
}
