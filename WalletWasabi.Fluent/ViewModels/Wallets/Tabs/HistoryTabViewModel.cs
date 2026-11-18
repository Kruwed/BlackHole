using System.Reactive.Disposables;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Home.History;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>The first tab: the transactions of the wallet. It is the one that shows when the wallet is opened.</summary>
[AppLifetime]
public class HistoryTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "History";

	public HistoryTabViewModel(UiContext uiContext, IWalletModel wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		History = new HistoryViewModel(uiContext, wallet);
	}

	public HistoryViewModel History { get; }

	protected override void OnActivated(CompositeDisposable disposables)
	{
		base.OnActivated(disposables);

		History.Activate(disposables);
	}
}
