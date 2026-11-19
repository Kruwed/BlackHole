using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Transactions;

/// <summary>
/// Tells what a transaction gives away, for the user to know before it is sent.
/// </summary>
public class PrivacyWarningsModel
{
	private const decimal LargePortionSpentTolerance = 0.9m;
	private static readonly Money LargePortionSpentMinAmount = new(1_000_000L);

	private readonly Wallet _wallet;

	public PrivacyWarningsModel(Wallet wallet)
	{
		_wallet = wallet;
	}

	public IReadOnlyList<PrivacyWarning> GetWarnings(TransactionInfo transactionInfo, BuildTransactionResult transaction)
	{
		var warnings = new List<PrivacyWarning>();

		if (LinksCoins(transaction.SpentCoins))
		{
			warnings.Add(new InterlinksCoinsWarning());
		}

		if (transactionInfo.AllRecipients.Count() > 1)
		{
			warnings.Add(new InterlinksRecipientsWarning());
		}

		if (GetLargePercentSpent(transaction.SpentCoins.Sum(x => x.Amount), _wallet.Coins.TotalAmount()) is { } percentSpent)
		{
			warnings.Add(new LargePercentSpentWarning(percentSpent));
		}

		if (transaction.SpendsUnconfirmed)
		{
			warnings.Add(new UnconfirmedFundsWarning());
		}

		if (transaction.SpendsCoinjoining)
		{
			warnings.Add(new CoinjoiningFundsWarning());
		}

		if (CreatesChange(transactionInfo, transaction))
		{
			warnings.Add(new CreatesChangeWarning());
		}

		return warnings;
	}

	/// <summary>
	/// Whether spending the coins together links coins that nothing linked to each other so far. Coins that are spent
	/// together are known to belong to one wallet from then on, whatever the coins are.
	/// </summary>
	public static bool LinksCoins(IEnumerable<SmartCoin> spentCoins) =>
		spentCoins
			.Select(x => x.HdPubKey.Cluster)
			.Distinct()
			.Count() > 1;

	/// <summary>
	/// The share of the balance that a transaction spends, in percent, if it is large enough to tell which coins it
	/// comes from by its amount: more than nine tenths of the balance and more than 0.01 BTC. Null otherwise.
	/// </summary>
	public static int? GetLargePercentSpent(Money spentAmount, Money walletBalance)
	{
		if (walletBalance <= Money.Zero
			|| spentAmount <= LargePortionSpentMinAmount
			|| spentAmount.Satoshi <= walletBalance.Satoshi * LargePortionSpentTolerance)
		{
			return null;
		}

		return (int)(spentAmount.Satoshi * 100m / walletBalance.Satoshi);
	}

	private static bool CreatesChange(TransactionInfo transactionInfo, BuildTransactionResult transaction)
	{
		var destinationScripts = transactionInfo.AllRecipients
			.Select(r => r.Destination.GetScriptPubKey())
			.ToHashSet();

		return transaction.InnerWalletOutputs.Any(x => !destinationScripts.Contains(x.ScriptPubKey));
	}
}
