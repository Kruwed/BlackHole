using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Blockchain.Analysis.Clustering;

/// <summary>
/// Keeps track of which coins of the wallet are linked to each other: the coins of one <see cref="Cluster"/> can be told
/// to belong together by what the blockchain shows. The wallet uses it to tell what a transaction would give away by
/// spending coins together.
/// </summary>
public static class CoinLinker
{
	/// <summary>
	/// Links the coins that a transaction gives to the wallet to the coins of the wallet that it spends. A matched
	/// output is the exception: an amount that another output of the transaction has too does not tell whose input paid
	/// for it. An amount that occurs once, such as change, is followed back to the inputs.
	/// </summary>
	public static void LinkOutputsToInputs(SmartTransaction tx)
	{
		foreach (var newCoin in tx.WalletOutputs.Where(coin => !coin.IsMatchedOutput))
		{
			foreach (var spentCoin in tx.WalletInputs)
			{
				newCoin.HdPubKey.Cluster.Merge(spentCoin.HdPubKey.Cluster);
			}
		}
	}
}
