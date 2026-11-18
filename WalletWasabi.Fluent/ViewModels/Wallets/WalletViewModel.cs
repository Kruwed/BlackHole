using System.Collections.Generic;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Configuration;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Tabs;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>
/// The wallet, once it is decrypted. It is a set of pages, and the window shows one of them at a time. The title bar
/// lists them as tabs: the history, which is the one to start with, sending, receiving, and one for every coinjoin protocol.
/// </summary>
[AppLifetime]
public partial class WalletViewModel : RoutableViewModel, IWalletViewModel
{
	[AutoNotify] private WalletTabViewModel _selectedTab;

	private string _title = UiConstants.WalletTitle;

	public WalletViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet) : base(uiContext)
	{
		WalletModel = walletModel;
		Wallet = wallet;

		History = new HistoryTabViewModel(uiContext, walletModel, Select);
		Send = new SendTabViewModel(uiContext, walletModel, wallet, Select);
		Receive = new ReceiveTabViewModel(uiContext, walletModel, Select);
		WabiSabi = new WabiSabiTabViewModel(uiContext, walletModel, wallet, Select);
		JoinMarket = new JoinMarketTabViewModel(uiContext, walletModel, wallet, Select);
		ZeroLink = new ZeroLinkTabViewModel(uiContext, Select);

		Tabs = [History, Send, Receive, WabiSabi, JoinMarket, ZeroLink];

		_selectedTab = History;

		this.WhenAnyValue(x => x.SelectedTab)
			.Subscribe(selected =>
			{
				foreach (var tab in Tabs)
				{
					tab.IsSelected = tab == selected;
				}
			});
	}

	// TODO: Remove this
	public Wallet Wallet { get; }

	public IWalletModel WalletModel { get; }

	public bool IsLoggedIn => WalletModel.Auth.IsLoggedIn;

	/// <summary>The pages of the wallet, in the order of their tabs.</summary>
	public IReadOnlyList<WalletTabViewModel> Tabs { get; }

	public HistoryTabViewModel History { get; }

	public SendTabViewModel Send { get; }

	public ReceiveTabViewModel Receive { get; }

	public WabiSabiTabViewModel WabiSabi { get; }

	public JoinMarketTabViewModel JoinMarket { get; }

	public ZeroLinkTabViewModel ZeroLink { get; }

	public override string Title
	{
		get => _title;
		protected set => this.RaiseAndSetIfChanged(ref _title, value);
	}

	/// <inheritdoc />
	public void SelectTransaction(uint256 txid, bool showHistory)
	{
		if (showHistory)
		{
			SelectedTab = History;
		}

		RxApp.MainThreadScheduler.Schedule(async () =>
		{
			await Task.Delay(500);
			History.History.SelectTransaction(txid);
		});
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		foreach (var tab in Tabs)
		{
			tab.Activate(disposables);
		}
	}

	private void Select(WalletTabViewModel tab)
	{
		SelectedTab = tab;
	}
}
