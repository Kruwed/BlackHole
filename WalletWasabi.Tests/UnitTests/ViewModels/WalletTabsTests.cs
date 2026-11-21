using System.Collections.Generic;
using NBitcoin;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.Fluent.Converters;
using WalletWasabi.Fluent.ViewModels.Wallets.Tabs;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The wallet is a set of tabs that the title bar lists: History, Coins and Order Book. These are the rules of the tabs
/// that can be told without a window: what a tab is, what the Coins tab says about the amounts and the fee rate of a
/// payment, and which coins it gives to a coinjoin round.
/// </summary>
public class WalletTabsTests
{
	[Fact]
	public void TabsHaveTheirTitles()
	{
		Assert.Equal("History", HistoryTabViewModel.TabTitle);
		Assert.Equal("Coins", CoinsTabViewModel.TabTitle);
		Assert.Equal("Order Book", OrderBookTabViewModel.TabTitle);
	}

	[Fact]
	public void ClickOnATabAsksForIt()
	{
		WalletTabViewModel? asked = null;
		var tab = new TestTab("Coins", x => asked = x);

		Assert.Equal("Coins", tab.Title);
		Assert.False(tab.IsSelected);

		tab.SelectCommand.Execute(null);

		Assert.Same(tab, asked);
	}

	[Fact]
	public void SendFormStartsWithTheFeeRateOfTheTitleBar()
	{
		var estimations = new FeeRateEstimations(new Dictionary<int, FeeRate>
		{
			[2] = new(102m),
			[3] = new(20m),
			[6] = new(10m)
		});

		Assert.Equal(new FeeRate(102m), CoinsTabViewModel.GetSuggestedFeeRate(estimations));
		Assert.Equal("102", CoinsTabViewModel.FormatFeeRate(CoinsTabViewModel.GetSuggestedFeeRate(estimations)!));

		// Without an estimate for the next two blocks the form suggests nothing, and neither does the title bar tell a fee rate.
		Assert.Null(CoinsTabViewModel.GetSuggestedFeeRate(new FeeRateEstimations(new Dictionary<int, FeeRate> { [6] = new(10m) })));
		Assert.Null(CoinsTabViewModel.GetSuggestedFeeRate(null));
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("12.5", 12.5)]
	[InlineData(" 144.71 ", 144.71)]
	[InlineData("0.1", 0.1)]
	public void SendFormTakesAFeeRateInSatoshisPerVirtualByte(string text, decimal expected)
	{
		Assert.Equal(new FeeRate(expected), CoinsTabViewModel.ParseFeeRate(text));
	}

	[Theory]
	[InlineData("")]
	[InlineData("abc")]
	[InlineData("-1")]
	[InlineData("0")]
	[InlineData("0.01")]
	[InlineData("1,5")]
	[InlineData("1e3")]
	[InlineData("100000")]
	public void SendFormRefusesWhatIsNoFeeRate(string text)
	{
		Assert.Null(CoinsTabViewModel.ParseFeeRate(text));
	}

	[Fact]
	public void FeeRateIsWrittenWithoutNeedlessDigits()
	{
		Assert.Equal("144.71", CoinsTabViewModel.FormatFeeRate(new FeeRate(144.714m)));
		Assert.Equal("2", CoinsTabViewModel.FormatFeeRate(new FeeRate(2m)));
		Assert.Equal("0.1", CoinsTabViewModel.FormatFeeRate(new FeeRate(0.1m)));
	}

	[Fact]
	public void AmountIsCheckedAgainstTheSelectedCoins()
	{
		// All is well within what the coins hold, and at exactly that too: the payment takes its fee from itself then.
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 0.05m, totalBtc: 0.05m, selectedBtc: 0.1m));
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 0.1m, totalBtc: 0.1m, selectedBtc: 0.1m));

		// More than the coins hold, by this payment alone or by all the payments together.
		Assert.Equal("The selected coins hold less than that.", CoinsTabViewModel.DescribeAmountError(amountBtc: 0.2m, totalBtc: 0.2m, selectedBtc: 0.1m));
		Assert.Equal("The selected coins hold less than that.", CoinsTabViewModel.DescribeAmountError(amountBtc: 0.05m, totalBtc: 0.15m, selectedBtc: 0.1m));
	}

	[Fact]
	public void AmountIsNotHeldAgainstCoinsThatAreNotSelectedYet()
	{
		// The form may be filled in before the coins are picked. It cannot be continued without them.
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 5m, totalBtc: 5m, selectedBtc: 0m));
	}

	[Fact]
	public void AmountOfNothingIsNotAnError()
	{
		// Nothing typed yet, a number that starts with a zero being typed, or what "Max" leaves once the last coin was
		// unticked: no message. The form cannot be continued with it.
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0m, selectedBtc: 1m));
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0m, selectedBtc: 0m));

		// The other payments take more than the coins hold, and "Max" leaves nothing for this one: their rows say so, this one does not.
		Assert.Null(CoinsTabViewModel.DescribeAmountError(amountBtc: 0m, totalBtc: 0.5m, selectedBtc: 0.1m));
	}

	[Fact]
	public void AmountCannotBeMoreThanThereIs()
	{
		Assert.Equal("Amount must be less than the total supply of BTC.", CoinsTabViewModel.DescribeAmountError(amountBtc: 21_000_001m, totalBtc: 21_000_001m, selectedBtc: 1m));
	}

	[Fact]
	public void FeeIsSaidToComeOutOfAPaymentThatTakesAll()
	{
		Assert.True(CoinsTabViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0.1m, hasAmountMessage: false));

		// The amount was typed: the fee is paid on top of it.
		Assert.False(CoinsTabViewModel.ShowsFeeSubtractionNote(takesAll: false, amountBtc: 0.1m, hasAmountMessage: false));

		// "Max" with the last coin unticked: there is no payment to take a fee from.
		Assert.False(CoinsTabViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0m, hasAmountMessage: false));
		Assert.False(CoinsTabViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: null, hasAmountMessage: false));

		// A message about the amount stands in the same place and goes first.
		Assert.False(CoinsTabViewModel.ShowsFeeSubtractionNote(takesAll: true, amountBtc: 0.1m, hasAmountMessage: true));
	}

	[Fact]
	public void HintTellsWhatToDoBeforeAnAmountCanBeSent()
	{
		Assert.Equal("Select the coins to spend below", CoinsTabViewModel.DescribeSelectionHint(hasCoins: true));
		Assert.Equal("The wallet has no coins to spend", CoinsTabViewModel.DescribeSelectionHint(hasCoins: false));
	}

	[Fact]
	public void MarketMakersAreListedWithWhatTheyOffer()
	{
		Assert.Equal(
			["Market Maker", "Fee Rate", "Spread", "Bid", "Ask", "Liquidity", "Bond"],
			OrderBookTabViewModel.Columns);
	}

	/// <summary>
	/// Play tells the wallet about every coin of the list, the unticked ones too: a coin that was chosen for an earlier
	/// round and is not ticked now has to be taken out again.
	/// </summary>
	[Fact]
	public void PlayNamesEveryCoinOfTheListWithItsTick()
	{
		var ticked = new OutPoint(uint256.One, 0);
		var unticked = new OutPoint(uint256.One, 1);

		var selection = CoinsTabViewModel.GetRoundSelection([(ticked, true), (unticked, false)]);

		Assert.Equal(2, selection.Count);
		Assert.True(selection[ticked]);
		Assert.False(selection[unticked]);

		Assert.Empty(CoinsTabViewModel.GetRoundSelection([]));
	}

	[Theory]
	[InlineData(500, "200", 300)]
	[InlineData(200, "200", 0)]
	[InlineData(120, "200", 0)]
	[InlineData(500, null, 500)]
	[InlineData(500, "many", 500)]
	public void LayoutLeavesRoomByTakingANumberOff(double value, string? parameter, double expected)
	{
		Assert.Equal(expected, DoubleConverters.Subtract(value, parameter));
	}

	private class TestTab(string title, Action<WalletTabViewModel> select) : WalletTabViewModel(null!, title, select);
}
