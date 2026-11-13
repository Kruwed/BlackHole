using NBitcoin;
using Nito.AsyncEx;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Transactions;

public partial class PrivacySuggestionsModel
{
	/// <summary>How far a suggested transaction may deviate from the requested amount.</summary>
	private const decimal MaximumDifferenceTolerance = 0.15m;

	private const decimal LargePortionSpentTolerance = 0.9m;
	private static readonly Money LargePortionSpentMinAmount = new (1_000_000L);

	/// <summary>Allow at most one suggestion generation run.</summary>
	private readonly AsyncLock _asyncLock = new();

	private readonly CoinJoinManager? _cjManager;
	private readonly SendFlowModel _sendFlow;
	private readonly IServices _services;
	private readonly Wallet _wallet;

	public PrivacySuggestionsModel(IServices services, SendFlowModel sendFlow)
	{
		_sendFlow = sendFlow;
		_wallet = sendFlow.Wallet;
		_cjManager = services.GetHostedService<CoinJoinManager>();
		_services = services;
	}

	/// <remarks>Method supports being called multiple times. In that case the last call cancels the previous one.</remarks>
	public async IAsyncEnumerable<PrivacyItem> BuildPrivacySuggestionsAsync(
		TransactionInfo transactionInfo,
		BuildTransactionResult transactionResult,
		bool includeSuggestions,
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var parameters = new Parameters(transactionInfo, transactionResult, includeSuggestions);
		var result = new List<PrivacyItem>();

		using (await _asyncLock.LockAsync(cancellationToken))
		{
			result.Add(VerifyLabels(parameters));
			result.Add(VerifyRecipients(parameters));
			result.Add(VerifyPrivacyLevel(parameters));
			result.Add(VerifyLargePercentSpent(parameters));
			result.Add(VerifyUnconfirmedInputs(parameters));
			result.Add(VerifyCoinjoiningInputs(parameters));
			result.Add(VerifyChange(parameters));
			foreach (var item in result)
			{
				yield return item;
			}
		}
	}

	private IEnumerable<PrivacyItem> VerifyLabels(Parameters parameters)
	{
		var warning = GetLabelWarning(parameters.Transaction, parameters.TransactionInfo.Recipient);

		if (warning is not null)
		{
			yield return warning;

			if (parameters.IncludeSuggestions && parameters.TransactionInfo.IsOtherPocketSelectionPossible)
			{
				yield return new LabelManagementSuggestion();
			}
		}
	}

	private PrivacyItem? GetLabelWarning(BuildTransactionResult transactionResult, LabelsArray recipient)
	{
		var pockets = _sendFlow.GetPockets();
		var spentCoins = transactionResult.SpentCoins;
		var nonPrivateSpentCoins = spentCoins.Where(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.NonPrivate).ToList();
		var usedPockets = pockets.Where(x => x.Coins.Any(coin => nonPrivateSpentCoins.Contains(coin))).ToList();

		if (usedPockets.Count > 1)
		{
			var pocketLabels = usedPockets.SelectMany(x => x.Labels).Distinct().ToArray();
			return new InterlinksLabelsWarning(new LabelsArray(pocketLabels));
		}

		var labels = transactionResult.SpentCoins.SelectMany(x => x.GetLabels(_wallet.AnonScoreTarget)).Except(recipient);
		var labelsArray = new LabelsArray(labels);

		if (!labelsArray.IsEmpty)
		{
			return new TransactionKnownAsYoursByWarning(labelsArray);
		}

		return null;
	}

	private IEnumerable<PrivacyItem> VerifyRecipients(Parameters parameters)
	{
		var recipients = parameters.TransactionInfo.AllRecipients.Select(x => x.Label).Distinct(LabelsComparer.Instance).ToArray();

		if (recipients.Length > 1)
		{
			yield return new InterlinksRecipientsWarning(LabelsArray.Merge(recipients));
		}
	}

	private IEnumerable<PrivacyItem> VerifyPrivacyLevel(Parameters parameters)
	{
		var canModifyTransactionAmount = !parameters.TransactionInfo.IsFixedAmount && !parameters.TransactionInfo.IsPayToMany;

		var transactionLabels = parameters.Transaction.SpentCoins.SelectMany(x => x.GetLabels(_wallet.AnonScoreTarget));
		var onlyKnownByRecipient =
			parameters.TransactionInfo.Recipient.Equals(new LabelsArray(transactionLabels), StringComparer.OrdinalIgnoreCase);

		var foundNonPrivate = !onlyKnownByRecipient &&
							  parameters.Transaction.SpentCoins.Any(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.NonPrivate);

		var foundSemiPrivate =
			parameters.Transaction.SpentCoins.Any(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.SemiPrivate);

		if (foundNonPrivate)
		{
			yield return new NonPrivateFundsWarning();
		}

		if (foundSemiPrivate)
		{
			yield return new SemiPrivateFundsWarning();
		}

		if (!parameters.IncludeSuggestions)
		{
			// Return early, to avoid needless compute.
			yield break;
		}

		var availableCoins = _sendFlow.AvailableCoins;

		ImmutableList<SmartCoin> coinsToExclude = _cjManager?.CoinsInCriticalPhase[_wallet.WalletId] ?? [];
		bool wasCoinjoiningCoinUsed = parameters.Transaction.SpentCoins.Any(coinsToExclude.Contains);

		// Only exclude coins if the original transaction doesn't use them either.
		var allPrivateCoin = availableCoins.Where(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.Private).ToArray();

		allPrivateCoin = wasCoinjoiningCoinUsed ? allPrivateCoin : allPrivateCoin.Except(coinsToExclude).ToArray();

		var onlyKnownByTheRecipientCoins = availableCoins.Where(x => parameters.TransactionInfo.Recipient.Equals(x.GetLabels(_wallet.AnonScoreTarget), StringComparer.OrdinalIgnoreCase)).ToArray();
		var allSemiPrivateCoin =
			availableCoins.Where(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.SemiPrivate)
						  .Union(onlyKnownByTheRecipientCoins)
						  .ToArray();

		allSemiPrivateCoin = wasCoinjoiningCoinUsed ? allSemiPrivateCoin : allSemiPrivateCoin.Except(coinsToExclude).ToArray();

		var totalAmount = parameters.Transaction.CalculateDestinationAmount(parameters.TransactionInfo.Destination).ToDecimal(MoneyUnit.BTC);
		FullPrivacySuggestion? fullPrivacySuggestion = null;

		if ((foundNonPrivate || foundSemiPrivate) && allPrivateCoin.Length != 0 &&
			TryCreateTransaction(parameters.TransactionInfo, allPrivateCoin, out var newTransaction, out var isChangeless))
		{
			var amountDifference = totalAmount - newTransaction.CalculateDestinationAmount(parameters.TransactionInfo.Destination).ToDecimal(MoneyUnit.BTC);
			var amountDifferencePercentage = amountDifference / totalAmount;

			if (amountDifferencePercentage <= MaximumDifferenceTolerance && (canModifyTransactionAmount || amountDifference == 0m))
			{
				var differenceBtc = GetDifference(parameters.TransactionInfo, newTransaction);
				var differenceText = GetBtcDifferenceText(differenceBtc);
				var differenceAmountText = GetDifferenceAmountText(differenceBtc);
				fullPrivacySuggestion = new FullPrivacySuggestion(newTransaction, amountDifference, differenceText, differenceAmountText, allPrivateCoin, isChangeless);
				yield return fullPrivacySuggestion;
			}
		}

		// Do not calculate the better privacy option when the full privacy option has the same amount.
		// This is only possible if the user makes a silly selection with coin control.
		if (fullPrivacySuggestion is { } sug && sug.Difference == 0m)
		{
			yield break;
		}

		var coins = allPrivateCoin.Union(allSemiPrivateCoin).ToArray();
		if (foundNonPrivate && allSemiPrivateCoin.Length != 0 &&
			TryCreateTransaction(parameters.TransactionInfo, coins, out newTransaction, out isChangeless))
		{
			var amountDifference = totalAmount - newTransaction.CalculateDestinationAmount(parameters.TransactionInfo.Destination).ToDecimal(MoneyUnit.BTC);
			var amountDifferencePercentage = amountDifference / totalAmount;

			if (amountDifferencePercentage <= MaximumDifferenceTolerance && (canModifyTransactionAmount || amountDifference == 0m))
			{
				var btcDifference = GetDifference(parameters.TransactionInfo, newTransaction);
				var differenceText = GetBtcDifferenceText(btcDifference);
				var differenceAmountText = GetDifferenceAmountText(btcDifference);
				yield return new BetterPrivacySuggestion(newTransaction, differenceText, differenceAmountText, coins, isChangeless);
			}
		}
	}

	private IEnumerable<PrivacyItem> VerifyLargePercentSpent(Parameters parameters)
	{
		// No need to check this warning if a Red Coin is selected.
		if(parameters.Transaction.SpentCoins.Any(x => x.GetPrivacyLevel(_wallet.AnonScoreTarget) == PrivacyLevel.NonPrivate))
		{
			yield break;
		}

		var spentAmount = parameters.Transaction.SpentCoins.Sum(x => x.Amount);
		var walletBalance = _wallet.Coins.TotalAmount().Satoshi;

		if (spentAmount <= LargePortionSpentMinAmount || spentAmount <= walletBalance * LargePortionSpentTolerance)
		{
			yield break;
		}

		var spentPercentage = (int)(spentAmount * 100m / walletBalance);
		yield return new LargePercentSpentWarning(spentPercentage);
	}

	private IEnumerable<PrivacyItem> VerifyUnconfirmedInputs(Parameters parameters)
	{
		if (parameters.Transaction.SpendsUnconfirmed)
		{
			yield return new UnconfirmedFundsWarning();
		}
	}

	private IEnumerable<PrivacyItem> VerifyCoinjoiningInputs(Parameters parameters)
	{
		if (parameters.Transaction.SpendsCoinjoining)
		{
			yield return new CoinjoiningFundsWarning();
		}
	}

	private IEnumerable<PrivacyItem> VerifyChange(Parameters parameters)
	{
		var destinationScripts = parameters.TransactionInfo.AllRecipients
			.Select(r => r.Destination.GetScriptPubKey())
			.ToHashSet();

		bool hasChange = parameters.Transaction.InnerWalletOutputs
			.Any(x => !destinationScripts.Contains(x.ScriptPubKey));

		if (hasChange)
		{
			yield return new CreatesChangeWarning();
		}
	}

	private bool TryCreateTransaction(
		TransactionInfo transactionInfo,
		SmartCoin[] coins,
		[NotNullWhen(true)] out BuildTransactionResult? txn,
		out bool isChangeless)
	{
		txn = null;
		isChangeless = false;

		try
		{
			// TODO: Verify the subtract fee change
			txn = _wallet.BuildTransaction(
				transactionInfo.Destination,
				transactionInfo.Amount,
				transactionInfo.Recipient,
				transactionInfo.FeeRate,
				coins,
				false,
				tryToSign: false);
		}
		catch (Exception)
		{
			try
			{
				txn = _wallet.BuildChangelessTransaction(
					transactionInfo.Destination,
					transactionInfo.Recipient,
					transactionInfo.FeeRate,
					coins,
					tryToSign: false);

				isChangeless = true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		return true;
	}

	private decimal GetDifference(TransactionInfo transactionInfo, BuildTransactionResult transaction)
	{
		var originalAmount = transactionInfo.Amount.ToDecimal(MoneyUnit.BTC);
		var totalAmount = transaction.CalculateDestinationAmount(transactionInfo.Destination);
		var total = totalAmount.ToDecimal(MoneyUnit.BTC);

		return total - originalAmount;
	}

	private string GetBtcDifferenceText(decimal btcDifference)
	{
		return btcDifference switch
		{
			> 0 => "more",
			< 0 => "less",
			_ => "the same amount"
		};
	}

	private string GetDifferenceAmountText(decimal btcDifference)
	{
		var btcDifferenceStr = Math.Abs(btcDifference).FormattedBtcFixedFractional();

		return $"{btcDifferenceStr} BTC";
	}

	private record Parameters(TransactionInfo TransactionInfo, BuildTransactionResult Transaction, bool IncludeSuggestions);
}
