using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.Configuration;
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
	[AutoNotify] private bool _isLoading;
	[AutoNotify] private string? _iconName;
	[AutoNotify] private string? _iconNameFocused;
	[AutoNotify] private WalletViewModel? _walletViewModel;
	[AutoNotify] private RoutableViewModel? _currentPage;

	public WalletPageViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		WalletModel = walletModel;

		// TODO: Finish partial refactor
		// Wallet property must be removed
		Wallet = UiContext.Services.WalletManager.Wallet ?? throw new InvalidOperationException("There is no wallet.");

		OpenCommand = ReactiveCommand.Create(ShowCurrentPage);

		// Show Login Page when wallet is not logged in
		this.WhenAnyValue(x => x.IsLoggedIn)
			.Where(x => !x)
			.Do(_ => ShowLogin())
			.Subscribe()
			.DisposeWith(_disposables);

		// Show Loading page when wallet is logged in
		this.WhenAnyValue(x => x.IsLoggedIn)
			.Where(x => x)
			.Do(_ => ShowWalletLoading())
			.Subscribe()
			.DisposeWith(_disposables);

		// Show main Wallet UI when wallet load is completed
		this.WhenAnyObservable(x => x.WalletModel.Loader.LoadCompleted)
			.Do(_ => ShowWallet())
			.Subscribe()
			.DisposeWith(_disposables);

		this.WhenAnyValue(x => x.WalletModel.Auth.IsLoggedIn)
			.BindTo(this, x => x.IsLoggedIn)
			.DisposeWith(_disposables);

		SetIcon();
	}

	public IWalletModel WalletModel { get; }

	public Wallet Wallet { get; }

	public string Title => UiConstants.WalletTitle;

	/// <summary>Brings the home screen back to the wallet.</summary>
	public ICommand OpenCommand { get; }

	/// <summary>
	/// Starts showing the wallet on the home screen: its login page, then its loading page, then the wallet itself.
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

	private void ShowWalletLoading()
	{
		CurrentPage = new LoadingViewModel(UiContext, WalletModel);
		IsLoading = true;
	}

	private void ShowWallet()
	{
		WalletViewModel = new WalletViewModel(UiContext, WalletModel, Wallet);

		CurrentPage = WalletViewModel;
		IsLoading = false;
	}

	public void Dispose() => _disposables.Dispose();

	private void SetIcon()
	{
		IconName = "nav_wallet_24_regular";
		IconNameFocused = "nav_wallet_24_filled";
	}
}
