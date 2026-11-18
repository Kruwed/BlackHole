using NBitcoin;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// A dialog lies over the title bar and takes it out of use. The welcome and the pages that follow it until there is a wallet are the exception.
/// The title bar tells the balance of the wallet and how far the wallet is with the blocks of the node.
/// </summary>
public class TitleBarTests
{
	[Fact]
	public void TitleBarIsFreeWithoutADialog()
	{
		Assert.True(MainViewModel.IsTitleBarFree(isDialogOpen: false, isFullScreenOpen: false, isCompactDialogOpen: false, isWelcome: false));
		Assert.True(MainViewModel.IsTitleBarFree(isDialogOpen: false, isFullScreenOpen: false, isCompactDialogOpen: false, isWelcome: true));
	}

	[Theory]
	[InlineData(true, false, false)]
	[InlineData(false, true, false)]
	[InlineData(false, false, true)]
	[InlineData(true, false, true)]
	public void AnyDialogCoversTheTitleBarOnceThereIsAWallet(bool isDialogOpen, bool isFullScreenOpen, bool isCompactDialogOpen)
	{
		Assert.False(MainViewModel.IsTitleBarFree(isDialogOpen, isFullScreenOpen, isCompactDialogOpen, isWelcome: false));
	}

	[Fact]
	public void PagesOfTheWelcomeLeaveTheTitleBarFree()
	{
		Assert.True(MainViewModel.IsTitleBarFree(isDialogOpen: true, isFullScreenOpen: false, isCompactDialogOpen: false, isWelcome: true));
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void DialogOnTopOfAPageOfTheWelcomeTakesTheTitleBarOutOfUse(bool isFullScreenOpen, bool isCompactDialogOpen)
	{
		Assert.False(MainViewModel.IsTitleBarFree(isDialogOpen: true, isFullScreenOpen, isCompactDialogOpen, isWelcome: true));
	}

	[Fact]
	public void SyncProgressTellsTheBlocksAndTheSearchForFidelityBonds()
	{
		var node = new SyncProgressCardModel(0, 976_172, 976_172);

		Assert.Equal(
			"120/976,172 blocks synced",
			MainViewModel.DescribeSyncProgress(new WalletLoadProgress(new WalletSyncStatus.Scanning(120, 976_172), node, new SyncProgressCardModel(0, 120, 976_172))));
		Assert.Equal(
			"976,172 blocks synced",
			MainViewModel.DescribeSyncProgress(new WalletLoadProgress(new WalletSyncStatus.Synchronized(976_172), node, new SyncProgressCardModel(0, 976_172, 976_172))));
		Assert.Equal(
			"Waiting for Bitcoin node",
			MainViewModel.DescribeSyncProgress(new WalletLoadProgress(new WalletSyncStatus.NotStarted(), node, new SyncProgressCardModel(0, 0, null))));

		// A wallet that goes through the chain again calculates the addresses that its bonds can have first. No block is
		// processed for about a minute, and the title bar says why.
		var preparing = new WalletSyncStatus.PreparingBondSearch(792_141);
		Assert.Equal(
			"Preparing the search for bonds",
			MainViewModel.DescribeSyncProgress(new WalletLoadProgress(preparing, node, new SyncProgressCardModel(0, 0, 976_172))));
		Assert.Contains("792,141 unlock heights", preparing.ToUserFriendlyString());
	}

	[Fact]
	public void BalanceIsToldAsOneAmountOfBitcoin()
	{
		Assert.Equal("0.00 000 000 BTC", MainViewModel.DescribeBalance(Amount.Zero));
		Assert.Equal("0.00 000 546 BTC", MainViewModel.DescribeBalance(new Amount(Money.Satoshis(546))));
		Assert.Equal("0.29 982 056 BTC", MainViewModel.DescribeBalance(new Amount(Money.Satoshis(29_982_056))));
		Assert.Equal("1 234.50 000 000 BTC", MainViewModel.DescribeBalance(new Amount(Money.Coins(1234.5m))));
	}
}
