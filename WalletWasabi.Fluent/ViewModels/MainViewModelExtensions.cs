using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.OpenDirectory;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Fluent.ViewModels.TransactionBroadcasting;

namespace WalletWasabi.Fluent.ViewModels;

public static class MainViewModelExtensions
{
	public static void RegisterAllViewModels(this MainViewModel mainViewModel, UiContext uiContext)
	{
		AddWalletPageViewModel.RegisterLazy(() => new AddWalletPageViewModel(uiContext));
		SettingsPageViewModel.Register(mainViewModel.SettingsPage);

		ConnectionsSettingsTabViewModel.RegisterLazy(() =>
		{
			mainViewModel.SettingsPage.SelectedTab = 0;
			return mainViewModel.SettingsPage;
		});

		CoordinatorTabSettingsViewModel.RegisterLazy(() =>
		{
			mainViewModel.SettingsPage.SelectedTab = 1;
			return mainViewModel.SettingsPage;
		});

		BroadcasterViewModel.RegisterLazy(() => new BroadcasterViewModel(uiContext));
		OpenDataFolderViewModel.RegisterLazy(() => new OpenDataFolderViewModel(uiContext));
		OpenWalletsFolderViewModel.RegisterLazy(() => new OpenWalletsFolderViewModel(uiContext));
		OpenLogsViewModel.RegisterLazy(() => new OpenLogsViewModel(uiContext));
		OpenTorLogsViewModel.RegisterLazy(() => new OpenTorLogsViewModel(uiContext));
		OpenConfigFileViewModel.RegisterLazy(() => new OpenConfigFileViewModel(uiContext));
	}
}
