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

	[AutoNotify] private string _title = Constants.AppName;
	[AutoNotify] private WindowState _windowState;
	[AutoNotify] private bool _isOobeBackgroundVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSyncProgressVisible;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _syncProgress = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _syncStatus = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _feeRate = "";
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

				await UiContext.Navigate().To().WelcomePage().GetResultAsync();

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

	public void ClearStacks()
	{
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
			});
	}

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
				// Until the node told how long the chain is, there is nothing to count towards.
				SyncProgress = progress.Blocks.Target switch
				{
					null => WaitingForNode,
					{ } target when progress.Blocks.Current >= target => $"{progress.Blocks.Current:N0} blocks synced",
					{ } target => $"{progress.Blocks.Current:N0}/{target:N0} blocks synced"
				};
				SyncStatus = progress.Status.ToUserFriendlyString();
			});
	}

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
