using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Exceptions;
using WalletWasabi.Extensions;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Helpers;

public static class TransactionHelpers
{
	public static BuildTransactionResult BuildTransaction(Wallet wallet, TransactionInfo transactionInfo, bool tryToSign = true)
	{
		if (transactionInfo.IsPayToMany)
		{
			return BuildPayToManyTransaction(wallet, transactionInfo, tryToSign);
		}

		return wallet.BuildTransaction(
			transactionInfo.Destination,
			transactionInfo.Amount,
			transactionInfo.FeeRate,
			transactionInfo.Coins,
			transactionInfo.SubtractFee,
			tryToSign: tryToSign);
	}

	private static BuildTransactionResult BuildPayToManyTransaction(Wallet wallet, TransactionInfo transactionInfo, bool tryToSign)
	{
		var intent = BuildPayToManyIntent(transactionInfo);

		return wallet.BuildTransaction(
			password: wallet.Password,
			payments: intent,
			feeStrategy: FeeStrategy.CreateFromFeeRate(transactionInfo.FeeRate),
			allowUnconfirmed: true,
			allowedInputs: transactionInfo.Coins.Select(c => c.Outpoint),
			tryToSign: tryToSign);
	}

	public static bool TryBuildTransactionWithoutPrevTx(
		KeyManager keyManager,
		TransactionInfo transactionInfo,
		ICoinsView allCoins,
		IEnumerable<SmartCoin> allowedCoins,
		string password,
		out Money minimumAmount)
	{
		minimumAmount = transactionInfo.IsPayToMany ? transactionInfo.TotalAmount : transactionInfo.Amount;

		try
		{
			PaymentIntent intent;
			if (transactionInfo.IsPayToMany)
			{
				intent = BuildPayToManyIntent(transactionInfo);
			}
			else
			{
				intent = new PaymentIntent(
					destination: transactionInfo.Destination,
					amount: transactionInfo.Amount,
					subtractFee: transactionInfo.SubtractFee);
			}

			var network = keyManager.GetNetwork();
			var builder = new TransactionFactory(network, keyManager, allCoins, password);

			TransactionParameters parameters = new (
				intent,
				transactionInfo.FeeRate,
				AllowUnconfirmed: true,
				AllowDoubleSpend: false,
				AllowedInputs: allowedCoins.Select(x => x.Outpoint),
				TryToSign: false,
				OverrideFeeOverpaymentProtection: false);

			builder.BuildTransaction(
				parameters,
				lockTimeSelector: () => LockTime.Zero); // Doesn't matter.

			return true;
		}
		catch (InsufficientBalanceException ex)
		{
			minimumAmount = ex.Minimum;
		}
		catch (Exception)
		{
			// Ignore.
		}

		return false;
	}

	internal static PaymentIntent BuildPayToManyIntent(TransactionInfo transactionInfo)
	{
		var requests = transactionInfo.AllRecipients
			.Select((r, index) =>
			{
				bool subtractFee = index == 0 ? transactionInfo.SubtractFee : r.IsSubtractFee;
				return new DestinationRequest(r.Destination, MoneyRequest.Create(r.Amount, subtractFee));
			})
			.ToArray();

		return new PaymentIntent(requests);
	}
}
