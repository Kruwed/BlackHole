using WalletWasabi.Fluent.ViewModels;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// A dialog lies over the title bar and takes it out of use. The welcome and the pages that follow it until there is a wallet are the exception.
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
}
