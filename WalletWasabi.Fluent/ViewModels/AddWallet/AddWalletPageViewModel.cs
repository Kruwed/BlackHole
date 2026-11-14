using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

[NavigationMetaData(
	Title = "Add Wallet",
	Caption = "Create or recover",
	Order = 2,
	Category = "General",
	Keywords = new[]
		{ "Wallet", "Add", "Create", "New", "Recover" },
	IconName = "nav_add_circle_24_regular",
	IconNameFocused = "nav_add_circle_24_filled",
	NavigationTarget = NavigationTarget.DialogScreen,
	NavBarPosition = NavBarPosition.Bottom,
	NavBarSelectionMode = NavBarSelectionMode.Button)]
public partial class AddWalletPageViewModel : DialogViewModelBase<Unit>
{
	public AddWalletPageViewModel(UiContext uiContext) : base(uiContext)
	{
		CreateWalletCommand = ReactiveCommand.Create(OnCreateWallet);

		RecoverWalletCommand = ReactiveCommand.Create(OnRecoverWallet);
	}

	public ICommand CreateWalletCommand { get; }

	public ICommand RecoverWalletCommand { get; }

	private void OnCreateWallet()
	{
		var options = new WalletCreationOptions.AddNewWallet().WithNewBackup();
		Navigate().To().WalletNamePage(options);
	}

	private void OnRecoverWallet()
	{
		Navigate().To().WalletNamePage(new WalletCreationOptions.RecoverWallet());
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		var enableCancel = UiContext.WalletRepository.HasWallet;
		SetupCancel(enableCancel: enableCancel, enableCancelOnEscape: enableCancel, enableCancelOnPressed: enableCancel);
	}

	public async Task Activate()
	{
		var mainViewModel = UiContext.MainViewModel
			?? throw new InvalidOperationException("MainViewModel is not initialized.");

		mainViewModel.IsOobeBackgroundVisible = true;
		await NavigateDialogAsync(this, NavigationTarget.DialogScreen);
		mainViewModel.IsOobeBackgroundVisible = false;
	}
}
