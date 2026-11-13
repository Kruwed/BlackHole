using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

/// <summary>A fee rate preset: the fee rate estimated to confirm within the given number of blocks.</summary>
public record FeeRateOption(int BlockTarget, FeeRate FeeRate)
{
	public string BlockTargetText => BlockTarget <= 1 ? "Next block" : $"Within {BlockTarget} blocks";

	public string FeeRateText => $"{FeeRate.SatoshiPerByte.ToString("0.###", CultureInfo.InvariantCulture)} sat/vByte";

	/// <summary>
	/// Turns fee rate estimations into presets, fastest first.
	/// </summary>
	/// <param name="maximumFeeRate">Presets above this fee rate are left out, because the transaction cannot pay for them.</param>
	public static IReadOnlyList<FeeRateOption> FromEstimations(FeeRateEstimations estimations, FeeRate? maximumFeeRate = null) =>
		estimations.Estimations
			.Where(x => maximumFeeRate is null || x.Value <= maximumFeeRate)
			.Select(x => new FeeRateOption(x.Key, x.Value))
			.ToList();

	/// <summary>
	/// Picks the preset to preselect: the one matching an already chosen fee rate, otherwise the one closest to the preferred block target.
	/// </summary>
	public static FeeRateOption? SelectDefault(IReadOnlyList<FeeRateOption> options, FeeRate? currentFeeRate, int preferredBlockTarget)
	{
		if (options.Count == 0)
		{
			return null;
		}

		if (currentFeeRate is not null && currentFeeRate != FeeRate.Zero)
		{
			// The fastest preset that does not cost more than what is already selected, or the cheapest one.
			return options.FirstOrDefault(x => x.FeeRate <= currentFeeRate) ?? options[^1];
		}

		return options.MinBy(x => Math.Abs(x.BlockTarget - preferredBlockTarget));
	}
}
