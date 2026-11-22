using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets;

/// <summary>The kinds of coinjoin that the menu of the coinjoin box lists. The box is set to one of them.</summary>
public enum CoinJoinOption
{
	/// <summary>Rounds of the coordinator with the ticked coins, ten at most.</summary>
	WabiSabi,

	/// <summary>Rounds of the coordinator with one ticked coin of a standard amount.</summary>
	ZeroLink,

	/// <summary>Coinjoins with the offers of market makers. There is none yet: the option holds its place.</summary>
	Taker
}

/// <summary>
/// The box of the coinjoins in the Coinjoin dialog. There is one box for every kind of coinjoin: its menu lists the
/// kinds, and the box shows the one that it is set to, with its name, what its coinjoin is doing, and Play and Stop.
/// The menu also leads to the offer of the wallet as a market maker and to its fidelity bonds. Everything takes the
/// coins that are ticked in the list of the Coins tab.
/// </summary>
/// <remarks>
/// A wallet has one coinjoin at a time. While one is under way the box is set to its kind and stays so, as the box is
/// where the coinjoin is watched and stopped.
/// </remarks>
[AppLifetime]
public partial class CoinJoinBoxViewModel : ViewModelBase
{
	private const string NoCoordinatorText = "Coordinator not configured";
	private const string NoCoordinatorDetail = "Coinjoin not available";
	private const string NotLoadedText = "Coinjoin waits for the wallet";
	private const string NotLoadedDetail = "It is processing the blocks";
	private const string NotAvailableYetText = "Not available yet";

	private readonly IWalletModel _walletModel;
	private readonly CoinListViewModel _coins;
	private readonly Dictionary<CoinJoinOption, CoinJoinStateViewModel> _states = [];

	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinOption _selectedOption = CoinJoinOption.WabiSabi;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinStateViewModel? _state;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isCoinJoinUnderWay;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _name = GetName(CoinJoinOption.WabiSabi);
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _notice = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _noticeDetail = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _noPlayToolTip = "";

	/// <param name="coins">The list of the Coins tab. A round, an offer and a bond take the coins that are ticked in it.</param>
	public CoinJoinBoxViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins) : base(uiContext)
	{
		_walletModel = walletModel;
		_coins = coins;

		IsCoinJoinEnabled = walletModel.IsCoinJoinEnabled;

		// A kind can be chosen while no coinjoin is under way. The one that the box is set to stays a choice, so that
		// it does not look as if it were not to be had.
		Options =
		[
			.. AllOptions.Select(option => new CoinJoinOptionViewModel(
				option,
				ReactiveCommand.Create(
					() => Select(option),
					this.WhenAnyValue(x => x.IsCoinJoinUnderWay, x => x.SelectedOption, (isUnderWay, selected) => !isUnderWay || selected == option))))
		];

		WabiSabi = Options.Single(x => x.Option == CoinJoinOption.WabiSabi);
		ZeroLink = Options.Single(x => x.Option == CoinJoinOption.ZeroLink);
		Taker = Options.Single(x => x.Option == CoinJoinOption.Taker);

		// The controls of the coordinator's coinjoins come with the loaded wallet: what the wallet knows about its coins
		// is not complete until it has processed the blocks of the node. Nothing starts a round but their Play button.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.Where(isLoaded => isLoaded && _states.Count == 0 && walletModel.Coinjoin is not null)
			.Subscribe(_ => CreateStates(walletModel.Coinjoin!));

		this.WhenAnyValue(x => x.SelectedOption)
			.Subscribe(_ => Refresh());

		// The settings only decide how the wallet coinjoins later, so they are open whatever the box is set to.
		CoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});

		CoinJoinPaymentsCommand = ReactiveCommand.Create(() => UiContext.Navigate(NavigationTarget.DialogScreen).To().CoinJoinPayments(walletModel, wallet));

		// An offer and a bond are about the coins of the wallet, not about the coordinator.
		MakerCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().MakerOffer(walletModel, coins));
		BondCommand = ReactiveCommand.Create(() => UiContext.Navigate().To().Bonds(walletModel, wallet, coins));
	}

	/// <summary>The kinds of coinjoin in the order of the menu.</summary>
	public static IReadOnlyList<CoinJoinOption> AllOptions { get; } = [CoinJoinOption.WabiSabi, CoinJoinOption.ZeroLink, CoinJoinOption.Taker];

	/// <summary>The kinds of coinjoin as the menu lists them.</summary>
	public IReadOnlyList<CoinJoinOptionViewModel> Options { get; }

	public CoinJoinOptionViewModel WabiSabi { get; }

	public CoinJoinOptionViewModel ZeroLink { get; }

	public CoinJoinOptionViewModel Taker { get; }

	/// <summary>Whether a coordinator is set. Without one there is no WabiSabi and no ZeroLink coinjoin, and the box says so.</summary>
	public bool IsCoinJoinEnabled { get; }

	/// <summary>Opens the settings of the coinjoins: the coordinator and the limits that the wallet keeps to.</summary>
	public ICommand CoordinatorSettingsCommand { get; }

	/// <summary>Opens the payments that are waiting for a WabiSabi round to make them.</summary>
	public ICommand CoinJoinPaymentsCommand { get; }

	/// <summary>Opens the offer of the wallet as a market maker. An offer offers the coins that are ticked.</summary>
	public ICommand MakerCommand { get; }

	/// <summary>Opens the fidelity bonds of the wallet. A new bond locks the coins that are ticked.</summary>
	public ICommand BondCommand { get; }

	/// <summary>What the box and its menu call a kind of coinjoin.</summary>
	public static string GetName(CoinJoinOption option) =>
		option switch
		{
			CoinJoinOption.WabiSabi => "WabiSabi",
			CoinJoinOption.ZeroLink => "ZeroLink",
			CoinJoinOption.Taker => "Taker",
			_ => option.ToString()
		};

	/// <summary>
	/// The coinjoins of the coordinator that a kind stands for. Null for a kind that the coordinator has no part in:
	/// a taker coinjoins with market makers.
	/// </summary>
	public static CoinJoinProtocol? GetProtocol(CoinJoinOption option) =>
		option switch
		{
			CoinJoinOption.WabiSabi => CoinJoinProtocol.WabiSabi,
			CoinJoinOption.ZeroLink => CoinJoinProtocol.ZeroLink,
			_ => null
		};

	/// <summary>
	/// Tells which kind the box is set to. A coinjoin that is under way has the box, whatever was asked for: the wallet
	/// has one coinjoin at a time, and the box is where it is watched and stopped. Without one, the box is set to what
	/// was asked for, and stays as it is if nothing was.
	/// </summary>
	/// <param name="current">The kind that the box is set to.</param>
	/// <param name="requested">The kind that the user picked in the menu, if this is about a pick.</param>
	/// <param name="underWay">The kind of the coinjoin that is under way, if one is.</param>
	public static CoinJoinOption ChooseOption(CoinJoinOption current, CoinJoinOption? requested, CoinJoinOption? underWay) =>
		underWay ?? requested ?? current;

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
	/// What the box says in place of the state of a coinjoin, for a kind that has none to show: what is missing, in two
	/// lines, and why there is no Play.
	/// </summary>
	/// <param name="isCoordinatorSet">Whether a coordinator is set. With one, the coinjoins of the coordinator are only missing until the wallet has processed the blocks of the node.</param>
	public static (string Notice, string Detail, string NoPlayToolTip) DescribeUnavailable(CoinJoinOption option, bool isCoordinatorSet)
	{
		if (GetProtocol(option) is null)
		{
			return (NotAvailableYetText, "", $"{GetName(option)} coinjoins are not available yet.");
		}

		if (!isCoordinatorSet)
		{
			return (NoCoordinatorText, NoCoordinatorDetail, "Set a coordinator in the Coordinator Settings first.");
		}

		return (NotLoadedText, NotLoadedDetail, "The wallet is still processing the blocks of the node.");
	}

	private void CreateStates(WalletCoinjoinModel coinjoin)
	{
		foreach (var option in AllOptions)
		{
			if (GetProtocol(option) is not { } protocol)
			{
				continue;
			}

			var state = new CoinJoinStateViewModel(UiContext, _walletModel, coinjoin, protocol, () => StartRoundAsync(protocol));

			_states[option] = state;

			// Stop shows from the moment that the wallet has started a coinjoin until it has stopped it.
			state.WhenAnyValue(x => x.StopVisible)
				.Subscribe(_ => OnCoinJoinStartedOrStopped());
		}

		Refresh();
	}

	/// <summary>The kind of the coinjoin that is under way, if one is.</summary>
	private CoinJoinOption? GetOptionUnderWay()
	{
		foreach (var (option, state) in _states)
		{
			if (state.StopVisible)
			{
				return option;
			}
		}

		return null;
	}

	/// <summary>
	/// A coinjoin takes the box when it starts, whoever started it: a round that a payment interrupted starts again by
	/// itself when the payment is done with.
	/// </summary>
	private void OnCoinJoinStartedOrStopped()
	{
		var underWay = GetOptionUnderWay();

		IsCoinJoinUnderWay = underWay is not null;
		SelectedOption = ChooseOption(SelectedOption, requested: null, underWay);
	}

	private void Select(CoinJoinOption option)
	{
		SelectedOption = ChooseOption(SelectedOption, option, GetOptionUnderWay());
	}

	/// <summary>Shows the kind that the box is set to.</summary>
	private void Refresh()
	{
		var option = SelectedOption;

		foreach (var item in Options)
		{
			item.IsSelected = item.Option == option;
		}

		Name = GetName(option);
		State = _states.GetValueOrDefault(option);

		(Notice, NoticeDetail, NoPlayToolTip) = State is null
			? DescribeUnavailable(option, IsCoinJoinEnabled)
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

/// <summary>One of the kinds of coinjoin in the menu of the coinjoin box.</summary>
public partial class CoinJoinOptionViewModel : ReactiveObject
{
	[AutoNotify(SetterModifier = AccessModifier.Internal)] private bool _isSelected;

	/// <param name="selectCommand">Sets the box to this kind.</param>
	public CoinJoinOptionViewModel(CoinJoinOption option, ICommand selectCommand)
	{
		Option = option;
		Name = CoinJoinBoxViewModel.GetName(option);
		SelectCommand = selectCommand;
	}

	public CoinJoinOption Option { get; }

	/// <summary>What the menu calls the kind.</summary>
	public string Name { get; }

	/// <summary>Sets the box to this kind. Not to be had while a coinjoin of another kind is under way.</summary>
	public ICommand SelectCommand { get; }
}
