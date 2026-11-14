using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Userfacing;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class WalletAuthModel : ReactiveObject
{
	private readonly Wallet _wallet;
	[AutoNotify] private bool _isLoggedIn;

	public WalletAuthModel(Wallet wallet)
	{
		_wallet = wallet;
	}

	public bool HasPassword => !string.IsNullOrEmpty(_wallet.Password);

	public async Task LoginAsync(string password)
	{
		var isPasswordCorrect = await TryLoginAsync(password);
		if (!isPasswordCorrect)
		{
			throw new InvalidOperationException("Incorrect password.");
		}

		CompleteLogin();
	}

	public async Task<bool> TryLoginAsync(string password)
	{
		return await Task.Run(() => _wallet.TryLogin(password));
	}

	public async Task<bool> TryPasswordAsync(string password)
	{
		return await Task.Run(() => PasswordHelper.TryPassword(_wallet.KeyManager, password));
	}

	public void CompleteLogin()
	{
		IsLoggedIn = true;
	}

	public void Logout()
	{
		_wallet.Logout();
		IsLoggedIn = false;
	}
}
