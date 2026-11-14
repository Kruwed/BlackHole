using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Blockchain.TransactionBuilding;

public class BuildTransactionResult
{
	public BuildTransactionResult(SmartTransaction transaction, Money fee, decimal feePercentOfSent)
	{
		Transaction = transaction;
		Fee = fee;
		FeePercentOfSent = feePercentOfSent;
	}

	public SmartTransaction Transaction { get; }
	public Money Fee { get; }
	public decimal FeePercentOfSent { get; }
	public bool SpendsUnconfirmed => Transaction.WalletInputs.Any(c => !c.Confirmed);
	public bool SpendsCoinjoining => Transaction.WalletInputs.Any(c => c.CoinJoinInProgress);
	public IEnumerable<SmartCoin> InnerWalletOutputs => Transaction.WalletOutputs;
	public IEnumerable<SmartCoin> SpentCoins => Transaction.WalletInputs;

	public IEnumerable<Coin> OuterWalletOutputs
	{
		get
		{
			var outputs = Transaction.Transaction.Outputs;
			var ownOutputIndexes = Transaction.WalletOutputs.Select(x => x.Index).ToHashSet();
			for (uint i = 0; i < outputs.Count; i++)
			{
				if (!ownOutputIndexes.Contains(i))
				{
					yield return new Coin(Transaction.Transaction, i);
				}
			}
		}
	}
}
