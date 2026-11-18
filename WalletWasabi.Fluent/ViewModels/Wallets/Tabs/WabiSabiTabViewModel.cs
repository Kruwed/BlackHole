using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.Tiles;
using WalletWasabi.Fluent.ViewModels.Wallets.Settings;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the WabiSabi coinjoins: how private the coins of the wallet are by now, the coinjoin that is running or
/// waiting with its controls, and the ways to its settings. Nothing of this shows anywhere else.
/// </summary>
[AppLifetime]
public partial class WabiSabiTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "WabiSabi";

	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinStateViewModel? _coinJoinState;

	public WabiSabiTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		WalletModel = walletModel;

		PrivacyProgress = new PrivacyControlTileViewModel(uiContext, walletModel);
		CoinJoinSettings = new WalletCoinJoinSettingsViewModel(uiContext, walletModel);

		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// The coinjoin controls come with the loaded wallet: they start coinjoins on their own, and what the wallet
		// knows about its coins is not complete until it has processed the blocks of the node.
		this.WhenAnyValue(x => x.IsWalletLoaded)
			.Where(isLoaded => isLoaded && CoinJoinState is null && walletModel.IsCoinJoinEnabled)
			.Subscribe(_ => CoinJoinState = new CoinJoinStateViewModel(uiContext, walletModel, wallet, walletModel.Coinjoin!));

		// The settings only decide how the wallet coinjoins later, so they are open while the wallet still processes blocks.
		CoinJoinSettingsCommand = ReactiveCommand.Create(() => UiContext.Navigate(NavigationTarget.DialogScreen).To(CoinJoinSettings));

		CoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});
	}

	public IWalletModel WalletModel { get; }

	/// <summary>Whether a coordinator is set. Without one there is no coinjoin, and the tab says so.</summary>
	public bool IsCoinJoinEnabled => WalletModel.IsCoinJoinEnabled;

	/// <summary>The share of the coins that reached the anonymity score target.</summary>
	public PrivacyControlTileViewModel PrivacyProgress { get; }

	public WalletCoinJoinSettingsViewModel CoinJoinSettings { get; }

	public ICommand CoinJoinSettingsCommand { get; }

	public ICommand CoordinatorSettingsCommand { get; }

	protected override void OnActivated(CompositeDisposable disposables)
	{
		base.OnActivated(disposables);

		PrivacyProgress.Activate(disposables);
	}
}
