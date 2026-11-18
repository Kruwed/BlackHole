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
	/// Keeps the given coins out of coinjoins or lets them in again. The coins that are not named stay as they are.
	/// </summary>
	/// <param name="exclusions">For every coin that is to change, whether it is kept out.</param>
	public async Task ExcludeFromCoinjoinAsync(IReadOnlyDictionary<OutPoint, bool> exclusions)
	{
		await Task.Run(() => Wallet.ExcludeCoinsFromCoinJoin(exclusions));
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

	protected override int AnonScoreTarget => Wallet.AnonScoreTarget;

	protected override IEnumerable<SmartCoin> GetCoins() => Wallet.Coins;
}
