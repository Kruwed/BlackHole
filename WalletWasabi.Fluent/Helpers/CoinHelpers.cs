using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Helpers;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Fluent.Helpers;

public static class CoinHelpers
{
	public static uint GetConfirmations(this SmartCoin coin, uint bestHeight) => coin.Transaction.GetConfirmations(bestHeight);

	public static PrivacyLevel GetPrivacyLevel(this SmartCoin coin, int privateThreshold)
	{
		if (coin.IsPrivate(privateThreshold))
		{
			return PrivacyLevel.Private;
		}

		if (coin.IsSemiPrivate(privateThreshold))
		{
			return PrivacyLevel.SemiPrivate;
		}

		return PrivacyLevel.NonPrivate;
	}
}
