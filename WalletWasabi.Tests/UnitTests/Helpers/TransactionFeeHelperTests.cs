using NBitcoin;
using System.Collections.Generic;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Helpers;

public class TransactionFeeHelperTests
{
	[Fact]
	public void MissingEstimatesAreUnavailable()
	{
		Assert.False(TransactionFeeHelper.TryGetFeeEstimates(feeRateEstimations: null, out var estimates));
		Assert.Null(estimates);
	}

	[Fact]
	public void EmptyEstimatesAreUnavailable()
	{
		Assert.False(TransactionFeeHelper.TryGetFeeEstimates(FeeRateEstimations.Empty, out var estimates));
		Assert.Null(estimates);
	}

	[Fact]
	public void FilteredOutEstimatesAreUnavailable()
	{
		var feeEstimates = new FeeRateEstimations(new Dictionary<int, FeeRate>
		{
			[0] = new(1m),
			[1009] = new(1m)
		});

		Assert.False(TransactionFeeHelper.TryGetFeeEstimates(feeEstimates, out var estimates));
		Assert.Null(estimates);
	}

	[Fact]
	public void AvailableEstimatesArePreserved()
	{
		var feeEstimates = new FeeRateEstimations(new Dictionary<int, FeeRate> { [2] = new(1m) });

		Assert.True(TransactionFeeHelper.TryGetFeeEstimates(feeEstimates, out var estimates));
		Assert.Same(feeEstimates, estimates);
	}
}
