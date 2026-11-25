using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using Unit = System.Reactive.Unit;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// The settings of the application, which are also its welcome. They have five columns: Tor, Node, Nostr, Coordinator
/// and Wallet, each with two boxes. The upper one connects to something that exists, or recovers the wallet; the
/// lower one is for running, or creating, one's own.
/// </summary>
/// <remarks>
/// <para>
/// As long as there is no wallet this is the welcome: it cannot be closed, and the user sets the columns up from left
/// to right, see <see cref="SettingsOrder"/>. Once there is a wallet it is what the gear of the title bar opens, every
/// box can be used, and the Wallet column configures the wallet.
/// </para>
/// <para>
/// The "Connect to" box of a connection that the user has configured tells whether it works at the moment: it has a
/// check mark if it does and an X if it does not, see <see cref="ConnectionsModel"/>. The box "Run a Nostr Relay"
/// has the same marks for the relay that the application runs itself, once the user turned that on. Running a Tor
/// relay, a Bitcoin node or a coordinator holds its place and does nothing yet, so these three boxes cannot be used.
/// </para>
/// </remarks>
public partial class SettingsPageViewModel : DialogViewModelBase<Unit>, ISettingsPage
{
	public const string SettingsTitle = "Settings";
	public const string SettingsCaption = "Tor, your node, Nostr, the coordinator and the wallet";
	public const string WelcomeCaption = "Set up Tor, your node, Nostr and the coordinator, one after the other, and then the wallet.";

	public static readonly string WelcomeTitle = $"Welcome to {Constants.AppName}";

	private string _title = SettingsTitle;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _caption = SettingsCaption;

	/// <summary>Whether this is the welcome: there is no wallet yet.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWelcome;

	/// <summary>Whether a wallet can be recovered or created: there is none, and the four connections are done.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _canAddWallet;

	/// <summary>Whether the wallet exists. Its box has a check mark then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasWallet;

	/// <summary>Whether the upper box of the Wallet column configures the wallet: the wallet was recovered.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConfigureTileUpper;

	/// <summary>Whether the lower box of the Wallet column configures the wallet: the wallet was created.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isConfigureTileLower;

	/// <summary>Whether the relay that the application runs itself is on and works at the moment. Its box has a check mark then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isNostrRelayWorking;

	/// <summary>Whether the relay that the application runs itself is on and does not work at the moment. Its box has an X then.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isNostrRelayNotWorking;

	public SettingsPageViewModel(UiContext uiContext) : base(uiContext)
	{
		Settings = uiContext.ApplicationSettings;

		Tor = new ConnectionColumnViewModel(Connection.Tor);
		Node = new ConnectionColumnViewModel(Connection.Node);
		Nostr = new ConnectionColumnViewModel(Connection.Nostr);
		Coordinator = new ConnectionColumnViewModel(Connection.Coordinator);
		Columns = [Tor, Node, Nostr, Coordinator];

		Update();

		ConnectToTorCommand = ReactiveCommand.Create(() => Navigate().To().ConnectToTor(), Tor.WhenAnyValue(x => x.IsOpen));
		ConnectToNodeCommand = ReactiveCommand.Create(() => Navigate().To().ConnectToNode(), Node.WhenAnyValue(x => x.IsOpen));
		ConnectToNostrCommand = ReactiveCommand.Create(() => Navigate().To().ConnectToNostr(), Nostr.WhenAnyValue(x => x.IsOpen));
		ConnectToCoordinatorCommand = ReactiveCommand.Create(() => Navigate().To().ConnectToCoordinator(), Coordinator.WhenAnyValue(x => x.IsOpen));

		// The relay of one's own is part of the Nostr column, and can be used when that column can.
		RunNostrRelayCommand = ReactiveCommand.Create(() => Navigate().To().RunNostrRelay(), Nostr.WhenAnyValue(x => x.IsOpen));

		var canAddWallet = this.WhenAnyValue(x => x.CanAddWallet);

		RecoverWalletCommand = ReactiveCommand.Create(OnRecoverWallet, canAddWallet);
		CreateWalletCommand = ReactiveCommand.Create(OnCreateWallet, canAddWallet);
		ConfigureWalletCommand = ReactiveCommand.Create(OnConfigureWallet, this.WhenAnyValue(x => x.HasWallet));

		RestartCommand = ReactiveCommand.Create(() => AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true));
		NextCommand = ReactiveCommand.Create(() => Close());
	}

	public override string Title
	{
		get => _title;
		protected set => this.RaiseAndSetIfChanged(ref _title, value);
	}

	public ApplicationSettings Settings { get; }

	/// <summary>Whether settings cannot be changed, because something was given on the command line or in the environment when the application was started.</summary>
	public bool IsReadOnly => Settings.IsOverridden;

	public ConnectionColumnViewModel Tor { get; }

	public ConnectionColumnViewModel Node { get; }

	public ConnectionColumnViewModel Nostr { get; }

	public ConnectionColumnViewModel Coordinator { get; }

	/// <summary>The columns of the connections from left to right, which is the order in which they are set up. The Wallet column follows them.</summary>
	public IReadOnlyList<ConnectionColumnViewModel> Columns { get; }

	public ICommand ConnectToTorCommand { get; }

	public ICommand ConnectToNodeCommand { get; }

	public ICommand ConnectToNostrCommand { get; }

	public ICommand ConnectToCoordinatorCommand { get; }

	public ICommand RunNostrRelayCommand { get; }

	public ICommand RecoverWalletCommand { get; }

	public ICommand CreateWalletCommand { get; }

	public ICommand ConfigureWalletCommand { get; }

	public ICommand RestartCommand { get; }

	/// <summary>Brings the screen up to date with the connections and the wallet.</summary>
	private void Update()
	{
		var connections = UiContext.Connections;
		var wallet = UiContext.WalletRepository.Wallet;
		var hasWallet = wallet is not null;
		var current = hasWallet ? null : SettingsOrder.GetCurrent(connections.IsDone);

		foreach (var column in Columns)
		{
			column.Update(
				connections.GetState(column.Connection),
				isOpen: SettingsOrder.IsOpen(column.Connection, hasWallet, connections.IsDone),
				isCurrent: current == column.Connection);
		}

		IsNostrRelayWorking = connections.NostrRelay == ConnectionState.Working;
		IsNostrRelayNotWorking = connections.NostrRelay == ConnectionState.NotWorking;

		var (upper, lower) = SettingsOrder.GetWalletTiles(hasWallet, isRecovered: wallet?.IsRecovered ?? false);

		IsConfigureTileUpper = upper == WalletTile.Configure;
		IsConfigureTileLower = lower == WalletTile.Configure;
		HasWallet = hasWallet;
		CanAddWallet = SettingsOrder.CanAddWallet(hasWallet, connections.IsDone);

		IsWelcome = !hasWallet;
		Title = IsWelcome ? WelcomeTitle : SettingsTitle;
		Caption = IsWelcome ? WelcomeCaption : SettingsCaption;

		// The welcome stays until there is a wallet. The settings are put away like any dialog.
		SetupCancel(enableCancel: false, enableCancelOnEscape: !IsWelcome, enableCancelOnPressed: !IsWelcome);
	}

	private void OnCreateWallet()
	{
		if (CanAddWalletNow())
		{
			Navigate().To().MultiShareOptions(new WalletCreationOptions.AddNewWallet().WithNewBackup());
		}
	}

	private void OnRecoverWallet()
	{
		if (CanAddWalletNow())
		{
			Navigate().To().RecoverMultiShareWallet(new WalletCreationOptions.RecoverWallet());
		}
	}

	private void OnConfigureWallet()
	{
		if (UiContext.WalletRepository.Wallet is { } wallet)
		{
			Navigate().To().ConfigureWallet(wallet);
		}
	}

	/// <summary>
	/// Tells the user right away when a wallet cannot be added, e.g. because a wallet file that cannot be opened is in the way.
	/// </summary>
	private bool CanAddWalletNow()
	{
		try
		{
			UiContext.Services.WalletManager.AssertWalletCanBeAdded();
			return true;
		}
		catch (InvalidOperationException ex)
		{
			Navigate().To().ShowErrorDialog(ex.Message, "Add Wallet", "A wallet cannot be added.", NavigationTarget.CompactDialogScreen);
			return false;
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		var connections = UiContext.Connections;

		// The pages of the connections report to the model, and the wallet appears when it is created or recovered.
		// Both are looked at while this screen shows, also when it comes back from one of its pages.
		connections.WhenAnyValue(x => x.Tor, x => x.Node, x => x.Nostr, x => x.Coordinator, x => x.NostrRelay, (_, _, _, _, _) => Unit.Default)
			.Merge(UiContext.WalletRepository.WhenAnyValue(x => x.Wallet).Select(_ => Unit.Default))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ => Update())
			.DisposeWith(disposables);

		Update();

		// The boxes tell whether the connections work at the moment, so that is found out while they are in sight.
		disposables.Add(connections.Watch());
	}
}
