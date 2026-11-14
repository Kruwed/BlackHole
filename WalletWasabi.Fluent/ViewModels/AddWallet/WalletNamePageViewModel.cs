using System.Reactive.Disposables;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Validation;
using System.Threading.Tasks;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

[NavigationMetaData(Title = "Wallet Name")]
public partial class WalletNamePageViewModel : RoutableViewModel
{
	private readonly WalletCreationOptions _options;
	[AutoNotify] private string _walletName;

	public WalletNamePageViewModel(UiContext uiContext, WalletCreationOptions options) : base(uiContext)
	{
		_options = options;
		_walletName = UiContext.WalletRepository.GetNextWalletName();

		EnableBack = true;

		var nextCommandCanExecute =
			this.WhenAnyValue(x => x.WalletName)
				.Select(_ => !Validations.Any);

		NextCommand = ReactiveCommand.CreateFromTask(OnNextAsync, nextCommandCanExecute);

		this.ValidateProperty(x => x.WalletName, ValidateWalletName);
	}

	private async Task OnNextAsync()
	{
		IsBusy = true;

		// Makes sure we can create a wallet with this wallet name.
		await Task.Run(() => WalletGenerator.GetWalletFilePath(WalletName, UiContext.Services.GetWalletsDir()));

		IsBusy = false;

		var options = _options with { WalletName = WalletName };

		switch (options)
		{
			case WalletCreationOptions.AddNewWallet add:
				Navigate().To().MultiShareOptions(add);
				break;

			case WalletCreationOptions.RecoverWallet rec:
				Navigate().To().RecoverMultiShareWallet(rec);
				break;

			default:
				throw new InvalidOperationException($"{nameof(WalletCreationOptions)} not supported: {options?.GetType().Name}");
		}
	}

	private void ValidateWalletName(IValidationErrors errors)
	{
		var error = UiContext.WalletRepository.ValidateWalletName(WalletName);
		if (error is { } e)
		{
			errors.Add(e.Severity, e.Message);
		}
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		if (isInHistory && !UiContext.WalletRepository.HasWallet)
		{
			Navigate().Back();
		}
		else if (!UiContext.WalletRepository.HasWallet && NextCommand is { } cmd && cmd.CanExecute(default))
		{
			cmd.Execute(default);
		}

		var enableCancel = UiContext.WalletRepository.HasWallet;
		SetupCancel(enableCancel: enableCancel, enableCancelOnEscape: enableCancel, enableCancelOnPressed: enableCancel);
	}
}
