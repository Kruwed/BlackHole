using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the WabiSabi coinjoins: the coinjoin that is running or waiting with its controls, and the coins, of
/// which the ticked ones are submitted into rounds. Nothing of this shows anywhere else.
/// </summary>
[AppLifetime]
public partial class WabiSabiTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "WabiSabi";

	/// <summary>
	/// The coins that the user ticked or unticked and that the wallet was not told about yet, with whether each is kept
	/// out of coinjoins. Ticking all of them changes many coins at once, and the wallet is told about them together.
	/// </summary>
	private readonly Dictionary<OutPoint, bool> _pendingExclusions = [];

	/// <summary>Whether a tick is being set to what the wallet says. Such a change is not the user's.</summary>
	private bool _isShowingWalletState;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isCoinjoining;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private CoinJoinStateViewModel? _coinJoinState;
	[AutoNotify] private bool _hasSelection;

	public WabiSabiTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		WalletModel = walletModel;

		CoinList = new CoinListViewModel(uiContext, walletModel.Coins, [], allowCoinjoiningCoinSelection: true, allowSelection: true);

		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// The coinjoin controls come with the loaded wallet: they start coinjoins on their own, and what the wallet
		// knows about its coins is not complete until it has processed the blocks of the node.
		this.WhenAnyValue(x => x.IsWalletLoaded)
			.Where(isLoaded => isLoaded && CoinJoinState is null && walletModel.IsCoinJoinEnabled)
			.Subscribe(_ => CoinJoinState = new CoinJoinStateViewModel(uiContext, walletModel, wallet, walletModel.Coinjoin!));

		// The settings only decide how the wallet coinjoins later, so they are open while the wallet still processes blocks.
		CoordinatorSettingsCommand = ReactiveCommand.CreateFromTask(async () =>
		{
			if (UiContext.MainViewModel is { } mainViewModel)
			{
				await mainViewModel.SettingsPage.ActivateCoordinatorTabAsync();
			}
		});

		// The coins of a round are settled once it is under way. What may take part is decided between rounds.
		walletModel.Coinjoin?.WhenAnyValue(x => x.IsCoinjoining)
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.IsCoinjoining);

		ToggleSelectionCommand = ReactiveCommand.Create(
			() => SelectAll(!CoinList.Selection.Any()),
			this.WhenAnyValue(x => x.IsCoinjoining, isCoinjoining => !isCoinjoining));

		ShowWhatMayBeSubmitted();
		TellTheWalletWhatTheUserTicks();
	}

	public IWalletModel WalletModel { get; }

	/// <summary>Whether a coordinator is set. Without one there is no coinjoin, and the tab says so.</summary>
	public bool IsCoinJoinEnabled => WalletModel.IsCoinJoinEnabled;

	/// <summary>
	/// The coins of the wallet. A round gets every ticked coin that it accepts and that can be registered at that time,
	/// the others are kept out of coinjoins. A coin is ticked unless the user unticked it, so a coin that arrives is
	/// submitted too.
	/// </summary>
	public CoinListViewModel CoinList { get; }

	/// <summary>Opens the settings of the coinjoins: the coordinator and the limits that the wallet keeps to.</summary>
	public ICommand CoordinatorSettingsCommand { get; }

	/// <summary>Ticks every coin, or none if some are ticked.</summary>
	public ICommand ToggleSelectionCommand { get; }

	/// <summary>
	/// Tells what the wallet is to be told after the user changed the tick of a coin: nothing, if the wallet treats the
	/// coin that way already.
	/// </summary>
	/// <param name="isTicked">Whether the coin may be submitted into rounds according to its tick.</param>
	/// <param name="isExcluded">Whether the wallet keeps the coin out of coinjoins so far.</param>
	/// <returns>Whether the coin is to be kept out from now on, or null if nothing changes.</returns>
	public static bool? GetExclusionChange(bool isTicked, bool isExcluded) =>
		isTicked == isExcluded ? !isTicked : null;

	/// <summary>
	/// A coin is shown ticked for as long as the wallet does not keep it out of coinjoins: when its row appears, and
	/// whenever the wallet changes its mind about it, which the daemon's interface can make it do as well.
	/// </summary>
	private void ShowWhatMayBeSubmitted()
	{
		CoinList.CoinItems
			.ToObservableChangeSet()
			.MergeMany(item => item.WhenAnyValue(x => x.IsExcludedFromCoinJoin).Select(_ => item))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(item =>
			{
				_isShowingWalletState = true;

				try
				{
					item.IsSelected = !item.IsExcludedFromCoinJoin;
				}
				finally
				{
					_isShowingWalletState = false;
				}
			});

		CoinList.Selection
			.ToObservableChangeSet()
			.ToCollection()
			.Select(selection => selection.Count > 0)
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.HasSelection);
	}

	/// <summary>
	/// The wallet is told about the ticks that the user changes, and about nothing else: not about the ticks that show
	/// what the wallet says, and not about the ones that the list restores when it puts its rows together again. A coin
	/// that the user did not touch is never kept out by this page.
	/// </summary>
	private void TellTheWalletWhatTheUserTicks()
	{
		CoinList.CoinItems
			.ToObservableChangeSet()
			.WhenPropertyChanged(x => x.IsSelected, notifyOnInitialValue: false)
			.Where(_ => !_isShowingWalletState && !CoinList.IsRebuilding)
			.Select(change => change.Sender)
			.Do(item =>
			{
				if (GetExclusionChange(isTicked: item.IsSelected, item.Coin.IsExcludedFromCoinJoin) is { } exclude)
				{
					_pendingExclusions[item.Coin.GetSmartCoin().Outpoint] = exclude;
				}
				else
				{
					_pendingExclusions.Remove(item.Coin.GetSmartCoin().Outpoint);
				}
			})
			.Throttle(TimeSpan.FromMilliseconds(100), RxApp.MainThreadScheduler)
			.DoAsync(async _ => await ApplyPendingExclusionsAsync())
			.Subscribe();
	}

	private async Task ApplyPendingExclusionsAsync()
	{
		if (_pendingExclusions.Count == 0)
		{
			return;
		}

		var exclusions = new Dictionary<OutPoint, bool>(_pendingExclusions);
		_pendingExclusions.Clear();

		try
		{
			await WalletModel.Coins.ExcludeFromCoinjoinAsync(exclusions);
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private void SelectAll(bool value)
	{
		foreach (var coin in CoinList.CoinItems)
		{
			coin.IsSelected = value;
		}
	}
}
