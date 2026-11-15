using System.Reactive.Linq;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.ViewModels.Wallets;

namespace WalletWasabi.Fluent.ViewModels.NavBar;

/// <summary>
/// The ViewModel that represents the structure of the sidebar.
/// </summary>
[AppLifetime]
public partial class NavBarViewModel : ViewModelBase
{
	/// <summary>The wallet, once it was created or recovered.</summary>
	[AutoNotify(SetterModifier = AccessModifier.Private)] private WalletPageViewModel? _wallet;

	public NavBarViewModel(UiContext uiContext) : base(uiContext)
	{
	}

	/// <summary>
	/// Shows the wallet on the home screen: right away if there is one, otherwise as soon as it is created or recovered.
	/// </summary>
	public void Activate()
	{
		UiContext.WalletRepository
			.WhenAnyValue(x => x.Wallet)
			.WhereNotNull()
			.Take(1)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(wallet =>
			{
				var walletPage = new WalletPageViewModel(UiContext, wallet);
				Wallet = walletPage;
				walletPage.Activate();
			});
	}
}
