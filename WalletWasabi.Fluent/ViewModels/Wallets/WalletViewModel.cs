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

		CoinJoinSettings = new WalletCoinJoinSettingsViewModel(UiContext, WalletModel);
		History = new HistoryViewModel(UiContext, WalletModel);

		walletModel.HasBalance
			.Select(x => !x)
			.BindTo(this, x => x.IsWalletBalanceZero);

		walletModel.IsCoinjoinRunning
			.BindTo(this, x => x.IsCoinJoining);

		// There is nothing to send from an empty wallet.
		this.WhenAnyValue(x => x.IsWalletBalanceZero)
			.Subscribe(isWalletBalanceZero => IsSendButtonVisible = !isWalletBalanceZero);

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

		ReceiveCommand = ReactiveCommand.Create(() => Navigate().To().Receive(WalletModel));

		WalletInfoCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (await AuthorizeForPasswordAsync())
			{
				Navigate().To().WalletInfo(WalletModel);
			}
		});

		WalletStatsCommand = ReactiveCommand.Create(() => Navigate().To().WalletStats(WalletModel));

		CoinJoinSettingsCommand = ReactiveCommand.Create(() => Navigate(NavigationTarget.DialogScreen).To(CoinJoinSettings));

		WalletCoinsCommand = ReactiveCommand.Create(() => Navigate(NavigationTarget.DialogScreen).To().WalletCoins(WalletModel));

		CoinJoinStateViewModel = WalletModel.IsCoinJoinEnabled
			? new CoinJoinStateViewModel(uiContext, WalletModel, Wallet, WalletModel.Coinjoin!, CoinJoinSettings)
			: null;

		NavigateToCoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});

		Tiles = GetTiles().ToList();
	}

	// TODO: Remove this
	public Wallet Wallet { get; }

	public IWalletModel WalletModel { get; }

	public bool IsLoggedIn => WalletModel.Auth.IsLoggedIn;

	public IObservable<bool> IsMusicBoxVisible { get; }

	public CoinJoinStateViewModel? CoinJoinStateViewModel { get; private set; }

	public WalletCoinJoinSettingsViewModel CoinJoinSettings { get; private set; }

	public HistoryViewModel History { get; }

	public IEnumerable<ActivatableViewModel> Tiles { get; }

	public ICommand SendCommand { get; private set; }

	public ICommand ReceiveCommand { get; }

	public ICommand WalletInfoCommand { get; private set; }

	public ICommand CoinJoinSettingsCommand { get; private set; }

	public ICommand WalletStatsCommand { get; private set; }

	public ICommand WalletCoinsCommand { get; private set; }

	public ICommand NavigateToCoordinatorSettingsCommand { get; }

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
