using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Settings;

[AppLifetime]
[NavigationMetaData(Title = "Wallet Settings", NavigationTarget = NavigationTarget.DialogScreen)]
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
    }

    public WalletCoinJoinSettingsViewModel WalletCoinJoinSettings { get; private set; }
}
