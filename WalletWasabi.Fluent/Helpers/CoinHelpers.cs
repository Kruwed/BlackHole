using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Fluent.Helpers;

public static class CoinHelpers
{
	public static uint GetConfirmations(this SmartCoin coin, uint bestHeight) => coin.Transaction.GetConfirmations(bestHeight);
}
