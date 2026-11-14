using System.Reactive;
using System.Reactive.Disposables;
using System.Windows.Input;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;

namespace WalletWasabi.Fluent.ViewModels.AddWallet;

/// <summary>
/// The choice between creating and recovering the wallet. It is offered as long as there is no wallet: the application works with a single one.
/// The node that the wallet is going to work with can be chosen here too.
/// </summary>
[NavigationMetaData(
	Title = "Add Wallet",
	Caption = "Create or recover",
	NavigationTarget = NavigationTarget.DialogScreen,
	Searchable = false)]
public partial class AddWalletPageViewModel : DialogViewModelBase<Unit>
{
	public AddWalletPageViewModel(UiContext uiContext) : base(uiContext)
	{
		CreateWalletCommand = ReactiveCommand.Create(OnCreateWallet);

		RecoverWalletCommand = ReactiveCommand.Create(OnRecoverWallet);

		ConnectToFullNodeCommand = ReactiveCommand.Create(() => Navigate().To().ConnectToFullNode());
	}

	public ICommand CreateWalletCommand { get; }

	public ICommand RecoverWalletCommand { get; }

	public ICommand ConnectToFullNodeCommand { get; }

	private void OnCreateWallet()
	{
		if (CanAddWallet())
		{
			Navigate().To().MultiShareOptions(new WalletCreationOptions.AddNewWallet().WithNewBackup());
		}
	}

	private void OnRecoverWallet()
	{
		if (CanAddWallet())
		{
			Navigate().To().RecoverMultiShareWallet(new WalletCreationOptions.RecoverWallet());
		}
	}

	/// <summary>
	/// Tells the user right away when a wallet cannot be added, e.g. because a wallet file that cannot be opened is in the way.
	/// </summary>
	private bool CanAddWallet()
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

		var enableCancel = UiContext.WalletRepository.HasWallet;
		SetupCancel(enableCancel: enableCancel, enableCancelOnEscape: enableCancel, enableCancelOnPressed: enableCancel);
	}
}
