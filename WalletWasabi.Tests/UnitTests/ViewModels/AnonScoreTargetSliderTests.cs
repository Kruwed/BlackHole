using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.ViewModels.Wallets.Settings;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The slider of the anonymity score target in the coinjoin settings stands between 0 and 1 on a logarithmic scale.
/// </summary>
public class AnonScoreTargetSliderTests
{
	[Fact]
	public void EndsOfTheSliderAreTheLimitsOfTheTarget()
	{
		Assert.Equal(KeyManager.MinAnonScoreTarget, WalletCoinJoinSettingsViewModel.SliderPositionToAnonScoreTarget(0));
		Assert.Equal(KeyManager.MaxAnonScoreTarget, WalletCoinJoinSettingsViewModel.SliderPositionToAnonScoreTarget(1));

		Assert.Equal(0, WalletCoinJoinSettingsViewModel.AnonScoreTargetToSliderPosition(KeyManager.MinAnonScoreTarget), precision: 9);
		Assert.Equal(1, WalletCoinJoinSettingsViewModel.AnonScoreTargetToSliderPosition(KeyManager.MaxAnonScoreTarget), precision: 9);
	}

	[Fact]
	public void EveryTargetHasAPositionThatGivesItBack()
	{
		var previous = -1.0;

		for (var target = KeyManager.MinAnonScoreTarget; target <= KeyManager.MaxAnonScoreTarget; target++)
		{
			var position = WalletCoinJoinSettingsViewModel.AnonScoreTargetToSliderPosition(target);

			Assert.InRange(position, 0, 1);
			Assert.True(position > previous, $"The position of {target} is not right of the one before.");
			Assert.Equal(target, WalletCoinJoinSettingsViewModel.SliderPositionToAnonScoreTarget(position));

			previous = position;
		}
	}

	[Fact]
	public void PositionsOutsideTheSliderGiveTheLimits()
	{
		Assert.Equal(KeyManager.MinAnonScoreTarget, WalletCoinJoinSettingsViewModel.SliderPositionToAnonScoreTarget(-1));
		Assert.Equal(KeyManager.MaxAnonScoreTarget, WalletCoinJoinSettingsViewModel.SliderPositionToAnonScoreTarget(2));
	}
}
