using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Settings;

[AppLifetime]
[NavigationMetaData(
    Title = "Wallet Settings",
    Caption = "Display wallet settings",
    IconName = "nav_wallet_24_regular",
    Order = 2,
    Category = "Wallet",
    Keywords = new[] { "Wallet", "Settings", },
    NavBarPosition = NavBarPosition.None,
    NavigationTarget = NavigationTarget.DialogScreen,
    Searchable = false)]
public partial class WalletSettingsViewModel : RoutableViewModel
{
    /// <summary>Index of the tab with the coinjoin settings.</summary>
    public const int CoinjoinTab = 0;

    private readonly IWalletModel _wallet;
    [AutoNotify] private int _selectedTab;

    public WalletSettingsViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
    {
        _wallet = walletModel;
        _selectedTab = CoinjoinTab;

        SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);

        NextCommand = ReactiveCommand.Create(() =>
        {
            _wallet.Settings.Save();
            Navigate().Back();
        });

        WalletCoinJoinSettings = new WalletCoinJoinSettingsViewModel(UiContext, walletModel);

        ResyncWalletCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var result = await UiContext.Navigate().To().ResyncWallet(walletModel.GetWalletStats().BirthHeight, walletModel.Settings.MinGapLimit).GetResultAsync();
            if (result is not null)
            {
                walletModel.Settings.RescanWallet(result.StartingHeight, result.MinGapLimit);
                UiContext.Navigate(MetaData.NavigationTarget).Clear();
                AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true);
            }
        });
    }

    public WalletCoinJoinSettingsViewModel WalletCoinJoinSettings { get; private set; }
    public ICommand ResyncWalletCommand { get; }
}
