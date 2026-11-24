using System.Collections.Immutable;
using System.Globalization;
using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>What a taker has set on its side of the market: the filters, the makers that are picked, and how the book is sorted.</summary>
public sealed record TakerDeskState
{
	/// <summary>The column that the book is sorted by.</summary>
	public BookCriterion SortKey { get; init; } = BookCriterion.FeeRate;

	public bool SortAscending { get; init; } = true;

	/// <summary>The position of every filter, in the order of <see cref="BookRules.Criteria"/>.</summary>
	public ImmutableArray<int> Ticks { get; init; } = [0, 0, 0, 0, 0, 0];

	/// <summary>The limits that were typed or worked out, where they are not the value of a maker that a position stands for.</summary>
	public ImmutableDictionary<BookCriterion, decimal> ExactCutoffs { get; init; } = ImmutableDictionary<BookCriterion, decimal>.Empty;

	/// <summary>The makers that are picked, by their numbers.</summary>
	public ImmutableHashSet<long> Selected { get; init; } = [];

	/// <summary>Whether the pick follows the filters: every maker that passes is picked, and one that does not is not.</summary>
	public bool FollowsFilters { get; init; }

	/// <summary>The point of the strategy triangle that the filters were set from. Null once a filter was moved by hand.</summary>
	public TriangleWeights? Strategy { get; init; }
}

/// <summary>A row of the order book as a taker sees it.</summary>
/// <param name="IsAvailable">Whether the maker can be picked: it passes the filters, or it is picked already. The others are greyed out.</param>
/// <param name="Fails">For every criterion, in the order of <see cref="BookRules.Criteria"/>: whether the maker fails the filter of it. Only told for a maker that is not available.</param>
public sealed record BookRow(BookMaker Maker, bool IsSelected, bool IsAvailable, ImmutableArray<bool> Fails);

/// <summary>A filter of the book as it shows.</summary>
/// <param name="Tick">The position of the filter. Zero cuts nothing.</param>
/// <param name="MaxTick">The last position, which keeps the best value of the book alone.</param>
/// <param name="Cutoff">The limit of the filter. Null if it cuts nothing.</param>
/// <param name="BestText">The best value of the book, with its unit.</param>
/// <param name="CutoffText">The limit with its direction and its unit: "≤ 0.9 sat/vB". For a filter that cuts nothing, the worst value of the book.</param>
/// <param name="ValueText">The limit as it stands in the box that takes a typed one.</param>
public sealed record FilterRow(BookCriterion Criterion, int Tick, int MaxTick, decimal? Cutoff, string BestText, string CutoffText, string ValueText)
{
	/// <summary>Where the slider of the filter stands. It runs from strict to loose: the far end cuts nothing.</summary>
	public int SliderPosition => MaxTick - Tick;
}

/// <summary>Whether none, some or all of the makers that can be picked are picked.</summary>
public enum SelectAllState
{
	None,
	Some,
	All
}

/// <summary>What the taker's side of the market shows for a state.</summary>
/// <param name="Bonded">The makers with a fidelity bond, in the order of the table.</param>
/// <param name="Unbonded">The makers without one. They are listed below all the others.</param>
/// <param name="Filters">The filters, from top to bottom.</param>
/// <param name="AvailableCount">How many makers can be picked.</param>
/// <param name="BookCount">How many makers the book has, the offers of this wallet not counted.</param>
/// <param name="AvailableLiquidity">What the makers that can be picked offer together, in sats.</param>
/// <param name="BookLiquidity">What the makers of the book offer together, in sats.</param>
public sealed record TakerDeskView(
	IReadOnlyList<BookRow> Bonded,
	IReadOnlyList<BookRow> Unbonded,
	IReadOnlyList<FilterRow> Filters,
	int AvailableCount,
	int BookCount,
	long AvailableLiquidity,
	long BookLiquidity,
	SelectAllState SelectAll)
{
	/// <summary>"48/250 Makers".</summary>
	public string CountText => string.Create(CultureInfo.InvariantCulture, $"{AvailableCount}/{BookCount} Makers");

	/// <summary>"76.79/418.91 BTC".</summary>
	public string LiquidityText => $"{MarketFormat.Btc(AvailableLiquidity, 2)}/{MarketFormat.Btc(BookLiquidity, 2)} BTC";
}

/// <summary>What a taker brings to a transaction and who it makes it with.</summary>
/// <param name="Makers">The picked makers in the order in which the planner goes through them: by their names.</param>
/// <param name="Taker">What the taker brings. Null if it has nothing to bring yet.</param>
/// <param name="Liquidity">What the picked makers offer together, in sats.</param>
public sealed record TakerSelection(IReadOnlyList<BookMaker> Makers, PlannedTaker? Taker, long Liquidity);

/// <summary>
/// The taker's side of the market over one order book: what moving a filter, the strategy triangle or a tick of a row
/// makes of the state, and what a state shows.
/// </summary>
/// <remarks>
/// The offers of this wallet show in the book too, but they are not what the book is measured by: the filters, their
/// positions and the triangle go by the offers of the others. An offer of this wallet that is picked is not a maker of
/// the transaction. Its coins are what the taker brings.
/// </remarks>
public sealed class TakerDesk
{
	private readonly decimal[][] _sortedValues;
	private readonly Dictionary<long, double> _scores = [];
	private readonly Dictionary<long, int> _ranks = [];

	/// <param name="book">The offers of the other makers.</param>
	/// <param name="own">The offers of this wallet.</param>
	public TakerDesk(IReadOnlyList<BookMaker> book, IReadOnlyList<BookMaker> own)
	{
		Book = book;
		Own = own;
		All = [.. book, .. own];
		_sortedValues = [.. BookRules.Criteria.Select(criterion => BookRules.SortValues(book, criterion))];

		var standings = BookRules.GetStandings(book);

		for (var i = 0; i < book.Count; i++)
		{
			_scores[book[i].Id] = BookRules.GetScore(standings[i]);
		}

		foreach (var maker in own)
		{
			_scores[maker.Id] = BookRules.GetScore(BookRules.GetStandingsAgainst(maker, _sortedValues));
		}

		var place = 1;

		foreach (var maker in All.OrderByDescending(maker => _scores[maker.Id]).ThenBy(maker => maker.Id))
		{
			_ranks[maker.Id] = place++;
		}
	}

	public IReadOnlyList<BookMaker> Book { get; }

	public IReadOnlyList<BookMaker> Own { get; }

	/// <summary>The offers of the book and those of this wallet.</summary>
	public IReadOnlyList<BookMaker> All { get; }

	/// <summary>The last position of a filter: every position drops one maker more, and the last one keeps the best value alone.</summary>
	public int MaxTick => Math.Max(1, Book.Count - 1);

	/// <summary>The state that the screen opens with: the middle of the strategy triangle, nothing picked, sorted by the fee rate.</summary>
	public TakerDeskState Open() => ApplyStrategy(new TakerDeskState(), TriangleWeights.Centre);

	/// <summary>The limit of every filter, in the order of <see cref="BookRules.Criteria"/>. Null where a filter cuts nothing.</summary>
	public decimal?[] GetCutoffs(TakerDeskState state) =>
		[.. BookRules.Criteria.Select(criterion =>
			state.ExactCutoffs.TryGetValue(criterion, out var exact)
				? exact
				: BookRules.GetCutoff(_sortedValues[BookRules.IndexOf(criterion)], state.Ticks[BookRules.IndexOf(criterion)]))];

	/// <summary>Sets the filters from a point of the strategy triangle. The filter of the liquidity stays as it is.</summary>
	public TakerDeskState ApplyStrategy(TakerDeskState state, TriangleWeights weights)
	{
		var filters = TakerStrategy.GetFilters(Book, weights, state.Ticks);
		var exact = ImmutableDictionary<BookCriterion, decimal>.Empty;

		// The limit of the spread is worked out, and need not be the value of any maker. The liquidity keeps a limit that was typed.
		if (filters.Cutoffs[BookRules.IndexOf(BookCriterion.Spread)] is { } spread)
		{
			exact = exact.Add(BookCriterion.Spread, spread);
		}

		if (state.ExactCutoffs.TryGetValue(BookCriterion.Liquidity, out var liquidity))
		{
			exact = exact.Add(BookCriterion.Liquidity, liquidity);
		}

		return ApplyFilters(state with { Strategy = weights, Ticks = [.. filters.Ticks], ExactCutoffs = exact });
	}

	/// <summary>Moves a filter to a position by hand. The marker of the strategy triangle lets go.</summary>
	public TakerDeskState SetTick(TakerDeskState state, BookCriterion criterion, int tick) =>
		ApplyFilters(state with
		{
			Strategy = null,
			Ticks = state.Ticks.SetItem(BookRules.IndexOf(criterion), Math.Clamp(tick, 0, MaxTick)),
			ExactCutoffs = state.ExactCutoffs.Remove(criterion)
		});

	/// <summary>
	/// Sets the limit of a filter to a typed value: the value is the limit, and the filter stands at the tightest
	/// position that still admits it. No value sets the filter back to cutting nothing.
	/// </summary>
	public TakerDeskState SetCutoff(TakerDeskState state, BookCriterion criterion, decimal? value)
	{
		var index = BookRules.IndexOf(criterion);

		return value is { } exact
			? ApplyFilters(state with
			{
				Strategy = null,
				Ticks = state.Ticks.SetItem(index, BookRules.GetTick(_sortedValues[index], BookRules.IsLowerBetter(criterion), exact)),
				ExactCutoffs = state.ExactCutoffs.SetItem(criterion, exact)
			})
			: ApplyFilters(state with
			{
				Strategy = null,
				Ticks = state.Ticks.SetItem(index, 0),
				ExactCutoffs = state.ExactCutoffs.Remove(criterion)
			});
	}

	/// <summary>Picks a maker, or lets it go. A maker that does not pass the filters cannot be picked. The pick no longer follows the filters.</summary>
	public TakerDeskState Toggle(TakerDeskState state, long id)
	{
		if (All.FirstOrDefault(maker => maker.Id == id) is not { } maker)
		{
			return state;
		}

		if (state.Selected.Contains(id))
		{
			return state with { Selected = state.Selected.Remove(id), FollowsFilters = false };
		}

		return BookRules.Passes(maker, GetCutoffs(state))
			? state with { Selected = state.Selected.Add(id), FollowsFilters = false }
			: state;
	}

	/// <summary>
	/// Picks every maker that passes the filters, and from then on the pick follows the filters. With all of them
	/// picked already, nothing is picked any more.
	/// </summary>
	public TakerDeskState ToggleAll(TakerDeskState state)
	{
		var cutoffs = GetCutoffs(state);
		var selectable = All.Where(maker => BookRules.Passes(maker, cutoffs)).Select(maker => maker.Id).ToArray();

		return GetSelectAllState(state, selectable) == SelectAllState.All
			? state with { Selected = [], FollowsFilters = false }
			: state with { Selected = state.Selected.Union(selectable), FollowsFilters = true };
	}

	/// <summary>
	/// Sorts the book by a column. The first time by what is better for the taker first, and the other way round the
	/// next time.
	/// </summary>
	public TakerDeskState SortBy(TakerDeskState state, BookCriterion criterion) =>
		state.SortKey == criterion
			? state with { SortAscending = !state.SortAscending }
			: state with { SortKey = criterion, SortAscending = BookRules.IsLowerBetter(criterion) };

	/// <summary>
	/// Carries a state over from the desk of an earlier book to this one. A strategy sets the filters anew on this
	/// book. Filters that were set by hand keep the limits that they had, and makers that are gone are not picked any more.
	/// </summary>
	public TakerDeskState Rebase(TakerDesk earlier, TakerDeskState state)
	{
		if (state.Strategy is { } weights)
		{
			return ApplyStrategy(state, weights);
		}

		var cutoffs = earlier.GetCutoffs(state);
		var ticks = state.Ticks;
		var exact = ImmutableDictionary<BookCriterion, decimal>.Empty;

		foreach (var criterion in BookRules.Criteria)
		{
			var index = BookRules.IndexOf(criterion);

			if (cutoffs[index] is { } cutoff)
			{
				ticks = ticks.SetItem(index, BookRules.GetTick(_sortedValues[index], BookRules.IsLowerBetter(criterion), cutoff));
				exact = exact.Add(criterion, cutoff);
			}
			else
			{
				ticks = ticks.SetItem(index, 0);
			}
		}

		return ApplyFilters(state with { Ticks = ticks, ExactCutoffs = exact });
	}

	/// <summary>What the state shows: the rows of the book in the order of the table, the filters, and the counts below them.</summary>
	public TakerDeskView GetView(TakerDeskState state)
	{
		var cutoffs = GetCutoffs(state);
		var direction = state.SortAscending ? 1 : -1;

		// Makers with a bond come first, and among those of a tier the ones that can be picked. Then the chosen column decides, then the rank.
		var rows = All
			.Select(maker =>
			{
				var passes = BookRules.Passes(maker, cutoffs);
				var isSelected = state.Selected.Contains(maker.Id);
				var isAvailable = isSelected || passes;

				return (
					Row: new BookRow(
						maker,
						isSelected,
						isAvailable,
						[.. BookRules.Criteria.Select(criterion => !isAvailable && !BookRules.Passes(criterion, maker.GetValue(criterion), cutoffs[BookRules.IndexOf(criterion)]))]),
					Passes: passes);
			})
			.OrderBy(row => row.Row.Maker.IsBonded ? 0 : 1)
			.ThenBy(row => row.Row.IsAvailable ? 0 : 1)
			.ThenBy(row => direction * row.Row.Maker.GetValue(state.SortKey))
			.ThenBy(row => _ranks[row.Row.Maker.Id])
			.ToArray();

		var available = rows.Where(row => row.Row.IsAvailable).ToArray();
		var selectable = rows.Where(row => row.Passes).Select(row => row.Row.Maker.Id).ToArray();

		return new TakerDeskView(
			[.. rows.Where(row => row.Row.Maker.IsBonded).Select(row => row.Row)],
			[.. rows.Where(row => !row.Row.Maker.IsBonded).Select(row => row.Row)],
			[.. BookRules.FilterOrder.Select(criterion => GetFilterRow(state, criterion, cutoffs[BookRules.IndexOf(criterion)]))],
			available.Length,
			Book.Count,
			available.Sum(row => row.Row.Maker.Liquidity),
			Book.Sum(maker => maker.Liquidity),
			GetSelectAllState(state, selectable));
	}

	/// <summary>
	/// Who the transaction of the state is made with, and what the taker brings to it. Picked offers of this wallet are
	/// not makers of the transaction: their coins are what the taker brings then, in place of the coins that it would
	/// bring otherwise.
	/// </summary>
	/// <param name="taker">What the taker brings unless it picked offers of its own. Null if that is nothing.</param>
	public TakerSelection GetSelection(TakerDeskState state, PlannedTaker? taker)
	{
		var picked = All
			.Where(maker => state.Selected.Contains(maker.Id))
			.OrderBy(maker => maker.Alias, StringComparer.Ordinal)
			.ThenBy(maker => maker.Id)
			.ToArray();

		var own = picked.Where(maker => maker.IsOwn).ToArray();
		var makers = picked.Where(maker => !maker.IsOwn).ToArray();

		if (own.Length > 0)
		{
			taker = new PlannedTaker(own.Sum(maker => maker.InputCount), own.Sum(maker => maker.Liquidity));
		}

		return new TakerSelection(makers, taker, makers.Sum(maker => maker.Liquidity));
	}

	/// <summary>After the filters changed: makers that are picked and do not pass any more are let go, and a pick that follows the filters takes in the ones that pass now.</summary>
	private TakerDeskState ApplyFilters(TakerDeskState state)
	{
		var cutoffs = GetCutoffs(state);

		return state with
		{
			Selected = [.. All
				.Where(maker => (state.Selected.Contains(maker.Id) || state.FollowsFilters) && BookRules.Passes(maker, cutoffs))
				.Select(maker => maker.Id)]
		};
	}

	private static SelectAllState GetSelectAllState(TakerDeskState state, IReadOnlyCollection<long> selectable) =>
		state.Selected.Count == 0
			? SelectAllState.None
			: selectable.Count > 0 && selectable.All(state.Selected.Contains)
				? SelectAllState.All
				: SelectAllState.Some;

	private FilterRow GetFilterRow(TakerDeskState state, BookCriterion criterion, decimal? cutoff)
	{
		var index = BookRules.IndexOf(criterion);
		var values = _sortedValues[index];
		var sign = BookRules.IsLowerBetter(criterion) ? "≤" : "≥";

		// A filter that cuts nothing stands at the worst value of the book.
		var shown = cutoff ?? (values.Length > 0 ? values[^1] : 0m);

		return new FilterRow(
			criterion,
			state.Ticks[index],
			MaxTick,
			cutoff,
			values.Length > 0 ? MarketFormat.Criterion(criterion, values[0]) : "",
			$"{sign} {MarketFormat.Criterion(criterion, shown)}",
			values.Length > 0 || cutoff is not null ? MarketFormat.Plain(shown) : "");
	}
}
