using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Login;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

[AppLifetime]
public partial class WalletPageViewModel : ViewModelBase, IDisposable
{
	private readonly CompositeDisposable _disposables = new();

	[AutoNotify] private bool _isLoggedIn;
	[AutoNotify] private WalletViewModel? _walletViewModel;
	[AutoNotify] private RoutableViewModel? _currentPage;

	public WalletPageViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		WalletModel = walletModel;

		// TODO: Finish partial refactor
		// Wallet property must be removed
		Wallet = UiContext.Services.WalletManager.Wallet ?? throw new InvalidOperationException("There is no wallet.");

		// Show Login Page when wallet is not logged in
		this.WhenAnyValue(x => x.IsLoggedIn)
			.Where(x => !x)
			.Do(_ => ShowLogin())
			.Subscribe()
			.DisposeWith(_disposables);

		// Show the wallet as soon as it is logged in. It fills up while the wallet processes the blocks of the node,
		// and the title bar tells how far that is.
		this.WhenAnyValue(x => x.IsLoggedIn)
			.Where(x => x)
			.Do(_ => ShowWallet())
			.Subscribe()
			.DisposeWith(_disposables);

		this.WhenAnyValue(x => x.WalletModel.Auth.IsLoggedIn)
			.BindTo(this, x => x.IsLoggedIn)
			.DisposeWith(_disposables);
	}

	public IWalletModel WalletModel { get; }

	public Wallet Wallet { get; }

	/// <summary>
	/// Starts showing the wallet on the home screen: its login page, then the wallet itself.
	/// </summary>
	public void Activate()
	{
		this.WhenAnyValue(x => x.CurrentPage)
			.WhereNotNull()
			.Do(_ => ShowCurrentPage())
			.Subscribe()
			.DisposeWith(_disposables);
	}

	private void ShowCurrentPage()
	{
		if (CurrentPage is { } currentPage)
		{
			UiContext.Navigate().To(currentPage, NavigationTarget.HomeScreen, NavigationMode.Clear);
		}
	}

	private void ShowLogin()
	{
		CurrentPage = new LoginViewModel(UiContext, WalletModel);
	}

	private void ShowWallet()
	{
		WalletViewModel = new WalletViewModel(UiContext, WalletModel, Wallet);

		CurrentPage = WalletViewModel;
	}

	public void Dispose() => _disposables.Dispose();
}
