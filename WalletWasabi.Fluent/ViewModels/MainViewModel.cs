using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Fluent.ViewModels.Dialogs.ReleaseHighlights;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Notifications;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.ViewModels;

[AppLifetime]
public partial class MainViewModel : ViewModelBase
{
	/// <summary>What the title bar tells in place of a block count while the node does not answer.</summary>
	private const string WaitingForNode = "Waiting for Bitcoin node";

	private bool _isClosing;

	/// <summary>How far the user is with the welcome. The welcome is shown anew when a dialog on top of it closed every page, and goes on where it was.</summary>
	private readonly WelcomeProgress _welcomeProgress = new();

	[AutoNotify] private string _title = Constants.AppName;
	[AutoNotify] private WindowState _windowState;
	[AutoNotify] private bool _isOobeBackgroundVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSyncProgressVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _syncProgress = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _syncStatus = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _feeRate = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _balance = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _chainHeight = WaitingForNode;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isChainHeightVisible;

	public MainViewModel(UiContext uiContext) : base(uiContext)
	{
		UiContext.SetMainViewModel(this);

		ApplyUiConfigWindowState();

		DialogScreen = new DialogScreenViewModel(UiContext);
		FullScreen = new DialogScreenViewModel(UiContext, NavigationTarget.FullScreen);
		CompactDialogScreen = new DialogScreenViewModel(UiContext, NavigationTarget.CompactDialogScreen);
		MainScreen = new TargettedNavigationStack(UiContext, NavigationTarget.HomeScreen);
		UiContext.RegisterNavigation(new NavigationState(UiContext, MainScreen, DialogScreen, FullScreen, CompactDialogScreen));

		ShowWalletWhenItExists();

		ShowFeeRate();
		ShowChainHeight();

		SettingsPage = new SettingsPageViewModel(UiContext);
		OpenDataFolderCommand = ReactiveCommand.CreateFromTask(OpenDataFolderAsync);
		Notifications = new WalletNotificationsViewModel(UiContext, () => WalletPage?.WalletViewModel);

		this.WhenAnyValue(x => x.WindowState)
			.Where(state => state != WindowState.Minimized)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(state => UiContext.ApplicationSettings.WindowState = state);

		IsMainContentEnabled =
			this.WhenAnyValue(
					x => x.DialogScreen.IsDialogOpen,
					x => x.FullScreen.IsDialogOpen,
					x => x.CompactDialogScreen.IsDialogOpen,
					(dialogIsOpen, fullScreenIsOpen, compactIsOpen) => !(dialogIsOpen || fullScreenIsOpen || compactIsOpen))
				.ObserveOn(RxApp.MainThreadScheduler);

		// The title bar is not to be used either while the page of a dialog is busy, creating the wallet for example.
		var isDialogPageBusy =
			this.WhenAnyValue(x => x.DialogScreen.CurrentPage)
				.Select(page => page?.WhenAnyValue(x => x.IsBusy) ?? Observable.Return(false))
				.Switch();

		IsTitleBarEnabled =
			this.WhenAnyValue(
					x => x.DialogScreen.IsDialogOpen,
					x => x.FullScreen.IsDialogOpen,
					x => x.CompactDialogScreen.IsDialogOpen,
					x => x.IsOobeBackgroundVisible,
					IsTitleBarFree)
				.CombineLatest(isDialogPageBusy, (isFree, isBusy) => isFree && !isBusy)
				.ObserveOn(RxApp.MainThreadScheduler);

		CurrentWallet =
			this.WhenAnyValue(x => x.MainScreen.CurrentPage)
				.WhereNotNull()
				.OfType<WalletViewModel>();

		IsOobeBackgroundVisible = UiContext.ApplicationSettings.Oobe;
		var isFirstLaunch = !UiContext.WalletRepository.HasWallet || UiContext.ApplicationSettings.Oobe;

		RxApp.MainThreadScheduler.Schedule(async () =>
		{
			if (isFirstLaunch)
			{
				IsOobeBackgroundVisible = true;

				// A wallet that is there already needs no welcome.
				while (!UiContext.WalletRepository.HasWallet)
				{
					await UiContext.Navigate().To().WelcomePage(_welcomeProgress).GetResultAsync();

					if (UiContext.WalletRepository.HasWallet || _isClosing)
					{
						break;
					}

					// The welcome was closed without a wallet: a dialog on top of it was dismissed, the settings for example, and that closes every page.
					// It comes back, as there would be no way to a wallet otherwise. The pages are still being closed at this point, so it waits a moment.
					await Task.Yield();
				}

				if (UiContext.WalletRepository.HasWallet)
				{
					UiContext.ApplicationSettings.Oobe = false;
					IsOobeBackgroundVisible = false;
				}
			}

			await Task.Delay(1000);

			var lastVersionHighlightsDisplayed = UiContext.ApplicationSettings.LastVersionHighlightsDisplayed;
			UiContext.ApplicationSettings.LastVersionHighlightsDisplayed = Constants.ClientVersion;
			if (!isFirstLaunch && Constants.ClientVersion > lastVersionHighlightsDisplayed)
			{
				await uiContext.Navigate().NavigateDialogAsync(new ReleaseHighlightsDialogViewModel(UiContext),
					navigationMode: NavigationMode.Clear);
			}
		});

		NetworkBadgeName =
			UiContext.ApplicationSettings.Network == Network.Main
			? ""
			: UiContext.ApplicationSettings.Network.Name;
	}

	public IObservable<bool> IsMainContentEnabled { get; }

	/// <summary>Whether the title bar reacts to the pointer: it does not under a dialog. See <see cref="IsTitleBarFree"/>.</summary>
	public IObservable<bool> IsTitleBarEnabled { get; }

	public string NetworkBadgeName { get; }

	public IObservable<WalletViewModel> CurrentWallet { get; }

	public TargettedNavigationStack MainScreen { get; }

	public DialogScreenViewModel DialogScreen { get; }
	public DialogScreenViewModel FullScreen { get; }
	public DialogScreenViewModel CompactDialogScreen { get; }

	/// <summary>The wallet, once it was created or recovered.</summary>
	public WalletPageViewModel? WalletPage { get; private set; }

	public SettingsPageViewModel SettingsPage { get; }

	/// <summary>Shows the data folder in the file manager. This is what the folder button in the title bar does.</summary>
	public ICommand OpenDataFolderCommand { get; }

	public WalletNotificationsViewModel Notifications { get; }

	/// <summary>
	/// Whether no dialog lies over the title bar. Every dialog does, except for the welcome and the pages that follow it until there is a wallet:
	/// they start below the title bar, so that the data folder and the settings can be reached from there.
	/// A small dialog on top of such a page takes the title bar out of use like any other dialog.
	/// </summary>
	/// <param name="isWelcome">Whether the welcome shows, which it does as long as there is no wallet. Its pages are those of the dialog screen.</param>
	public static bool IsTitleBarFree(bool isDialogOpen, bool isFullScreenOpen, bool isCompactDialogOpen, bool isWelcome) =>
		!(isFullScreenOpen || isCompactDialogOpen || (isDialogOpen && !isWelcome));

	public bool IsDialogOpen()
	{
		return DialogScreen.IsDialogOpen
			   || FullScreen.IsDialogOpen
			   || CompactDialogScreen.IsDialogOpen;
	}

	public void ShowDialogAlert()
	{
		if (CompactDialogScreen.IsDialogOpen)
		{
			CompactDialogScreen.ShowAlert = false;
			CompactDialogScreen.ShowAlert = true;
			return;
		}

		if (DialogScreen.IsDialogOpen)
		{
			DialogScreen.ShowAlert = false;
			DialogScreen.ShowAlert = true;
			return;
		}

		if (FullScreen.IsDialogOpen)
		{
			FullScreen.ShowAlert = false;
			FullScreen.ShowAlert = true;
		}
	}

	/// <summary>Closes every page. The application does that when it exits.</summary>
	public void ClearStacks()
	{
		// Nothing is to be shown any more, the welcome neither.
		_isClosing = true;

		MainScreen.Clear();
		DialogScreen.Clear();
		FullScreen.Clear();
		CompactDialogScreen.Clear();
	}

	public void Initialize()
	{
		Notifications.StartListening();

		if (UiContext.ApplicationSettings.Network != Network.Main)
		{
			Title += $" - {UiContext.ApplicationSettings.Network}";
		}
	}

	public void ApplyUiConfigWindowState()
	{
		WindowState = UiContext.ApplicationSettings.WindowState;
	}

	/// <summary>
	/// Shows the wallet on the home screen: right away if there is one, otherwise as soon as it is created or recovered.
	/// </summary>
	private void ShowWalletWhenItExists()
	{
		UiContext.WalletRepository
			.WhenAnyValue(x => x.Wallet)
			.WhereNotNull()
			.Take(1)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(wallet =>
			{
				WalletPage = new WalletPageViewModel(UiContext, wallet);
				WalletPage.Activate();
				ShowSyncProgress(wallet);
				ShowBalance(wallet);
			});
	}

	/// <summary>
	/// The title bar tells what the wallet owns, from the moment it is decrypted on. It is one amount: the coins of the wallet
	/// and what is locked in its fidelity bonds, private or not.
	/// </summary>
	private void ShowBalance(IWalletModel wallet)
	{
		wallet.WhenAnyValue(x => x.IsLoggedIn)
			.Select(isLoggedIn => isLoggedIn ? wallet.Balances.Select(DescribeBalance) : Observable.Return(""))
			.Switch()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(balance => Balance = balance);
	}

	/// <summary>Tells an amount the way the title bar shows the balance of the wallet.</summary>
	public static string DescribeBalance(Amount balance) => $"{balance.FormattedBtc} BTC";

	/// <summary>
	/// The title bar tells how far the wallet is with the blocks of the node, from the moment it is decrypted on.
	/// While the wallet is behind the chain it shows both heights, afterwards the one height that they share.
	/// </summary>
	private void ShowSyncProgress(IWalletModel wallet)
	{
		wallet.WhenAnyValue(x => x.IsLoggedIn)
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.IsSyncProgressVisible);

		wallet.Loader.Progress
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(progress =>
			{
				SyncProgress = DescribeSyncProgress(progress);
				SyncStatus = progress.Status.ToUserFriendlyString();
			});
	}

	/// <summary>Tells in a few words how far the wallet is with the blocks of the node.</summary>
	public static string DescribeSyncProgress(WalletLoadProgress progress) =>
		progress switch
		{
			// For about a minute no block is processed, and the wallet would look stuck.
			{ Status: WalletWasabi.Wallets.WalletSyncStatus.PreparingBondSearch } => "Preparing the search for bonds",

			// Until the node told how long the chain is, there is nothing to count towards.
			{ Blocks.Target: null } => WaitingForNode,
			{ Blocks: { Target: { } target, Current: var current } } when current >= target => $"{current:N0} blocks",
			{ Blocks: { Target: { } target, Current: var current } } => $"{current:N0}/{target:N0} blocks"
		};

	/// <summary>
	/// Until a wallet is opened, the title bar tells how many blocks the chain has according to the node. Then the progress of the wallet takes its place.
	/// </summary>
	private void ShowChainHeight()
	{
		UiContext.Services.EventBus.AsObservable<RpcStatusChanged>()
			.Select(x => x.Status.Match(node => (ConnectedRpcStatus?)node, _ => null))
			.StartWith(UiContext.Services.GetNodeStatus())
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(node =>
				// The node knows the height of the chain from the headers, also while it is still downloading the blocks.
				ChainHeight = node is not null ? $"{Math.Max(node.Headers, node.Blocks):N0} blocks mined" : WaitingForNode);

		this.WhenAnyValue(x => x.IsSyncProgressVisible, isWalletOpen => !isWalletOpen)
			.BindTo(this, x => x.IsChainHeightVisible);
	}

	/// <summary>
	/// The title bar tells the fee rate that the node estimates for a confirmation within two blocks, once the node gave its estimates.
	/// </summary>
	private void ShowFeeRate()
	{
		UiContext.Services.EventBus.AsObservable<MiningFeeRatesChanged>()
			.Select(e => e.AllFeeEstimate.Estimations.FirstOrDefault(x => x.Key == 2).Value)
			.WhereNotNull()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(feeRate => FeeRate = $"Fee Rate: {feeRate.SatoshiPerByte:0.##} sat/vB");
	}

	private async Task OpenDataFolderAsync()
	{
		try
		{
			UiContext.FileSystem.OpenFolderInFileExplorer(UiContext.Config.DataDir);
		}
		catch (Exception ex)
		{
			// For example, there is no program on this computer that opens folders.
			Logger.LogError(ex);
			await UiContext.Navigate().To().ShowErrorDialog(ex.ToUserFriendlyString(), "Data Folder", "Black Hole was unable to open its data folder.").GetResultAsync();
		}
	}
}
