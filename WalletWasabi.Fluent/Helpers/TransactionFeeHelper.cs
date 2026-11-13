using System.Collections.Generic;
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
	private static readonly FeeRateEstimations TestNetFeeRateEstimations = new(
		new Dictionary<int, FeeRate>
		{
			[1] = new( 17m),
			[2] = new( 12m),
			[3] = new( 9m),
			[6] = new( 9m),
			[18] = new( 2m),
			[36] = new( 2m),
			[72] = new( 2m),
			[144] = new( 2m),
			[432] = new( 1m),
			[1008] = new( 1m)
		});

	public static bool TryGetFeeEstimates(Wallet wallet, [NotNullWhen(true)] out FeeRateEstimations? estimates)
		=> TryGetFeeEstimates(wallet.FeeRateEstimations, wallet.Network, out estimates);

	public static bool TryGetFeeEstimates(FeeRateEstimations? feeRateEstimations, Network network, [NotNullWhen(true)] out FeeRateEstimations? estimates)
	{
		if (network == Network.TestNet)
		{
			estimates = TestNetFeeRateEstimations;
			return true;
		}
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
					found = FeeHelpers.TryGetMaxFeeRate(wallet, info.Destination, info.Amount, info.Recipient, info.FeeRate, info.Coins, info.SubtractFee, out rate);
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
