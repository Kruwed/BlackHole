using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Userfacing;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Login;

[NavigationMetaData(Title = "")]
public partial class LoginViewModel : RoutableViewModel
{
	[AutoNotify] private string _password;
	[AutoNotify] private string _errorMessage;

	public LoginViewModel(UiContext uiContext, IWalletModel wallet) : base(uiContext)
	{
		_password = "";
		_errorMessage = "";
		WalletName = wallet.Name;

		NextCommand = ReactiveCommand.CreateFromTask(async () => await OnNextAsync(wallet));

		OkCommand = ReactiveCommand.Create(OnOk);

		EnableAutoBusyOn(NextCommand);
	}

	public string WalletName { get; }

	public ICommand OkCommand { get; }

	private async Task OnNextAsync(IWalletModel walletModel)
	{
		var (success, compatibilityPasswordUsed) = await walletModel.Auth.TryLoginAsync(Password);

		if (!success)
		{
			ErrorMessage = "The passphrase is incorrect!";
			return;
		}

		if (compatibilityPasswordUsed)
		{
			await ShowErrorAsync(Title, PasswordHelper.CompatibilityPasswordWarnMessage, "Compatibility password was used");
		}

		walletModel.Auth.CompleteLogin();
	}

	private void OnOk()
	{
		Password = "";
		ErrorMessage = "";
	}
}
