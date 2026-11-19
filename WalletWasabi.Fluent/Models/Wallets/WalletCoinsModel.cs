using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class WalletCoinsModel(Wallet wallet, IWalletModel walletModel, IServices services)
	: CoinListModel(wallet, walletModel, services)
{
	/// <summary>
	/// Chooses the given coins for coinjoins or takes them out of them again. The coins that are not named stay as they are.
	/// </summary>
	/// <param name="selections">For every coin that is to change, whether it is chosen.</param>
	public async Task SelectForCoinjoinAsync(IReadOnlyDictionary<OutPoint, bool> selections)
	{
		await Task.Run(() => Wallet.SelectCoinsForCoinJoin(selections));
	}

	public List<CoinModel> GetSpentCoins(BuildTransactionResult? transaction)
	{
		var coins = (transaction?.SpentCoins ?? new List<SmartCoin>()).ToList();
		return coins.Select(GetCoinModel).ToList();
	}

	public bool AreEnoughToCreateTransaction(TransactionInfo transactionInfo, IEnumerable<CoinModel> coins)
	{
		return TransactionHelpers.TryBuildTransactionWithoutPrevTx(Wallet.KeyManager, transactionInfo, Wallet.Coins, coins.GetSmartCoins(), Wallet.Password, out _);
	}

	protected override IEnumerable<SmartCoin> GetCoins() => Wallet.Coins;
}
