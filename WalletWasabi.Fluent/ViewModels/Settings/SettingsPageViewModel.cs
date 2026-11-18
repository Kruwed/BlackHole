using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.Settings;

[AppLifetime]
[NavigationMetaData(Title = "Settings", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class SettingsPageViewModel : DialogViewModelBase<Unit>
{
	private const string ResyncWalletHint = "If your wallet is missing some confirmed transaction, try to resync.";
	private const string ResyncLockedWalletHint = "Decrypt the wallet to resync it.";
	private const string WalletKeysHint = "The extended public keys of the wallet and the paths that its keys are derived by. The password is asked for first.";
	private const string LockedWalletKeysHint = "Decrypt the wallet to see its keys.";

	[AutoNotify] private bool _isModified;
	[AutoNotify] private int _selectedTab;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasWallet;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _resyncWalletToolTip = ResyncLockedWalletHint;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _walletKeysToolTip = LockedWalletKeysHint;

	public SettingsPageViewModel(UiContext uiContext) : base(uiContext)
	{
		_selectedTab = 0;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		ConnectionsSettingsTab = new ConnectionsSettingsTabViewModel(UiContext, UiContext.ApplicationSettings);
		CoordinatorTabSettings = new CoordinatorTabSettingsViewModel(UiContext, UiContext.ApplicationSettings);

		RestartCommand = ReactiveCommand.Create(() => AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true));
		NextCommand = ReactiveCommand.Create(() => Close());
		OpenCommand = ReactiveCommand.CreateFromTask(Activate);

		// The wallet appears when it is created or recovered. It can be resynchronized once it is decrypted.
		var wallet = UiContext.WalletRepository.WhenAnyValue(x => x.Wallet);

		var isWalletDecrypted = wallet
			.Select(x => x?.WhenAnyValue(w => w.IsLoggedIn) ?? Observable.Return(false))
			.Switch()
			.ObserveOn(RxApp.MainThreadScheduler);

		wallet
			.Select(x => x is not null)
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.HasWallet);

		isWalletDecrypted
			.Select(isDecrypted => isDecrypted ? ResyncWalletHint : ResyncLockedWalletHint)
			.BindTo(this, x => x.ResyncWalletToolTip);

		ResyncWalletCommand = ReactiveCommand.CreateFromTask(ResyncWalletAsync, isWalletDecrypted);

		isWalletDecrypted
			.Select(isDecrypted => isDecrypted ? WalletKeysHint : LockedWalletKeysHint)
			.BindTo(this, x => x.WalletKeysToolTip);

		WalletKeysCommand = ReactiveCommand.CreateFromTask(ShowWalletKeysAsync, isWalletDecrypted);

		// Show restart message when needed
		UiContext.ApplicationSettings.IsRestartNeeded
									 .BindTo(this, x => x.IsModified);

		// Show restart notification when needed only if this page is not active.
		UiContext.ApplicationSettings.IsRestartNeeded
				 .Where(x => x && !IsActive)
				 .Do(_ => NotificationHelpers.Show(new RestartViewModel(UiContext, "To apply the new setting, Black Hole needs to be restarted")))
				 .Subscribe();
	}

	public bool IsReadOnly => UiContext.ApplicationSettings.IsOverridden;

	public ICommand RestartCommand { get; }

	/// <summary>Opens the settings. This is what the button in the title bar does.</summary>
	public ICommand OpenCommand { get; }

	/// <summary>Asks from where to resynchronize the wallet and restarts the application to do it.</summary>
	public ICommand ResyncWalletCommand { get; }

	/// <summary>Shows the keys of the wallet, after its password was given.</summary>
	public ICommand WalletKeysCommand { get; }

	public ConnectionsSettingsTabViewModel ConnectionsSettingsTab { get; }
	public CoordinatorTabSettingsViewModel CoordinatorTabSettings { get; }

	public async Task Activate()
	{
		await NavigateDialogAsync(this);
	}

	public async Task ActivateCoordinatorTabAsync()
	{
		SelectedTab = 1;
		await NavigateDialogAsync(this);
	}

	private async Task ShowWalletKeysAsync()
	{
		if (UiContext.WalletRepository.Wallet is not { } wallet)
		{
			return;
		}

		if (wallet.Auth.HasPassword && !await UiContext.Navigate().To().PasswordAuthDialog(wallet).GetResultAsync())
		{
			return;
		}

		UiContext.Navigate().To().WalletInfo(wallet);
	}

	private async Task ResyncWalletAsync()
	{
		if (UiContext.WalletRepository.Wallet is not { } wallet)
		{
			return;
		}

		if (!wallet.IsHeightKnown)
		{
			// The dialog would offer to start with the first block of the chain, which makes a wallet without a history read all of it.
			await ShowErrorAsync(
				"Resync Wallet",
				"The wallet is new and has not reached your node yet. Its history starts with the block that the node is at when it does.",
				"There is nothing to resync yet.",
				NavigationTarget.CompactDialogScreen);
			return;
		}

		var result = await UiContext.Navigate().To().ResyncWallet(wallet.Settings.BirthHeight, wallet.Settings.MinGapLimit).GetResultAsync();

		if (result is not null)
		{
			wallet.Resync(result.StartingHeight, result.MinGapLimit);
			UiContext.Navigate(MetaData.NavigationTarget).Clear();
			AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true);
		}
	}
}
