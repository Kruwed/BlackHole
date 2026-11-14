using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Services;
using WalletWasabi.Fluent.ViewModels.Dialogs.ReleaseHighlights;
using WalletWasabi.Fluent.ViewModels.NavBar;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.SearchBar;
using WalletWasabi.Fluent.ViewModels.SearchBar.Sources;
using WalletWasabi.Fluent.ViewModels.Settings;
using WalletWasabi.Fluent.ViewModels.StatusIcon;
using WalletWasabi.Fluent.ViewModels.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Notifications;
using WalletWasabi.Helpers;
using System.Diagnostics.CodeAnalysis;

namespace WalletWasabi.Fluent.ViewModels;

[AppLifetime]
public partial class MainViewModel : ViewModelBase
{
	[AutoNotify] private string _title = Constants.AppName;
	[AutoNotify] private WindowState _windowState;
	[AutoNotify] private bool _isOobeBackgroundVisible;
	[AutoNotify] private bool _isCoinJoinActive;

	/// <summary>The notification that the node cannot be reached, as long as nothing took it off the screen.</summary>
	private INotification? _bitcoinRpcErrorNotification;

	public MainViewModel(UiContext uiContext) : base(uiContext)
	{
		UiContext.SetMainViewModel(this);

		ApplyUiConfigWindowState();

		DialogScreen = new DialogScreenViewModel(UiContext);
		FullScreen = new DialogScreenViewModel(UiContext, NavigationTarget.FullScreen);
		CompactDialogScreen = new DialogScreenViewModel(UiContext, NavigationTarget.CompactDialogScreen);
		NavBar = new NavBarViewModel(UiContext);
		MainScreen = new TargettedNavigationStack(UiContext, NavigationTarget.HomeScreen);
		UiContext.RegisterNavigation(new NavigationState(UiContext, MainScreen, DialogScreen, FullScreen, CompactDialogScreen));

		NavBar.Activate();

		StatusIcon = new StatusIconViewModel(UiContext);

		SettingsPage = new SettingsPageViewModel(UiContext);
		PrivacyMode = new PrivacyModeViewModel(UiContext, UiContext.ApplicationSettings);
		Notifications = new WalletNotificationsViewModel(UiContext, () => NavBar.Wallet?.WalletViewModel);

		NavigationManager.RegisterType(NavBar);

		this.RegisterAllViewModels(UiContext);

		RxApp.MainThreadScheduler.Schedule(async () => await NavBar.InitialiseAsync());

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

		SearchBar = CreateSearchBar();

		NetworkBadgeName =
			UiContext.ApplicationSettings.Network == Network.Main
			? ""
			: UiContext.ApplicationSettings.Network.Name;
	}

	public IObservable<bool> IsMainContentEnabled { get; }

	public string NetworkBadgeName { get; }

	public IObservable<WalletViewModel> CurrentWallet { get; }

	public TargettedNavigationStack MainScreen { get; }

	public SearchBarViewModel SearchBar { get; }

	public DialogScreenViewModel DialogScreen { get; }
	public DialogScreenViewModel FullScreen { get; }
	public DialogScreenViewModel CompactDialogScreen { get; }
	public NavBarViewModel NavBar { get; }
	public StatusIconViewModel StatusIcon { get; }
	public SettingsPageViewModel SettingsPage { get; }
	public PrivacyModeViewModel PrivacyMode { get; }
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
		UiContext.WalletRepository
			.WhenAnyValue(x => x.Wallet)
			.Select(wallet => wallet?.IsCoinjoinRunning ?? Observable.Return(false))
			.Switch()
			.BindTo(this, x => x.IsCoinJoinActive);

		var rpcStatuses = UiContext.Services.EventBus.AsObservable<RpcStatusChanged>()
			.Select(x => x.Status);

		rpcStatuses
			.Where(x => !x.IsOk)
			.ObserveOn(RxApp.MainThreadScheduler)
			// Not while the user is telling where the node is: there the notification would only be in the way.
			.Where(_ => DialogScreen.CurrentPage is not ConnectToFullNodeViewModel)
			.Take(1)
			.Subscribe(x =>
			{
				_bitcoinRpcErrorNotification = NotificationHelpers.ShowError(
					"Could not connect to Bitcoin RPC",
					$"\n>>Click here to verify Bitcoin RPC settings.<<",
					onClick: () =>
					{
						SettingsPage.SelectedTab = 1; // Bitcoin Tab
						_ = SettingsPage.Activate();
					});

				// The message is out of date as soon as the node answers.
				rpcStatuses
					.Where(status => status.IsOk)
					.Take(1)
					.ObserveOn(RxApp.MainThreadScheduler)
					.Subscribe(_ => DismissBitcoinRpcError());
			});

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
	/// Takes the notification that the node cannot be reached off the screen. It is out of date once the node answers,
	/// and it is in the way where the user is about to tell where the node is.
	/// </summary>
	public void DismissBitcoinRpcError()
	{
		if (_bitcoinRpcErrorNotification is { } notification)
		{
			_bitcoinRpcErrorNotification = null;
			NotificationHelpers.Close(notification);
		}
	}

	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Same lifecycle as the application. Won't be disposed separately.")]
	private SearchBarViewModel CreateSearchBar()
	{
		// This subject is created to solve the circular dependency between the sources and SearchBarViewModel
		var querySubject = new Subject<string>();

		var source = new CompositeSearchSource(
			new ActionsSearchSource(UiContext, querySubject),
			new SettingsSearchSource(UiContext, querySubject),
			new TransactionsSearchSource(NavBar, querySubject),
			UiContext.EditableSearchSource);

		var searchBar = new SearchBarViewModel(source);

		var queries = searchBar
			.WhenAnyValue(a => a.SearchText)
			.WhereNotNull();

		UiContext.EditableSearchSource.SetQueries(queries);

		queries
			.Subscribe(querySubject);

		return searchBar;
	}
}
