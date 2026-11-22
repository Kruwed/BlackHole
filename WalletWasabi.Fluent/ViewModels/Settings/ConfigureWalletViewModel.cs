using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// What is done with the wallet as a whole: it can be resynchronized, and its keys can be shown. The settings lead
/// here once there is a wallet.
/// </summary>
[NavigationMetaData(Title = "Configure Wallet")]
public partial class ConfigureWalletViewModel : RoutableViewModel
{
	private const string ResyncWalletHint = "If your wallet is missing some confirmed transaction, try to resync.";
	private const string ResyncLockedWalletHint = "Decrypt the wallet to resync it.";
	private const string WalletKeysHint = "The extended public keys of the wallet and the paths that its keys are derived by. The password is asked for first.";
	private const string LockedWalletKeysHint = "Decrypt the wallet to see its keys.";

	private readonly IWalletModel _wallet;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _resyncWalletToolTip = ResyncLockedWalletHint;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _walletKeysToolTip = LockedWalletKeysHint;

	public ConfigureWalletViewModel(UiContext uiContext, IWalletModel wallet) : base(uiContext)
	{
		_wallet = wallet;

		EnableBack = true;
		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);

		// The wallet can be resynchronized, and its keys can be shown, once it is decrypted.
		var isWalletDecrypted = wallet
			.WhenAnyValue(x => x.IsLoggedIn)
			.ObserveOn(RxApp.MainThreadScheduler);

		isWalletDecrypted
			.Select(isDecrypted => isDecrypted ? ResyncWalletHint : ResyncLockedWalletHint)
			.BindTo(this, x => x.ResyncWalletToolTip);

		isWalletDecrypted
			.Select(isDecrypted => isDecrypted ? WalletKeysHint : LockedWalletKeysHint)
			.BindTo(this, x => x.WalletKeysToolTip);

		ResyncWalletCommand = ReactiveCommand.CreateFromTask(ResyncWalletAsync, isWalletDecrypted);
		WalletKeysCommand = ReactiveCommand.CreateFromTask(ShowWalletKeysAsync, isWalletDecrypted);
	}

	/// <summary>Asks from where to resynchronize the wallet and restarts the application to do it.</summary>
	public ICommand ResyncWalletCommand { get; }

	/// <summary>Shows the keys of the wallet, after its password was given.</summary>
	public ICommand WalletKeysCommand { get; }

	private async Task ShowWalletKeysAsync()
	{
		if (_wallet.Auth.HasPassword && !await UiContext.Navigate().To().PasswordAuthDialog(_wallet).GetResultAsync())
		{
			return;
		}

		UiContext.Navigate().To().WalletInfo(_wallet);
	}

	private async Task ResyncWalletAsync()
	{
		if (!_wallet.IsHeightKnown)
		{
			// The dialog would offer to start with the first block of the chain, which makes a wallet without a history read all of it.
			await ShowErrorAsync(
				"Resync Wallet",
				"The wallet is new and has not reached your node yet. Its history starts with the block that the node is at when it does.",
				"There is nothing to resync yet.",
				NavigationTarget.CompactDialogScreen);
			return;
		}

		var result = await UiContext.Navigate().To().ResyncWallet(_wallet.Settings.BirthHeight, _wallet.Settings.MinGapLimit).GetResultAsync();

		if (result is not null)
		{
			_wallet.Resync(result.StartingHeight, result.MinGapLimit);
			Navigate().Clear();
			AppLifetimeHelper.Shutdown(withShutdownPrevention: true, restart: true);
		}
	}
}
