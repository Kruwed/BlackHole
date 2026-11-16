using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class UserSelectionCoinListModel(Wallet wallet, IWalletModel walletModel, SmartCoin[] selectedCoins, IServices services)
	: CoinListModel(wallet, walletModel, services)
{
	protected override IEnumerable<SmartCoin> GetCoins() => selectedCoins;
}
