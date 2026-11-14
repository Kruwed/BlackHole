using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Transactions;

public record SendFlowModel
{
	private SendFlowModel(Wallet wallet, ICoinsView availableCoins, ICoinListModel coinListModel, bool donate)
	{
		Wallet = wallet;
		AvailableCoins = availableCoins;
		CoinList = coinListModel;
		Donate = donate;
	}

	/// <summary>A send flow uses only the coins that the user picked for it.</summary>
	public SendFlowModel(Wallet wallet, IWalletModel walletModel, IEnumerable<SmartCoin> coins, IServices services, bool donate = false):
		this(wallet, new CoinsView(coins), new UserSelectionCoinListModel(wallet, walletModel, coins.ToArray(), services), donate)
	{
	}

	public Wallet Wallet { get; }

	public ICoinsView AvailableCoins { get; }

	public ICoinListModel CoinList { get; }

	public TransactionInfo? TransactionInfo { get; init; } = null;

	public bool Donate { get; init; }

	public decimal AvailableAmountBtc => AvailableAmount.ToDecimal(MoneyUnit.BTC);

	public Money AvailableAmount => AvailableCoins.TotalAmount();
}
