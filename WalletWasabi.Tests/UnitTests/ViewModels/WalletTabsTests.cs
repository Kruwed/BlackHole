using WalletWasabi.Fluent.ViewModels.Wallets.Tabs;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The wallet is a set of tabs that the title bar lists: History, Coins and Order Book. These are the rules of the tabs
/// that can be told without a window: what a tab is, and what the Order Book lists. What is done with the coins that
/// are ticked on the Coins tab is in the Coinjoin dialog, see <see cref="CoinJoinDialogTests"/>.
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
	public void MarketMakersAreListedWithWhatTheyOffer()
	{
		Assert.Equal(
			["Market Maker", "Fee Rate", "Spread", "Bid", "Ask", "Liquidity", "Bond"],
			OrderBookTabViewModel.Columns);
	}

	private class TestTab(string title, Action<WalletTabViewModel> select) : WalletTabViewModel(null!, title, select);
}
