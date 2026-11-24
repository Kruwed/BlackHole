using System.Collections.Generic;
using System.Linq;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// What a taker does with the order book: it sets the filters with the triangle, with the sliders or by typing, sorts
/// the table, and ticks makers. The book of these tests is the sample book of the design, 250 makers.
/// </summary>
public class TakerDeskTests
{
	private static readonly IReadOnlyList<BookMaker> Book = SampleMarket.Makers;
	private readonly TakerDesk _desk = new(Book, []);

	/// <summary>The screen opens with the marker in the middle of the triangle. On the sample book that admits 48 makers.</summary>
	[Fact]
	public void DeskOpensAtTheCentreOfTheTriangle()
	{
		var state = _desk.Open();
		var view = _desk.GetView(state);

		Assert.Equal(TriangleWeights.Centre, state.Strategy);
		Assert.Empty(state.Selected);
		Assert.False(state.FollowsFilters);
		Assert.Equal(BookCriterion.FeeRate, state.SortKey);
		Assert.True(state.SortAscending);

		Assert.Equal(48, view.AvailableCount);
		Assert.Equal(250, view.BookCount);
		Assert.Equal("48/250 Makers", view.CountText);
		Assert.Equal("76.79/418.91 BTC", view.LiquidityText);
		Assert.Equal(SelectAllState.None, view.SelectAll);
	}

	[Theory]
	[InlineData(1, 0, 0, 66, "122.87")]
	[InlineData(0, 1, 0, 14, "26.55")]
	[InlineData(0, 0, 1, 59, "93.31")]
	public void CornerOfTheTriangleAdmitsWhatTheDesignSays(double privacy, double costs, double speed, int makers, string liquidity)
	{
		var view = _desk.GetView(_desk.ApplyStrategy(_desk.Open(), new TriangleWeights(privacy, costs, speed)));

		Assert.Equal(makers, view.AvailableCount);
		Assert.Equal($"{liquidity}/418.91 BTC", view.LiquidityText);
	}

	/// <summary>The filters, from top to bottom, with the best value of the book on the left of the bar and the limit on its right.</summary>
	[Fact]
	public void FiltersShowTheBestValueOfTheBookAndTheLimit()
	{
		var view = _desk.GetView(_desk.Open());

		Assert.Equal(
			[BookCriterion.Liquidity, BookCriterion.Bond, BookCriterion.FeeRate, BookCriterion.Spread, BookCriterion.SellRate, BookCriterion.BuyRate],
			view.Filters.Select(filter => filter.Criterion));

		// The triangle leaves the liquidity alone: its filter stands at the worst value of the book, which cuts nobody.
		var liquidity = view.Filters[0];
		Assert.Equal(0, liquidity.Tick);
		Assert.Null(liquidity.Cutoff);
		Assert.Equal(249, liquidity.MaxTick);
		Assert.Equal(249, liquidity.SliderPosition);
		Assert.Equal("4.99013785 BTC", liquidity.BestText);
		Assert.Equal("≥ 0.09156593 BTC", liquidity.CutoffText);
		Assert.Equal("0.09156593", liquidity.ValueText);

		Assert.Equal(("3.00465965 BTC", "≥ 0.26305214 BTC", 99), (view.Filters[1].BestText, view.Filters[1].CutoffText, view.Filters[1].Tick));
		Assert.Equal(("0.1 sat/vB", "≤ 0.9 sat/vB", 81), (view.Filters[2].BestText, view.Filters[2].CutoffText, view.Filters[2].Tick));
		Assert.Equal(("0.5 sats", "≤ 2.5 sats"), (view.Filters[3].BestText, view.Filters[3].CutoffText));
		Assert.Equal(("0.6 sats/AP", "≤ 3.3 sats/AP", 79), (view.Filters[4].BestText, view.Filters[4].CutoffText, view.Filters[4].Tick));
		Assert.Equal(("≥ 0.8 sats/AP", 76), (view.Filters[5].CutoffText, view.Filters[5].Tick));

		// The bar is full when the filter is loose.
		Assert.All(view.Filters, filter => Assert.Equal(filter.MaxTick - filter.Tick, filter.SliderPosition));
	}

	/// <summary>
	/// Makers with a bond come first and the others after them. Within each part the ones that can be picked come before
	/// the ones that the filters cut, and then the chosen column decides: the fee rate, the lowest first.
	/// </summary>
	[Fact]
	public void TableHasTheBondedMakersFirstAndThePickableOnesOnTop()
	{
		var view = _desk.GetView(_desk.Open());

		Assert.Equal(216, view.Bonded.Count);
		Assert.Equal(34, view.Unbonded.Count);
		Assert.All(view.Bonded, row => Assert.True(row.Maker.Bond > 0));
		Assert.All(view.Unbonded, row => Assert.Equal(0, row.Maker.Bond));

		foreach (var block in new[] { view.Bonded, view.Unbonded })
		{
			var available = block.TakeWhile(row => row.IsAvailable).ToArray();
			var cut = block.Skip(available.Length).ToArray();

			Assert.All(cut, row => Assert.False(row.IsAvailable));
			Assert.Equal(available.Select(row => row.Maker.MinFeeRate).Order(), available.Select(row => row.Maker.MinFeeRate));
			Assert.Equal(cut.Select(row => row.Maker.MinFeeRate).Order(), cut.Select(row => row.Maker.MinFeeRate));
		}

		Assert.Equal(48, view.Bonded.Count(row => row.IsAvailable) + view.Unbonded.Count(row => row.IsAvailable));

		// The first rows of the sample book, as the design shows them.
		Assert.Equal(["misty-noddy", "stark-nyala", "bold-fern"], view.Bonded.Take(3).Select(row => row.Maker.Alias));
	}

	[Fact]
	public void HeaderSortsByItsColumnInItsNaturalDirectionAndReversesOnTheSecondClick()
	{
		var state = _desk.Open();

		// Where more is better, the most comes first.
		state = _desk.SortBy(state, BookCriterion.Liquidity);
		Assert.Equal((BookCriterion.Liquidity, false), (state.SortKey, state.SortAscending));

		var available = _desk.GetView(state).Bonded.TakeWhile(row => row.IsAvailable).ToArray();
		Assert.Equal(available.Select(row => row.Maker.Liquidity).OrderDescending(), available.Select(row => row.Maker.Liquidity));

		state = _desk.SortBy(state, BookCriterion.Liquidity);
		Assert.Equal((BookCriterion.Liquidity, true), (state.SortKey, state.SortAscending));

		// Where less is better, the least comes first.
		state = _desk.SortBy(state, BookCriterion.SellRate);
		Assert.Equal((BookCriterion.SellRate, true), (state.SortKey, state.SortAscending));

		state = _desk.SortBy(state, BookCriterion.BuyRate);
		Assert.Equal((BookCriterion.BuyRate, false), (state.SortKey, state.SortAscending));

		// Sorting picks nobody and moves no filter.
		Assert.Empty(state.Selected);
		Assert.Equal(TriangleWeights.Centre, state.Strategy);
	}

	[Fact]
	public void RowThatPassesTheFiltersCanBeTickedAndARowThatDoesNotCannot()
	{
		var state = _desk.Open();
		var view = _desk.GetView(state);
		var passing = view.Bonded.First(row => row.IsAvailable).Maker;
		var cut = view.Bonded.First(row => !row.IsAvailable).Maker;

		var ticked = _desk.Toggle(state, passing.Id);
		Assert.Equal([passing.Id], ticked.Selected);
		Assert.Equal(SelectAllState.Some, _desk.GetView(ticked).SelectAll);

		Assert.Same(state, _desk.Toggle(state, cut.Id));
		Assert.Same(state, _desk.Toggle(state, -5));

		Assert.Empty(_desk.Toggle(ticked, passing.Id).Selected);
	}

	/// <summary>In a row that the filters cut, the cells that fail their filter are dimmed further. A row that can be picked has none.</summary>
	[Fact]
	public void GreyedOutRowTellsWhichOfItsCellsFail()
	{
		var state = _desk.Open();
		var view = _desk.GetView(state);
		var cutoffs = _desk.GetCutoffs(state);

		Assert.All(view.Bonded.Concat(view.Unbonded).Where(row => row.IsAvailable), row => Assert.All(row.Fails, fails => Assert.False(fails)));

		foreach (var row in view.Bonded.Concat(view.Unbonded).Where(row => !row.IsAvailable))
		{
			Assert.Contains(true, row.Fails);

			foreach (var criterion in BookRules.Criteria)
			{
				var index = BookRules.IndexOf(criterion);

				Assert.Equal(!BookRules.Passes(criterion, row.Maker.GetValue(criterion), cutoffs[index]), row.Fails[index]);
			}
		}
	}

	[Fact]
	public void MovingAFilterTakesTheMarkerOutOfTheTriangleAndUnticksWhoNoLongerPasses()
	{
		var state = _desk.Open();
		var cheapest = _desk.GetView(state).Bonded.First(row => row.IsAvailable).Maker;
		var dearest = _desk.GetView(state).Bonded.Last(row => row.IsAvailable).Maker;

		Assert.True(cheapest.MinFeeRate < dearest.MinFeeRate);

		state = _desk.Toggle(_desk.Toggle(state, cheapest.Id), dearest.Id);
		Assert.Equal(2, state.Selected.Count);

		// The strictest fee rate filter keeps the best value of the book and nothing else.
		state = _desk.SetTick(state, BookCriterion.FeeRate, _desk.MaxTick);

		Assert.Null(state.Strategy);
		Assert.Equal([cheapest.Id], state.Selected);
		Assert.Equal(Book.Min(maker => maker.MinFeeRate), _desk.GetCutoffs(state)[BookRules.IndexOf(BookCriterion.FeeRate)]);

		// A tick beyond the last is the last, and one before the first is the first.
		Assert.Equal(_desk.MaxTick, _desk.SetTick(state, BookCriterion.FeeRate, 9_999).Ticks[BookRules.IndexOf(BookCriterion.FeeRate)]);
		Assert.Equal(0, _desk.SetTick(state, BookCriterion.FeeRate, -3).Ticks[BookRules.IndexOf(BookCriterion.FeeRate)]);
	}

	/// <summary>A typed value is the limit as it was typed, and the slider goes to the tightest tick that still admits it.</summary>
	[Fact]
	public void TypedValueIsTheLimitAndClearingItIsNoLimit()
	{
		var index = BookRules.IndexOf(BookCriterion.FeeRate);
		var state = _desk.SetCutoff(_desk.Open(), BookCriterion.FeeRate, 0.55m);

		Assert.Null(state.Strategy);
		Assert.Equal(0.55m, _desk.GetCutoffs(state)[index]);

		// The box has the value as it was typed. On the bar a fee rate has one decimal, like everywhere on the screen.
		var filter = _desk.GetView(state).Filters.Single(row => row.Criterion == BookCriterion.FeeRate);
		Assert.Equal("≤ 0.6 sat/vB", filter.CutoffText);
		Assert.Equal("0.55", filter.ValueText);
		Assert.Equal(state.Ticks[index], filter.Tick);
		Assert.Equal(BookRules.GetTick(BookRules.SortValues(Book, BookCriterion.FeeRate), isLowerBetter: true, 0.55m), filter.Tick);

		var passing = Book.Count(maker => maker.MinFeeRate <= 0.55m);
		Assert.InRange(passing, 1, 249);
		Assert.All(_desk.GetView(state).Bonded.Where(row => row.IsAvailable), row => Assert.True(row.Maker.MinFeeRate <= 0.55m));

		state = _desk.SetCutoff(state, BookCriterion.FeeRate, null);

		Assert.Equal(0, state.Ticks[index]);
		Assert.Null(_desk.GetCutoffs(state)[index]);
	}

	/// <summary>
	/// The box in the head of the table ticks every maker that passes. From then on the selection follows the filters:
	/// who is newly admitted joins, who is cut leaves. Ticking a single row ends that.
	/// </summary>
	[Fact]
	public void SelectAllTicksWhoPassesAndFollowsTheFilters()
	{
		var state = _desk.ToggleAll(_desk.Open());

		Assert.Equal(48, state.Selected.Count);
		Assert.True(state.FollowsFilters);
		Assert.Equal(SelectAllState.All, _desk.GetView(state).SelectAll);

		// Another strategy admits other makers, and they are the selection.
		state = _desk.ApplyStrategy(state, new TriangleWeights(1, 0, 0));
		Assert.Equal(66, state.Selected.Count);
		Assert.Equal(SelectAllState.All, _desk.GetView(state).SelectAll);

		state = _desk.ApplyStrategy(state, new TriangleWeights(0, 1, 0));
		Assert.Equal(14, state.Selected.Count);

		// One row unticked: the rest stays, and the selection does not follow any more.
		var one = state.Selected.First();
		state = _desk.Toggle(state, one);

		Assert.False(state.FollowsFilters);
		Assert.Equal(13, state.Selected.Count);
		Assert.Equal(SelectAllState.Some, _desk.GetView(state).SelectAll);

		state = _desk.ApplyStrategy(state, new TriangleWeights(1, 0, 0));
		Assert.True(state.Selected.Count <= 13);
		Assert.DoesNotContain(one, state.Selected);

		// With some ticked, the box ticks all. With all ticked, it clears.
		state = _desk.ToggleAll(state);
		Assert.Equal(66, state.Selected.Count);

		state = _desk.ToggleAll(state);
		Assert.Empty(state.Selected);
		Assert.False(state.FollowsFilters);
		Assert.Equal(SelectAllState.None, _desk.GetView(state).SelectAll);
	}

	[Fact]
	public void PickedMakersGoToThePlannerInTheOrderOfTheirNames()
	{
		var state = _desk.ToggleAll(_desk.Open());
		var selection = _desk.GetSelection(state, SampleMarket.Taker);

		Assert.Equal(48, selection.Makers.Count);
		Assert.Equal(selection.Makers.Select(maker => maker.Alias).Order(System.StringComparer.Ordinal), selection.Makers.Select(maker => maker.Alias));
		Assert.Equal(SampleMarket.Taker, selection.Taker);
		Assert.Equal(selection.Makers.Sum(maker => maker.Liquidity), selection.Liquidity);
		Assert.Equal("76.79", MarketFormat.Btc(selection.Liquidity, 2));

		// Without a taker there is still a selection, and nothing to plan it with.
		Assert.Null(_desk.GetSelection(state, taker: null).Taker);
	}

	/// <summary>
	/// The offers of this wallet are in the book, and follow the rules of every other row. Ticking them makes their
	/// coins what the taker brings.
	/// </summary>
	[Fact]
	public void OwnOffersAreRowsOfTheBookAndBringTheCoinsOfTheTaker()
	{
		var bonded = new BookMaker(1_000_001, "own-bonded", 0.2m, 1.5m, 2m, 0.5m, 50_000_000, 30_000_000, 3, 1, 4m, [], IsOwn: true);
		var plain = new BookMaker(1_000_002, "own-plain", 0.2m, 1.5m, 2m, 0.5m, 0, 20_000_000, 1, 0, 2m, [], IsOwn: true);
		var desk = new TakerDesk(Book, [bonded, plain]);
		var state = desk.Open();
		var view = desk.GetView(state);

		// The counts are of the makers of the book, on both sides of the stroke: the offer of the wallet that passes the
		// filters is a row that can be ticked, and is not counted. The offers of the wallet are rows among the others.
		Assert.Equal(250, view.BookCount);
		Assert.Equal(48, view.AvailableCount);
		Assert.Equal("48/250 Makers", view.CountText);
		Assert.Equal("76.79/418.91 BTC", view.LiquidityText);
		Assert.True(view.Bonded.Single(row => row.Maker.Id == bonded.Id).IsAvailable);
		Assert.Equal(217, view.Bonded.Count);
		Assert.Equal(35, view.Unbonded.Count);
		Assert.Contains(view.Bonded, row => row.Maker.Id == bonded.Id);
		Assert.Contains(view.Unbonded, row => row.Maker.Id == plain.Id);

		// The filters are moved by the book alone: the offers of the wallet do not change what a tick means.
		Assert.Equal(_desk.GetCutoffs(_desk.Open()), desk.GetCutoffs(state));

		state = desk.Toggle(state, bonded.Id);
		var other = view.Bonded.First(row => row.IsAvailable && !row.Maker.IsOwn).Maker;
		state = desk.Toggle(state, other.Id);

		var selection = desk.GetSelection(state, SampleMarket.Taker);

		Assert.Equal([other], selection.Makers);
		Assert.Equal(new PlannedTaker(InputCount: 4, Amount: 30_000_000), selection.Taker);

		// A ticked offer of the wallet stays ticked when a filter moves that it passes.
		state = desk.SetTick(state, BookCriterion.Liquidity, 1);
		Assert.Contains(bonded.Id, state.Selected);

		// And goes like any other row when it does not pass.
		state = desk.SetCutoff(state, BookCriterion.FeeRate, 0.1m);
		Assert.DoesNotContain(bonded.Id, state.Selected);
	}

	/// <summary>When the book changes under the screen, a strategy is applied to the new book, and limits that were set by hand stay what they were.</summary>
	[Fact]
	public void NewBookKeepsTheStrategyOrTheLimits()
	{
		BookMaker[] smaller = [.. Book.Take(120)];
		var later = new TakerDesk(smaller, []);

		// With the marker in the triangle.
		var state = _desk.ToggleAll(_desk.Open());
		var rebased = later.Rebase(_desk, state);

		Assert.Equal(TriangleWeights.Centre, rebased.Strategy);
		Assert.Equal(later.Open().Ticks, rebased.Ticks);
		Assert.All(rebased.Selected, id => Assert.Contains(smaller, maker => maker.Id == id));
		Assert.Equal(later.GetView(rebased).AvailableCount, rebased.Selected.Count);

		// With limits that were set by hand.
		state = _desk.SetCutoff(_desk.SetTick(_desk.Open(), BookCriterion.Bond, 100), BookCriterion.FeeRate, 0.55m);
		var bondLimit = _desk.GetCutoffs(state)[BookRules.IndexOf(BookCriterion.Bond)];
		rebased = later.Rebase(_desk, state);

		Assert.Null(rebased.Strategy);
		Assert.Equal(0.55m, later.GetCutoffs(rebased)[BookRules.IndexOf(BookCriterion.FeeRate)]);
		Assert.Equal(bondLimit, later.GetCutoffs(rebased)[BookRules.IndexOf(BookCriterion.Bond)]);
		Assert.Null(later.GetCutoffs(rebased)[BookRules.IndexOf(BookCriterion.Liquidity)]);
	}

	/// <summary>
	/// A maker without anonymity points sells at 0 whatever it buys at, so its spread is below zero. The filter of the
	/// spread takes a limit below zero, and no limit of the sell rate cuts such a maker.
	/// </summary>
	[Fact]
	public void MakerWithoutPointsHasASpreadBelowZero()
	{
		BookMaker[] book =
		[
			new(1, "with-points", 1m, BuyRate: 1m, SellRate: 3m, Spread: 2m, Bond: 0, Liquidity: 10_000_000, 1, 0, 4m, []),
			new(2, "more-points", 1m, BuyRate: 2m, SellRate: 2.5m, Spread: 0.5m, Bond: 0, Liquidity: 20_000_000, 1, 0, 9m, []),
			new(3, "no-points", 1m, BuyRate: 1.3m, SellRate: 0m, Spread: -1.3m, Bond: 0, Liquidity: 30_000_000, 2, 0, 0m, [])
		];
		var desk = new TakerDesk(book, []);

		// Without a limit all three can be picked.
		var open = new TakerDeskState();
		Assert.Equal(3, desk.GetView(open).AvailableCount);

		var spread = desk.GetView(open).Filters.Single(row => row.Criterion == BookCriterion.Spread);
		Assert.Equal("−1.3 sats", spread.BestText);
		Assert.Equal("≤ 2 sats", spread.CutoffText);

		// A limit of the spread below zero leaves the maker without points.
		var state = desk.SetCutoff(open, BookCriterion.Spread, -1m);
		var view = desk.GetView(state);

		Assert.Equal(["no-points"], view.Unbonded.Where(row => row.IsAvailable).Select(row => row.Maker.Alias));

		spread = view.Filters.Single(row => row.Criterion == BookCriterion.Spread);
		Assert.Equal("≤ −1 sats", spread.CutoffText);
		Assert.Equal("-1", spread.ValueText);

		// The strictest limit of the sell rate is its sell rate, 0.
		state = desk.SetTick(open, BookCriterion.SellRate, desk.MaxTick);

		Assert.Equal(0m, desk.GetCutoffs(state)[BookRules.IndexOf(BookCriterion.SellRate)]);
		Assert.Equal(["no-points"], desk.GetView(state).Unbonded.Where(row => row.IsAvailable).Select(row => row.Maker.Alias));
	}

	/// <summary>Whoever is the only maker on its relays sees its own offer in the table, and a book of nobody else.</summary>
	[Fact]
	public void OfferOfTheWalletAloneIsNoBookOfOthers()
	{
		var own = new BookMaker(1_000_001, "jade-ibis", 1.5m, BuyRate: 120m, SellRate: 0m, Spread: -120m, Bond: 0, Liquidity: 150_000_000, 3, 0, 0m, [], IsOwn: true);
		var desk = new TakerDesk([], [own]);
		var state = desk.Open();
		var view = desk.GetView(state);

		Assert.Empty(view.Bonded);
		Assert.Equal([own], view.Unbonded.Select(row => row.Maker));
		Assert.True(view.Unbonded[0].IsAvailable);
		Assert.Equal("0/0 Makers", view.CountText);
		Assert.Equal("0.00/0.00 BTC", view.LiquidityText);

		// It can be ticked, and then its coins are what the taker brings.
		state = desk.Toggle(state, own.Id);
		var selection = desk.GetSelection(state, taker: null);

		Assert.Empty(selection.Makers);
		Assert.Equal(new PlannedTaker(InputCount: 3, Amount: 150_000_000), selection.Taker);
	}

	[Fact]
	public void EmptyBookHasNothingToFilterOrToPick()
	{
		var desk = new TakerDesk([], []);
		var state = desk.Open();
		var view = desk.GetView(state);

		Assert.Empty(view.Bonded);
		Assert.Empty(view.Unbonded);
		Assert.Equal("0/0 Makers", view.CountText);
		Assert.Equal("0.00/0.00 BTC", view.LiquidityText);
		Assert.Equal(SelectAllState.None, view.SelectAll);
		Assert.All(view.Filters, filter => Assert.Equal("", filter.BestText));
		Assert.All(view.Filters.Where(filter => filter.Criterion != BookCriterion.Spread), filter => Assert.Equal("", filter.ValueText));

		Assert.Empty(desk.ToggleAll(state).Selected);
		Assert.Empty(desk.GetSelection(state, SampleMarket.Taker).Makers);
	}
}
