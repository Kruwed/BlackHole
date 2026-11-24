using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// The taker's side of the market, which is the main screen of the Taker tab of the Coinjoin dialog: the strategy
/// triangle and the six filters on the left, the order book in the middle, and on the right the transaction that the
/// picked makers make with the taker, as the Transaction Builder and the Price Calculator tell it.
/// </summary>
/// <remarks>
/// Everything that is worked out here is worked out by <see cref="TakerDesk"/> and by the planner of the transaction.
/// The planner runs off the thread of the window: until it is done, the screen shows the plan that it had.
/// </remarks>
public partial class TakerScreenViewModel : ReactiveObject
{
	private readonly IMarket _market;
	private readonly Func<TakerSelection, PlanReview?> _plan;
	private readonly Dictionary<BookCriterion, BarRowViewModel> _filterRows = [];

	private TakerSelection? _planned;
	private TakerSelection? _waiting;
	private Task _planning = Task.CompletedTask;

	private TakerDesk _desk;
	private TakerDeskState _state;

	[AutoNotify] private TriangleWeights? _strategy;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _strategyText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<object> _rows = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _countText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _liquidityText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAllSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSomeSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAnySelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private BookCriterion _sortKey;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _sortAscending;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private PlanReview? _review;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasSelection;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isPlanning;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _bookNotice;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _takerNotice = "";
	[AutoNotify] private int _selectedPanel;

	/// <param name="plan">What makes the plan of a selection. It is run away from the thread of the screen, one plan at a time.</param>
	public TakerScreenViewModel(IMarket market, Func<TakerSelection, PlanReview?>? plan = null)
	{
		_market = market;
		_plan = plan ?? CreateReview;
		_desk = new TakerDesk(market.Book, LiveMarketModel.GetOwnMakers(market));
		_state = _desk.Open();

		Filters = [.. BookRules.FilterOrder.Select(criterion =>
		{
			var row = new BarRowViewModel(
				MarketFormat.Name(criterion),
				moved: position => Apply(_desk.SetTick(_state, criterion, _desk.MaxTick - (int)Math.Round(position))),
				typed: text => TypeCutoff(criterion, text));

			_filterRows[criterion] = row;
			return row;
		})];

		ToggleCommand = ReactiveCommand.Create<long>(id => Apply(_desk.Toggle(_state, id)));
		ToggleAllCommand = ReactiveCommand.Create(() => Apply(_desk.ToggleAll(_state)));
		SortCommand = ReactiveCommand.Create<BookCriterion>(criterion => Apply(_desk.SortBy(_state, criterion)));
		ShowBuilderCommand = ReactiveCommand.Create(() => { SelectedPanel = 0; });
		ShowCalculatorCommand = ReactiveCommand.Create(() => { SelectedPanel = 1; });

		// Nothing can be signed: how a taker reaches the makers that it picked is not part of the design yet.
		SignCommand = ReactiveCommand.Create(() => { }, Observable.Return(false));

		this.WhenAnyValue(x => x.SelectedPanel)
			.Subscribe(_ =>
			{
				this.RaisePropertyChanged(nameof(IsBuilderShown));
				this.RaisePropertyChanged(nameof(IsCalculatorShown));
			});

		// The triangle tells where its marker was put.
		this.WhenAnyValue(x => x.Strategy)
			.Skip(1)
			.Where(weights => weights is not null && weights != _state.Strategy)
			.Subscribe(weights => Apply(_desk.ApplyStrategy(_state, weights!.Value)));

		Refresh();
	}

	/// <summary>The six filters, from top to bottom: Liquidity, Bond, Fee rate, Spread, Sell rate, Buy rate.</summary>
	public IReadOnlyList<BarRowViewModel> Filters { get; }

	/// <summary>The columns of the book that can be sorted by, from left to right.</summary>
	public IReadOnlyList<BookCriterion> Columns => BookRules.ColumnOrder;

	/// <summary>Picks a maker by its number, or lets it go.</summary>
	public ICommand ToggleCommand { get; }

	/// <summary>Picks every maker that passes the filters, or none if all of them are picked.</summary>
	public ICommand ToggleAllCommand { get; }

	/// <summary>Sorts the book by a column.</summary>
	public ICommand SortCommand { get; }

	public ICommand ShowBuilderCommand { get; }

	public ICommand ShowCalculatorCommand { get; }

	/// <summary>Signs and sends the planned transaction. It cannot be pressed yet.</summary>
	public ICommand SignCommand { get; }

	/// <summary>Why the planned transaction cannot be signed and sent.</summary>
	public string SignNotice => _market.SignNotice;

	public bool IsSample => _market.IsSample;

	public bool IsBuilderShown => SelectedPanel == 0;

	public bool IsCalculatorShown => SelectedPanel == 1;

	public string TopName => TakerStrategy.TopName;

	public string LeftName => TakerStrategy.LeftName;

	public string RightName => TakerStrategy.RightName;

	/// <summary>Ends when no plan is being made and none waits to be made.</summary>
	public Task Planning => _planning;

	/// <summary>
	/// The plan of the transaction that a selection makes, as the screen shows it. Null without makers, and null while
	/// the taker has nothing to bring: there is nothing to plan then.
	/// </summary>
	public static PlanReview? CreateReview(TakerSelection selection)
	{
		if (selection.Taker is not { } taker)
		{
			return null;
		}

		try
		{
			var plan = TransactionPlanner.Plan(taker, [.. selection.Makers.Select(maker => maker.ToPlannedMaker())]);

			return PlanReview.Create(plan, selection.Liquidity);
		}
		catch (Exception ex)
		{
			// A plan that cannot be made is not a reason to lose the screen. It is told in the log.
			Logger.LogError(ex);
			return null;
		}
	}

	/// <summary>Follows the market for as long as the screen shows: the book, and the coins that the taker brings.</summary>
	public void Activate(CompositeDisposable disposables)
	{
		_market.Changed
			.Subscribe(_ => Rebase())
			.DisposeWith(disposables);

		Rebase();
	}

	/// <summary>Takes the book and the offers of the wallet as they are now, and carries over what was set.</summary>
	private void Rebase()
	{
		var desk = new TakerDesk(_market.Book, LiveMarketModel.GetOwnMakers(_market));

		_state = desk.Rebase(_desk, _state);
		_desk = desk;
		Refresh();
	}

	/// <summary>A typed limit is the limit of its filter. No text sets the filter back to cutting nothing, and what is no number changes nothing.</summary>
	private void TypeCutoff(BookCriterion criterion, string text)
	{
		if (text.Length == 0)
		{
			Apply(_desk.SetCutoff(_state, criterion, null));
		}
		else if ((criterion == BookCriterion.Spread ? MarketFormat.ParseSignedNumber(text) : MarketFormat.ParseNumber(text)) is { } value)
		{
			// The spread of an offer without points is below zero, so the limit of the spread can be.
			Apply(_desk.SetCutoff(_state, criterion, value));
		}
		else
		{
			Refresh();
		}
	}

	private void Apply(TakerDeskState state)
	{
		_state = state;
		Refresh();
	}

	/// <summary>Shows what the state has to show, and asks for the plan of its selection.</summary>
	private void Refresh()
	{
		var view = _desk.GetView(_state);

		ShowRows(view);

		// Without makers there is nothing to filter.
		var hasBook = _desk.Book.Count > 0;

		foreach (var filter in view.Filters)
		{
			if (hasBook)
			{
				_filterRows[filter.Criterion].Show(filter.SliderPosition, filter.MaxTick, filter.BestText, filter.CutoffText, filter.ValueText);
			}
			else
			{
				_filterRows[filter.Criterion].Show(filter.MaxTick, filter.MaxTick, "", MarketFormat.None, "", isEnabled: false);
			}
		}

		CountText = view.CountText;
		LiquidityText = view.LiquidityText;
		IsAllSelected = view.SelectAll == SelectAllState.All;
		IsSomeSelected = view.SelectAll == SelectAllState.Some;
		IsAnySelected = view.SelectAll != SelectAllState.None;
		SortKey = _state.SortKey;
		SortAscending = _state.SortAscending;
		Strategy = _state.Strategy;
		StrategyText = _state.Strategy is { } weights ? StrategyTriangle.Describe(weights, "Privacy", "Costs", "Speed") : "";
		BookNotice = _market.BookNotice;
		TakerNotice = _market.TakerNotice;

		var selection = _desk.GetSelection(_state, _market.Taker);

		HasSelection = selection.Makers.Count > 0;
		RequestPlan(selection);
	}

	/// <summary>
	/// Shows the makers of the book. While the table has the same makers in the same order, its rows are told what
	/// changed and stay where they are: a table that is made again starts at its top again, under the hand of whoever
	/// ticked a maker further down.
	/// </summary>
	private void ShowRows(TakerDeskView view)
	{
		var items = new List<object>(view.Bonded.Count + view.Unbonded.Count + 1);

		items.AddRange(view.Bonded);

		if (view.Unbonded.Count > 0)
		{
			items.Add(DescribeUnbonded(view.Unbonded.Count));
			items.AddRange(view.Unbonded);
		}

		var shown = Rows;
		var isSame = shown.Count == items.Count;

		for (var i = 0; isSame && i < items.Count; i++)
		{
			isSame = (items[i], shown[i]) switch
			{
				(BookRow row, BookRowViewModel rowViewModel) => rowViewModel.Maker.Equals(row.Maker),
				(string caption, BookCaptionViewModel captionViewModel) => captionViewModel.Text == caption,
				_ => false
			};
		}

		if (isSame)
		{
			for (var i = 0; i < items.Count; i++)
			{
				if (items[i] is BookRow row)
				{
					((BookRowViewModel)shown[i]).Update(row);
				}
			}

			return;
		}

		Rows = [.. items.Select(item => item is BookRow row ? (object)new BookRowViewModel(row, ToggleCommand) : new BookCaptionViewModel((string)item))];
	}

	/// <summary>The caption above the makers without a bond: "12 Unbonded Makers".</summary>
	public static string DescribeUnbonded(int count) => $"{MarketFormat.Whole(count)} Unbonded {(count == 1 ? "Maker" : "Makers")}";

	/// <summary>
	/// Asks for the plan of a selection. A selection that is planned already is not planned again. Plans are made one
	/// at a time, away from the thread of the screen, and when several were asked for meanwhile, only the last of them
	/// is made: a plan of the whole book takes a moment, and the ones that a moving slider asks for on its way are of
	/// no use to anybody.
	/// </summary>
	private void RequestPlan(TakerSelection selection)
	{
		if (_planned is { } planned && IsSame(planned, selection))
		{
			return;
		}

		_planned = selection;
		_waiting = selection;
		IsPlanning = true;

		if (_planning.IsCompleted)
		{
			_planning = PlanAsync();
		}
	}

	private async Task PlanAsync()
	{
		while (_waiting is { } selection)
		{
			_waiting = null;

			var review = await Task.Run(() => _plan(selection));

			// A plan that is out of date when it arrives is not shown: the next one is on its way.
			if (_waiting is null)
			{
				Review = review;
				IsPlanning = false;
			}
		}
	}

	private static bool IsSame(TakerSelection one, TakerSelection other) =>
		one.Taker == other.Taker
		&& one.Liquidity == other.Liquidity
		&& one.Makers.Count == other.Makers.Count
		&& one.Makers.SequenceEqual(other.Makers);
}
