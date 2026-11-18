using System.Linq;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the coinjoin controls say about the end of a coinjoin. A coinjoin is one round that the user started: when it is
/// over the coinjoin is stopped, so no text may tell that something goes on by itself.
/// </summary>
public class CoinJoinStateTests
{
	[Fact]
	public void EndOfRoundIsToldAsItWas()
	{
		Assert.Equal("Coinjoin successful", CoinJoinStateViewModel.DescribeRoundEnd(EndRoundState.TransactionBroadcasted));
		Assert.Equal("Insufficient participants", CoinJoinStateViewModel.DescribeRoundEnd(EndRoundState.AbortedNotEnoughAlices));

		// Every other end is a round without a coinjoin.
		foreach (var endRoundState in Enum.GetValues<EndRoundState>().Except([EndRoundState.TransactionBroadcasted, EndRoundState.AbortedNotEnoughAlices]))
		{
			Assert.Equal("Round ended without a coinjoin", CoinJoinStateViewModel.DescribeRoundEnd(endRoundState));
		}
	}

	[Fact]
	public void CoinJoinThatDidNotTakePlaceIsToldWhy()
	{
		Assert.Equal("No round was found", CoinJoinStateViewModel.DescribeError(CoinjoinError.NoRoundAvailable));
		Assert.Equal("The funds are not confirmed yet", CoinJoinStateViewModel.DescribeError(CoinjoinError.NoConfirmedCoinsEligibleToMix));
		Assert.Equal("The send dialog is open", CoinJoinStateViewModel.DescribeError(CoinjoinError.UserInSendWorkflow));
		Assert.Equal("Insufficient funds eligible for coinjoin", CoinJoinStateViewModel.DescribeError(CoinjoinError.NoCoinsEligibleToMix));
		Assert.Equal("Coordinator lied and might be malicious!", CoinJoinStateViewModel.DescribeError(CoinjoinError.CoordinatorLiedAboutInputs));

		// An error that the controls do not know by name still gets a text.
		Assert.Equal("Coinjoin did not take place", CoinJoinStateViewModel.DescribeError((CoinjoinError)int.MaxValue));
	}

	[Fact]
	public void NoTextPromisesThatTheCoinJoinGoesOn()
	{
		var texts = Enum.GetValues<EndRoundState>().Select(CoinJoinStateViewModel.DescribeRoundEnd)
			.Concat(Enum.GetValues<CoinjoinError>().Select(CoinJoinStateViewModel.DescribeError))
			.Distinct()
			.ToArray();

		Assert.All(texts, text =>
		{
			Assert.False(string.IsNullOrWhiteSpace(text));
			Assert.DoesNotContain("retry", text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("continu", text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("await", text, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("next round", text, StringComparison.OrdinalIgnoreCase);
		});
	}
}
