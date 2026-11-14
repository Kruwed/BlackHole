using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Configuration;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

[NavigationMetaData(Title = "Success")]
public partial class AddedWalletPageViewModel : RoutableViewModel
{
	private readonly WalletSettingsModel _walletSettings;
	private IWalletModel? _wallet;

	public AddedWalletPageViewModel(UiContext uiContext, WalletSettingsModel walletSettings, WalletCreationOptions options) : base(uiContext)
	{
		_walletSettings = walletSettings;

		SetupCancel(enableCancel: false, enableCancelOnEscape: false, enableCancelOnPressed: false);
		EnableBack = false;

		NextCommand = ReactiveCommand.CreateFromTask(() => OnNextAsync(options));
	}

	private async Task OnNextAsync(WalletCreationOptions options)
	{
		if (_wallet is not { })
		{
			return;
		}

		IsBusy = true;

		await AutoLoginAsync(options);

		IsBusy = false;

		await Task.Delay(UiConstants.CloseSuccessDialogMillisecondsDelay);

		// The home screen shows the wallet already: it appeared there when it was saved.
		Navigate().Clear();
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		_wallet = UiContext.WalletRepository.SaveWallet(_walletSettings);

		if (NextCommand is not null && NextCommand.CanExecute(default))
		{
			NextCommand.Execute(default);
		}
	}

	private async Task AutoLoginAsync(WalletCreationOptions? options)
	{
		if (_wallet is not { })
		{
			return;
		}

		var password =
			options switch
			{
				WalletCreationOptions.AddNewWallet add => add.Backup?.Password,
				WalletCreationOptions.RecoverWallet rec => rec.Backup?.Password,
				_ => null
			};

		if (password is { })
		{
			await _wallet.Auth.LoginAsync(password);
		}
	}
}
