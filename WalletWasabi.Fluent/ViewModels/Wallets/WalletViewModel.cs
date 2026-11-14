using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.SearchBar.SearchItems;
using WalletWasabi.Fluent.ViewModels.SearchBar.Sources;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.Tiles;
using WalletWasabi.Fluent.ViewModels.Wallets.Settings;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

[AppLifetime]
public partial class WalletViewModel : RoutableViewModel, IWalletViewModel
{
	[AutoNotify(SetterModifier = AccessModifier.Protected)] private bool _isCoinJoining;

	[AutoNotify(SetterModifier = AccessModifier.Protected)] private bool _isLoading;
	[AutoNotify] private bool _isPointerOver;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSendButtonVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isDonateButtonVisible;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletBalanceZero;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _areAllCoinsPrivate;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasMusicBoxBeenDisplayed;
	[AutoNotify] private bool _isMusicBoxFlyoutDisplayed;

	// This proxy fixes a stack overflow bug in Avalonia
	public bool IsMusicBoxFlyoutOpenedProxy
	{
		get => IsMusicBoxFlyoutDisplayed;
		set => IsMusicBoxFlyoutDisplayed = value;
	}

	private string _title = UiConstants.WalletTitle;
	[AutoNotify(SetterModifier = AccessModifier.Protected)] private bool _loaded;

	public WalletViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet) : base(uiContext)
	{
		WalletModel = walletModel;
		Wallet = wallet;

		Settings = new WalletSettingsViewModel(UiContext, WalletModel);
		History = new HistoryViewModel(UiContext, WalletModel);

		UiContext.EditableSearchSource.Add(CreateSearchItems());

		var sendSearchItem = CreateSendItem();
		this.WhenAnyValue(x => x.IsSendButtonVisible)
			.Do(shouldAdd => UiContext.EditableSearchSource.Toggle(sendSearchItem, shouldAdd))
			.Subscribe();

		var donateSearchItem = CreateDonateItem();
		this.WhenAnyValue(x => x.IsDonateButtonVisible)
			.Do(shouldAdd => UiContext.EditableSearchSource.Toggle(donateSearchItem, shouldAdd))
			.Subscribe();

		walletModel.HasBalance
			.Select(x => !x)
			.BindTo(this, x => x.IsWalletBalanceZero);

		walletModel.IsCoinjoinRunning
			.BindTo(this, x => x.IsCoinJoining);

		 // Keep the send button visible while the discreet mode is on. Otherwise its absence reveals an empty wallet.
		 this.WhenAnyValue(x => x.IsWalletBalanceZero, x => x.UiContext.ApplicationSettings.PrivacyMode)
		 	.Subscribe(_ => IsSendButtonVisible = !IsWalletBalanceZero || UiContext.ApplicationSettings.PrivacyMode);

		 this.WhenAnyValue(x => x.IsSendButtonVisible)
			 .Subscribe(_ => IsDonateButtonVisible = IsSendButtonVisible && WalletModel.Network == Network.Main);

		 WalletModel.Privacy.IsWalletPrivate
			 .BindTo(this, x => x.AreAllCoinsPrivate);

		 IsMusicBoxVisible = this.WhenAnyValue(
			 x => x.HasMusicBoxBeenDisplayed,
			 x => x.IsWalletBalanceZero,
			 x => x.AreAllCoinsPrivate,
			 x => x.IsPointerOver,
			 x => x.IsMusicBoxFlyoutDisplayed,
			 (hasBeenDisplayed, hasNoBalance, areAllCoinsPrivate, isPointerOver, isMusicBoxFlyoutDisplayed) =>
			 {
				 if (!hasBeenDisplayed)
				 {
					 if (!WalletModel.IsCoinJoinEnabled)
					 {
						 // If there is no coordinator configured and it's the first time, display MusicBox even without pointer over
						 Task.Run(() => DelaySwitchHasMusicBoxBeenDisplayedAsync(CancellationToken.None));
						 return true;
					 }

					 HasMusicBoxBeenDisplayed = true;
				 }

				 if (!WalletModel.IsCoinJoinEnabled)
				 {
					 return isPointerOver || isMusicBoxFlyoutDisplayed;
				 }

				 return !hasNoBalance;
			 });


		// Sending always starts with the user picking the coins to spend.
		SendCommand = ReactiveCommand.Create(() => Navigate().To().ManualControlDialog(walletModel, wallet));
		DonateCommand = ReactiveCommand.Create(() => Navigate().To().ManualControlDialog(walletModel, wallet, donate: true));

		ReceiveCommand = ReactiveCommand.Create(() => Navigate().To().Receive(WalletModel));

		WalletInfoCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (await AuthorizeForPasswordAsync())
			{
				Navigate().To().WalletInfo(WalletModel);
			}
		});

		WalletStatsCommand = ReactiveCommand.Create(() => Navigate().To().WalletStats(WalletModel));

		WalletSettingsCommand = ReactiveCommand.Create(
			() =>
			{
				Settings.SelectedTab = WalletSettingsViewModel.CoinjoinTab;
				Navigate(NavigationTarget.DialogScreen).To(Settings);
			});

		CoinJoinSettingsCommand = ReactiveCommand.Create(
			() =>
			{
				Settings.SelectedTab = WalletSettingsViewModel.CoinjoinTab;
				Navigate(NavigationTarget.DialogScreen).To(Settings);
			});

		WalletCoinsCommand = ReactiveCommand.Create(() => Navigate(NavigationTarget.DialogScreen).To().WalletCoins(WalletModel));

		CoinJoinStateViewModel = WalletModel.IsCoinJoinEnabled
			? new CoinJoinStateViewModel(uiContext, WalletModel, Wallet, WalletModel.Coinjoin!, Settings)
			: null;

		CoinJoinPaymentsCommand = ReactiveCommand.Create(() => Navigate(NavigationTarget.DialogScreen).To().CoinJoinPayments(WalletModel, Wallet));

		if (WalletModel.IsCoinJoinEnabled)
		{
			UiContext.EditableSearchSource.Add(CreateCoinJoinPaymentsItem());
		}

		NavigateToCoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});

		NavigateToExcludedCoinsCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().ExcludedCoins(WalletModel));

		Tiles = GetTiles().ToList();
	}

	// TODO: Remove this
	public Wallet Wallet { get; }

	public IWalletModel WalletModel { get; }

	public bool IsLoggedIn => WalletModel.Auth.IsLoggedIn;

	public IObservable<bool> IsMusicBoxVisible { get; }

	public CoinJoinStateViewModel? CoinJoinStateViewModel { get; private set; }

	public WalletSettingsViewModel Settings { get; private set; }

	public HistoryViewModel History { get; }

	public IEnumerable<ActivatableViewModel> Tiles { get; }

	public ICommand SendCommand { get; private set; }
	public ICommand DonateCommand { get; private set; }

	public ICommand ReceiveCommand { get; }

	public ICommand WalletInfoCommand { get; private set; }

	public ICommand WalletSettingsCommand { get; private set; }

	public ICommand WalletStatsCommand { get; private set; }

	public ICommand WalletCoinsCommand { get; private set; }

	public ICommand CoinJoinSettingsCommand { get; private set; }

	public ICommand CoinJoinPaymentsCommand { get; private set; }

	public ICommand NavigateToCoordinatorSettingsCommand { get; }

	public ICommand NavigateToExcludedCoinsCommand { get; }

	public override string Title
	{
		get => _title;
		protected set => this.RaiseAndSetIfChanged(ref _title, value);
	}

	public void SelectTransaction(uint256 txid)
	{
		RxApp.MainThreadScheduler.Schedule(async () =>
		{
			await Task.Delay(500);
			History.SelectTransaction(txid);
		});
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		History.Activate(disposables);

		foreach (var tile in Tiles)
		{
			tile.Activate(disposables);
		}

		WalletModel.Loaded
			.BindTo(this, x => x.Loaded)
			.DisposeWith(disposables);
	}

	private ISearchItem[] CreateSearchItems()
	{
		return new ISearchItem[]
		{
			new ActionableItem("Receive", "Display wallet receive dialog", () => { ReceiveCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Receive", "Action", }) { Icon = "wallet_action_receive", IsDefault = true, Priority = 2 },
			new ActionableItem("Coinjoin Settings", "Display wallet coinjoin settings", () => { CoinJoinSettingsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Settings", }) { Icon = "wallet_action_coinjoin", IsDefault = true, Priority = 3 },
			new ActionableItem("Wallet Settings", "Display wallet settings", () => { WalletSettingsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Settings", }) { Icon = "settings_wallet_regular", IsDefault = true, Priority = 4 },
			new ActionableItem("Exclude Coins", "Display exclude coins", () => { NavigateToExcludedCoinsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Exclude", "Coins", "Coinjoin", "Freeze", "UTXO", }) { Icon = "exclude_coins", IsDefault = true, Priority = 5 },
			new ActionableItem("Wallet Coins", "Display wallet coins", () => { WalletCoinsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Coins", "UTXO", }) { Icon = "wallet_coins", IsDefault = true, Priority = 6 },
			new ActionableItem("Wallet Stats", "Display wallet stats", () => { WalletStatsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Stats", }) { Icon = "stats_wallet_regular", IsDefault = true, Priority = 7 },
			new ActionableItem("Wallet Info", "Display wallet info", () => { WalletInfoCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Info", }) { Icon = "info_regular", IsDefault = true, Priority = 8 },
		};
	}

	private ISearchItem CreateSendItem()
	{
		return new ActionableItem("Send", "Display wallet send dialog", () => { SendCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Send", "Action", }) { Icon = "wallet_action_send", IsDefault = true, Priority = 1 };
	}

	private ISearchItem CreateDonateItem()
	{
		return new ActionableItem("Donate", "Donate to The Wasabi Wallet Developers", () => { DonateCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Send", "Action", "Donate" }) { Icon = "gift", IsDefault = true, Priority = 4 };
	}

	private ISearchItem CreateCoinJoinPaymentsItem()
	{
		return new ActionableItem("Coinjoin Payments", "Manage queued coinjoin payments", () => { CoinJoinPaymentsCommand.ExecuteIfCan(); return Task.CompletedTask; }, "Wallet", new[] { "Wallet", "Coinjoin", "Payments", "Send", "Batch" }) { Icon = "embedded_payment", IsDefault = true, Priority = 3 };
	}

	private IEnumerable<ActivatableViewModel> GetTiles()
	{
		yield return new WalletBalanceTileViewModel(UiContext, WalletModel.Balances);

		yield return new PrivacyControlTileViewModel(UiContext, WalletModel);

	}

	private async Task<bool> AuthorizeForPasswordAsync()
	{
		if (WalletModel.Auth.HasPassword)
		{
			return await Navigate().To().PasswordAuthDialog(WalletModel).GetResultAsync();
		}

		return true;
	}

	private async Task DelaySwitchHasMusicBoxBeenDisplayedAsync(CancellationToken cancellationToken)
	{
		await Task.Delay(10000, cancellationToken);
		HasMusicBoxBeenDisplayed = true;
	}
}
