using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.ViewModels.Settings;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The dust forfeit limit is typed on the Coordinator tab of the settings, as an amount in BTC. Only an amount that a
/// wallet can have as its limit is taken.
/// </summary>
public class DustForfeitLimitTests
{
	[Theory]
	[InlineData("0", 0)]
	[InlineData("0.00000001", 1)]
	[InlineData("0.00005", 5_000)]
	[InlineData("0.00005000", 5_000)]
	[InlineData("0.0001", 10_000)]
	public void AmountUpToTheHighestLimitIsTaken(string text, long satoshis)
	{
		Assert.True(CoordinatorTabSettingsViewModel.TryParseMaxCoinjoinLeftover(text, out var limit));
		Assert.Equal(Money.Satoshis(satoshis), limit);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("abc")]
	[InlineData("-0.00001")]
	[InlineData("0.00010001")]
	[InlineData("1")]
	public void AnythingElseIsRefused(string? text)
	{
		Assert.False(CoordinatorTabSettingsViewModel.TryParseMaxCoinjoinLeftover(text, out var limit));
		Assert.Null(limit);
	}

	[Fact]
	public void HighestLimitIsTheOneOfTheWallet()
	{
		var highest = KeyManager.HighestMaxCoinjoinLeftover;

		Assert.True(CoordinatorTabSettingsViewModel.TryParseMaxCoinjoinLeftover(highest.ToString(), out var limit));
		Assert.Equal(highest, limit);
		Assert.False(CoordinatorTabSettingsViewModel.TryParseMaxCoinjoinLeftover((highest + Money.Satoshis(1)).ToString(), out _));
	}
}
