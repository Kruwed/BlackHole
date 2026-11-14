using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.AddWallet;
using WalletWasabi.Fluent.ViewModels.Navigation;
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

		NextCommand = ReactiveCommand.CreateFromTask(async () => await OnNextAsync(wallet));

		OkCommand = ReactiveCommand.Create(OnOk);

		EnableAutoBusyOn(NextCommand);
	}

	public string WalletTitle => UiConstants.WalletTitle;

	public ICommand OkCommand { get; }

	private async Task OnNextAsync(IWalletModel walletModel)
	{
		var success = await walletModel.Auth.TryLoginAsync(Password);

		if (!success)
		{
			ErrorMessage = "The password is incorrect!";
			return;
		}

		walletModel.Auth.CompleteLogin();
	}

	private void OnOk()
	{
		Password = "";
		ErrorMessage = "";
	}
}
