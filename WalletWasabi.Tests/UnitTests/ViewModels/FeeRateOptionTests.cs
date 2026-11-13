using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.ViewModels.Wallets.Send;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="FeeRateOption"/>.
/// </summary>
public class FeeRateOptionTests
{
	private static readonly FeeRateEstimations Estimations = new(new Dictionary<int, FeeRate>
	{
		[2] = new(102m),
		[3] = new(20m),
		[6] = new(10m),
		[18] = new(1.5m)
	});

	[Fact]
	public void OptionsAreOrderedFastestFirst()
	{
		var options = FeeRateOption.FromEstimations(Estimations);

		Assert.Equal([2, 3, 6, 18], options.Select(x => x.BlockTarget));
		Assert.Equal([102m, 20m, 10m, 1.5m], options.Select(x => x.FeeRate.SatoshiPerByte));
	}

	[Fact]
	public void OptionsAboveTheMaximumFeeRateAreLeftOut()
	{
		var options = FeeRateOption.FromEstimations(Estimations, maximumFeeRate: new FeeRate(20m));

		Assert.Equal([3, 6, 18], options.Select(x => x.BlockTarget));

		Assert.Empty(FeeRateOption.FromEstimations(Estimations, maximumFeeRate: new FeeRate(1m)));
	}

	[Fact]
	public void TextsMentionBlocksAndFeeRateOnly()
	{
		var option = FeeRateOption.FromEstimations(Estimations)[^1];

		Assert.Equal("Within 18 blocks", option.BlockTargetText);
		Assert.Equal("1.5 sat/vByte", option.FeeRateText);
		Assert.Equal("Next block", new FeeRateOption(1, new FeeRate(50m)).BlockTargetText);
	}

	[Theory]
	[InlineData(1, 2)]
	[InlineData(2, 2)]
	[InlineData(4, 3)]
	[InlineData(5, 6)]
	[InlineData(1000, 18)]
	public void DefaultIsTheClosestToThePreferredBlockTarget(int preferredBlockTarget, int expectedBlockTarget)
	{
		var options = FeeRateOption.FromEstimations(Estimations);

		var selected = FeeRateOption.SelectDefault(options, currentFeeRate: null, preferredBlockTarget);

		Assert.NotNull(selected);
		Assert.Equal(expectedBlockTarget, selected.BlockTarget);
	}

	[Theory]
	[InlineData(500, 2)] // Can afford the fastest one.
	[InlineData(102, 2)]
	[InlineData(50, 3)] // The fastest one that is not more expensive.
	[InlineData(10, 6)]
	[InlineData(1, 18)] // Cheaper than everything: the cheapest one.
	public void DefaultFollowsAnAlreadyChosenFeeRate(decimal currentSatPerByte, int expectedBlockTarget)
	{
		var options = FeeRateOption.FromEstimations(Estimations);

		var selected = FeeRateOption.SelectDefault(options, new FeeRate(currentSatPerByte), preferredBlockTarget: 18);

		Assert.NotNull(selected);
		Assert.Equal(expectedBlockTarget, selected.BlockTarget);
	}

	[Fact]
	public void NothingToSelectWithoutOptions()
	{
		Assert.Null(FeeRateOption.SelectDefault([], new FeeRate(10m), preferredBlockTarget: 2));
	}
}
