using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Helpers;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Fluent.Helpers;

public static class CoinHelpers
{
	public static uint GetConfirmations(this SmartCoin coin, uint bestHeight) => coin.Transaction.GetConfirmations(bestHeight);

	/// <summary>
	/// Sorts a coin by its anonymity score: private from <see cref="Constants.PrivateThreshold"/> on, semi-private from
	/// <see cref="Constants.SemiPrivateThreshold"/> on, non-private below that.
	/// </summary>
	public static PrivacyLevel GetPrivacyLevel(this SmartCoin coin)
	{
		if (coin.IsPrivate(Constants.PrivateThreshold))
		{
			return PrivacyLevel.Private;
		}

		if (coin.IsSemiPrivate(Constants.PrivateThreshold))
		{
			return PrivacyLevel.SemiPrivate;
		}

		return PrivacyLevel.NonPrivate;
	}
}
