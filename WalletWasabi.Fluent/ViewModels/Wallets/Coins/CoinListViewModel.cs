using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Controls;
using WalletWasabi.Fluent.Controls.Sorting;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Coins;

/// <summary>
/// A list of coins with one row for every coin, which the user can tick.
/// </summary>
public class CoinListViewModel : ViewModelBase, IDisposable
{
	private readonly CompositeDisposable _disposables = new();
	private readonly bool _allowCoinjoiningCoinSelection;

	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Uses DisposeWith()")]
	public CoinListViewModel(
		UiContext uiContext,
		ICoinListModel availableCoins,
		IList<CoinModel> initialCoinSelection,
		bool allowCoinjoiningCoinSelection,
		bool allowSelection = true) : base(uiContext)
	{
		_allowCoinjoiningCoinSelection = allowCoinjoiningCoinSelection;

		var viewModels = new SourceList<CoinViewModel>().DisposeWith(_disposables);

		var changes = viewModels.Connect();

		var coinItems = changes.AddKey(model => model.Coin.Key);

		coinItems.OnItemAdded(model => model.Coin.SubscribeToCoinChanges(_disposables))
			.Subscribe()
			.DisposeWith(_disposables);

		changes
			.Sort(DefaultOrder)
			.DisposeMany()
			.Bind(out var coinItemsCollection)
			.Subscribe()
			.DisposeWith(_disposables);

		CoinItems = coinItemsCollection;

		coinItems.AutoRefresh(x => x.IsSelected)
			.Filter(x => x.IsSelected)
			.Transform(x => x.Coin)
			.Bind(out var selection)
			.Subscribe()
			.DisposeWith(_disposables);

		Selection = selection;

		// Coins on the same address are ticked together.
		coinItems
			.WhenPropertyChanged(x => x.IsSelected, notifyOnInitialValue: false)
			.Subscribe(change =>
			{
				var others = CoinItems
					.Where(x => x.IsSelected != change.Value && x.Coin.IsSameAddress(change.Sender.Coin))
					.ToArray();

				foreach (var other in others)
				{
					other.IsSelected = change.Value;
				}
			})
			.DisposeWith(_disposables);

		availableCoins.List
			.Connect(suppressEmptyChangeSets: false)
			.ToCollection()
			.Do(
				coins =>
				{
					IsRebuilding = true;

					try
					{
						IList<CoinModel> oldSelection = Selection.ToArray();
						Rebuild(viewModels, coins);
						UpdateSelection(CoinItems, oldSelection);
					}
					finally
					{
						IsRebuilding = false;
					}
				})
			.Subscribe()
			.DisposeWith(_disposables);

		TreeDataGridSource = CoinListDataGridSource.Create(CoinItems, allowSelection);
		TreeDataGridSource.DisposeWith(_disposables);

		Sortables =
		[
			CreateSortable("Status", column: 0),
			CreateSortable("Anonymity Score", column: 1),
			CreateSortable("Amount", column: 2),
		];

		SetInitialSelection(initialCoinSelection);
	}

	/// <summary>
	/// The order of the rows until the user asks for another one: the coins with the highest anonymity score come first
	/// and, among coins of the same score, the largest. The last step only keeps coins that are alike in both in one order.
	/// </summary>
	public static IComparer<CoinViewModel> DefaultOrder { get; } =
		SortExpressionComparer<CoinViewModel>
			.Descending(x => x.AnonymityScore)
			.ThenByDescending(x => x.Amount.Btc)
			.ThenByAscending(x => x.Coin.Key);

	/// <summary>The rows of the list, one for every coin.</summary>
	public ReadOnlyObservableCollection<CoinViewModel> CoinItems { get; }

	/// <summary>
	/// Whether the list is putting its rows together again because the coins of the wallet changed. Ticks that change
	/// meanwhile are the list restoring what was ticked, not the user ticking.
	/// </summary>
	public bool IsRebuilding { get; private set; }

	public ReadOnlyObservableCollection<CoinModel> Selection { get; }

	public FlatTreeDataGridSource<CoinViewModel> TreeDataGridSource { get; }

	public IEnumerable<SortableItem> Sortables { get; private set; }

	public void Dispose()
	{
		_disposables.Dispose();
	}

	private SortableItem CreateSortable(string name, int column) =>
		new(name)
		{
			SortByAscendingCommand = ReactiveCommand.Create(() => SortBy(column, ListSortDirection.Ascending)),
			SortByDescendingCommand = ReactiveCommand.Create(() => SortBy(column, ListSortDirection.Descending))
		};

	private void SortBy(int column, ListSortDirection direction) =>
		((ITreeDataGridSource)TreeDataGridSource).SortBy(TreeDataGridSource.Columns[column], direction);

	private void SetInitialSelection(IEnumerable<CoinModel> initialSelection) => UpdateSelection(CoinItems, initialSelection.ToList());

	private static void UpdateSelection(IEnumerable<CoinViewModel> coinItems, IList<CoinModel> selectedCoins)
	{
		var selectedSmartCoins = selectedCoins.GetSmartCoins().ToList();

		var coinsToSelect = coinItems.Where(x => selectedSmartCoins.Contains(x.Coin.GetSmartCoin())).ToArray();

		foreach (var coinItem in coinsToSelect)
		{
			coinItem.IsSelected = true;
		}
	}

	private void Rebuild(ISourceList<CoinViewModel> source, IEnumerable<CoinModel> coins)
	{
		var newItems = coins
			.Select(coin => new CoinViewModel(UiContext, coin, _allowCoinjoiningCoinSelection))
			.ToArray();

		// A coin that changed is another row: the one in the list would go on showing the coin as it was.
		source.EditDiff(
			newItems,
			new LambdaComparer<CoinViewModel>(
				(a, b) => a?.Coin.Key == b?.Coin.Key && a?.Content == b?.Content,
				row => row?.Coin.Key ?? 0));
	}
}
