using WalletWasabi.Blockchain.TransactionOutputs;

namespace WalletWasabi.Helpers;

public static class SmartCoinExtensions
{
	extension(SmartCoin coin)
	{
		public bool IsRedCoin(int semiPrivateThreshold = Constants.SemiPrivateThreshold) => coin.AnonymitySet < semiPrivateThreshold;
	}
}
