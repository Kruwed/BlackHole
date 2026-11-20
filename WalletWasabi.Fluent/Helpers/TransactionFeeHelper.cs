using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using WalletWasabi.Helpers;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Helpers;

public static class TransactionFeeHelper
{
	public static bool TryGetFeeEstimates(Wallet wallet, [NotNullWhen(true)] out FeeRateEstimations? estimates)
		=> TryGetFeeEstimates(wallet.FeeRateEstimations, out estimates);

	public static bool TryGetFeeEstimates(FeeRateEstimations? feeRateEstimations, [NotNullWhen(true)] out FeeRateEstimations? estimates)
	{
		if (feeRateEstimations is { Estimations.Count: > 0 })
		{
			estimates = feeRateEstimations;
			return true;
		}

		estimates = null;
		return false;
	}

	/// <summary>
	/// Seeks for the maximum possible fee rate that the transaction can pay.
	/// </summary>
	/// <remarks>The method does not throw any exception.</remarks>
	/// <remarks>Stores the found fee rate in the received <see cref="TransactionInfo"/> object. </remarks>
	/// <returns>True if the seeking was successful, False if not.</returns>
	public static async Task<bool> TrySetMaxFeeRateAsync(UiContext uiContext, Wallet wallet, TransactionInfo info)
	{
		var maxFeeRate =
			await Task.Run(() =>
			{
				bool found;
				FeeRate? rate;

				if (info.IsPayToMany)
				{
					var intent = TransactionHelpers.BuildPayToManyIntent(info);
					found = FeeHelpers.TryGetMaxFeeRateForPayToMany(wallet, intent, info.FeeRate, info.Coins, out rate);
				}
				else
				{
					found = FeeHelpers.TryGetMaxFeeRate(wallet, info.Destination, info.Amount, info.FeeRate, info.Coins, info.SubtractFee, out rate);
				}

				return found ? rate! : new FeeRate(0m);
			});

		if (EnsureFeeRateIsPossible(wallet, maxFeeRate))
		{
			info.MaximumPossibleFeeRate = maxFeeRate;
			info.FeeRate = maxFeeRate;

			return true;
		}

		return false;
	}

	/// <summary>
	/// A fee rate is usable when estimations are available and it is not below the cheapest estimated fee rate.
	/// </summary>
	private static bool EnsureFeeRateIsPossible(Wallet wallet, FeeRate feeRate)
	{
		if (!TryGetFeeEstimates(wallet, out var feeEstimates))
		{
			return false;
		}

		return feeEstimates.Estimations.Values.Min() <= feeRate;
	}
}
