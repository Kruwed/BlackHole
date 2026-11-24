using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>
/// The tabs of the Coinjoin dialog: the protocols that there are to choose from. The dialog shows the main screen of
/// the tab that is selected, and the box of the coinjoins is set to it.
/// </summary>
public enum CoinJoinTab
{
	/// <summary>
	/// Payments that the payee takes part in. There is none yet: the tab holds its place, and its main screen has the
	/// ordinary payment and Receive. It is the one that the dialog opens with.
	/// </summary>
	Payjoin,

	/// <summary>Rounds of the coordinator with the ticked coins, ten at most.</summary>
	WabiSabi,

	/// <summary>Rounds of the coordinator with one ticked coin of a standard amount.</summary>
	ZeroLink,

	/// <summary>
	/// Coinjoins with the offers of makers. The main screen of the tab is the order book with the plan of the
	/// transaction; the transaction itself cannot be made yet.
	/// </summary>
	Taker,

	/// <summary>
	/// The wallet as a maker. The main screen of the tab locks coins in a fidelity bond and posts offers; a coinjoin
	/// that takes an offer does not exist yet.
	/// </summary>
	Maker
}

/// <summary>
/// The box of the coinjoins at the bottom of the Coinjoin dialog. There is one box for all the tabs of the dialog: it
/// shows the one that is selected, with its name, what its coinjoin is doing, and Play and Stop. A round takes the
/// coins that are ticked in the list of the Coins tab.
/// </summary>
/// <remarks>
/// A wallet has one coinjoin at a time, and the box is where it is watched and stopped. So while a coinjoin is under
/// way the box shows that one, whatever tab is selected: the tab still decides which main screen the dialog shows, so
/// that a payment can be made or an address be had while a round goes on.
/// </remarks>
[AppLifetime]
public partial class CoinJoinBoxViewModel : ViewModelBase
{
	/// <summary>The tab that the dialog opens with, until another one is selected.</summary>
	public const CoinJoinTab DefaultTab = CoinJoinTab.Payjoin;

	private const string NoCoordinatorText = "Coordinator not configured";
	private const string NoCoordinatorDetail = "Coinjoin not available";
	private const string NotLoadedText = "Coinjoin waits for the wallet";
	private const string NotLoadedDetail = "It is processing the blocks";
	private const string NotAvailableYetText = "Not available yet";
	private const string PayjoinDetail = "Send and Receive work without it";
	private const string MakerDetail = "Offers and bonds work all the same";
	private const string TakerDetail = "A transaction can be planned all the same";

	private readonly IWalletModel _walletModel;
	private readonly CoinListViewModel _coins;
	private readonly Dictionary<CoinJoinTab, CoinJoinStateViewModel> _states = [];

	[AutoNotify] private CoinJoinTab _selectedTab = DefaultTab;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinTab _shownTab = DefaultTab;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinStateViewModel? _state;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _name = GetName(DefaultTab);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _notice = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _noticeDetail = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _noPlayToolTip = "";

	/// <param name="coins">The list of the Coins tab. A round takes the coins that are ticked in it.</param>
	public CoinJoinBoxViewModel(UiContext uiContext, IWalletModel walletModel, CoinListViewModel coins) : base(uiContext)
	{
		_walletModel = walletModel;
		_coins = coins;

		// The controls of the coordinator's coinjoins come with the loaded wallet: what the wallet knows about its coins
		// is not complete until it has processed the blocks of the node. They also take a coordinator, which the user
		// may connect the application to while the wallet is open. Nothing starts a round but their Play button.
		walletModel.WhenAnyValue(x => x.IsLoaded, x => x.Coinjoin)
			.Subscribe(x =>
			{
				if (x.Item1 && _states.Count == 0 && x.Item2 is { } coinjoin)
				{
					CreateStates(coinjoin);
				}
				else
				{
					// What the box says is missing may have changed.
					Refresh();
				}
			});

		// The dialog tells which of its tabs is selected.
		this.WhenAnyValue(x => x.SelectedTab)
			.Subscribe(_ => Refresh());
	}

	/// <summary>The tabs of the dialog, in their order.</summary>
	public static IReadOnlyList<CoinJoinTab> AllTabs { get; } = [CoinJoinTab.Payjoin, CoinJoinTab.WabiSabi, CoinJoinTab.ZeroLink, CoinJoinTab.Taker, CoinJoinTab.Maker];

	/// <summary>Whether a coordinator is set. Without one there is no WabiSabi and no ZeroLink coinjoin, and the box says so.</summary>
	public bool IsCoinJoinEnabled => _walletModel.IsCoinJoinEnabled;

	/// <summary>What a tab is called, on the tab itself and in the box.</summary>
	public static string GetName(CoinJoinTab tab) =>
		tab switch
		{
			CoinJoinTab.Payjoin => "Payjoin",
			CoinJoinTab.WabiSabi => "WabiSabi",
			CoinJoinTab.ZeroLink => "ZeroLink",
			CoinJoinTab.Taker => "Taker",
			CoinJoinTab.Maker => "Maker",
			_ => tab.ToString()
		};

	/// <summary>
	/// The coinjoins of the coordinator that a tab stands for. Null for one that the coordinator has no part in: a
	/// payjoin is made with the payee, and takers and makers coinjoin with each other.
	/// </summary>
	public static CoinJoinProtocol? GetProtocol(CoinJoinTab tab) =>
		tab switch
		{
			CoinJoinTab.WabiSabi => CoinJoinProtocol.WabiSabi,
			CoinJoinTab.ZeroLink => CoinJoinProtocol.ZeroLink,
			_ => null
		};

	/// <summary>
	/// Tells what the box shows. A coinjoin that is under way has the box, whatever tab is selected: the wallet has one
	/// coinjoin at a time, and the box is where it is watched and stopped. Without one, the box shows the tab that is
	/// selected.
	/// </summary>
	/// <param name="selected">The tab that is selected in the dialog.</param>
	/// <param name="underWay">The tab of the coinjoin that is under way, if one is.</param>
	public static CoinJoinTab ChooseShownTab(CoinJoinTab selected, CoinJoinTab? underWay) =>
		underWay ?? selected;

	/// <summary>
	/// Tells the wallet's choice of coins for the round that Play starts: every coin of the list is named, the ticked
	/// ones as chosen and the others as not chosen. So the round gets what is ticked now and nothing that an earlier
	/// round was started with.
	/// </summary>
	public static IReadOnlyDictionary<OutPoint, bool> GetRoundSelection(IEnumerable<(OutPoint Outpoint, bool IsTicked)> coins)
	{
		var selection = new Dictionary<OutPoint, bool>();

		foreach (var (outpoint, isTicked) in coins)
		{
			selection[outpoint] = isTicked;
		}

		return selection;
	}

	/// <summary>
	/// What the box says in place of the state of a coinjoin, for a tab that has none to show: what is missing, in two
	/// lines, and why there is no Play.
	/// </summary>
	/// <param name="isCoordinatorSet">Whether a coordinator is set. With one, the coinjoins of the coordinator are only missing until the wallet has processed the blocks of the node.</param>
	public static (string Notice, string Detail, string NoPlayToolTip) DescribeUnavailable(CoinJoinTab tab, bool isCoordinatorSet)
	{
		switch (tab)
		{
			case CoinJoinTab.Payjoin:
				// Its main screen is not empty: the ordinary payment and Receive are there.
				return (NotAvailableYetText, PayjoinDetail, "Payjoin is not available yet.");

			case CoinJoinTab.Maker:
				// Its main screen locks bonds and posts offers. What is missing is the coinjoin that takes an offer.
				return (NotAvailableYetText, MakerDetail, "Maker coinjoins are not available yet.");

			case CoinJoinTab.Taker:
				// Its main screen plans the transaction. What is missing is the way to the makers that sign it.
				return (NotAvailableYetText, TakerDetail, "Taker coinjoins are not available yet.");
		}

		if (GetProtocol(tab) is null)
		{
			return (NotAvailableYetText, "", $"{GetName(tab)} coinjoins are not available yet.");
		}

		if (!isCoordinatorSet)
		{
			return (NoCoordinatorText, NoCoordinatorDetail, "Set a coordinator first: the gear in the top right corner, then Connect to Coordinator.");
		}

		return (NotLoadedText, NotLoadedDetail, "The wallet is still processing the blocks of the node.");
	}

	private void CreateStates(WalletCoinjoinModel coinjoin)
	{
		foreach (var tab in AllTabs)
		{
			if (GetProtocol(tab) is not { } protocol)
			{
				continue;
			}

			var state = new CoinJoinStateViewModel(UiContext, _walletModel, coinjoin, protocol, () => StartRoundAsync(protocol));

			_states[tab] = state;

			// Stop shows from the moment that the wallet has started a coinjoin until it has stopped it. A coinjoin
			// takes the box when it starts, whoever started it: a round that a payment interrupted starts again by
			// itself when the payment is done with. When it is over, the box goes back to the tab that is selected.
			state.WhenAnyValue(x => x.StopVisible)
				.Subscribe(_ => Refresh());
		}

		Refresh();
	}

	/// <summary>The tab of the coinjoin that is under way, if one is.</summary>
	private CoinJoinTab? GetTabUnderWay()
	{
		foreach (var (tab, state) in _states)
		{
			if (state.StopVisible)
			{
				return tab;
			}
		}

		return null;
	}

	/// <summary>Shows in the box what it has to show.</summary>
	private void Refresh()
	{
		var tab = ChooseShownTab(SelectedTab, GetTabUnderWay());

		ShownTab = tab;
		Name = GetName(tab);
		State = _states.GetValueOrDefault(tab);

		(Notice, NoticeDetail, NoPlayToolTip) = State is null
			? DescribeUnavailable(tab, IsCoinJoinEnabled)
			: ("", "", "");
	}

	/// <summary>
	/// Starts one round of the given kind with the coins that are ticked. The wallet is told first which coins they
	/// are: a round takes of the coins that the wallet has as chosen for coinjoins, ten at most in a WabiSabi round and
	/// one in a ZeroLink round. If the wallet could not be told, no round is started, as it would get other coins than
	/// the ticked ones.
	/// </summary>
	private async Task StartRoundAsync(CoinJoinProtocol protocol)
	{
		if (_walletModel.Coinjoin is not { } coinjoin)
		{
			return;
		}

		try
		{
			var ticks = _coins.CoinItems.Select(row => (row.Coin.GetSmartCoin().Outpoint, row.IsSelected)).ToArray();

			await _walletModel.Coins.SelectForCoinjoinAsync(GetRoundSelection(ticks));
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
			return;
		}

		await coinjoin.StartAsync(protocol);
	}
}
