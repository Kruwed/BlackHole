using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.JoinMarket.OrderBook;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The taker's side of the market for anonymity points: the strategy triangle with the six filters, the order book,
/// and the transaction that the picked makers make with the taker. The market of these tests is the sample market of
/// the design: 250 makers, and a taker that brings 2 coins with 0.1 BTC.
/// </summary>
public class TakerScreenTests
{
	private readonly SampleMarketModel _market = new();

	[Fact]
	public async Task ScreenOpensWithTheMarkerInTheMiddleOfTheTriangleAsync()
	{
		var screen = new TakerScreenViewModel(_market);
		await screen.Planning;

		Assert.Equal(("Maximize Privacy", "Minimize Costs", "Maximize Speed"), (screen.TopName, screen.LeftName, screen.RightName));
		Assert.Equal(TriangleWeights.Centre, screen.Strategy);
		Assert.Equal("33% Privacy · 33% Costs · 33% Speed", screen.StrategyText);

		// The figures that the design opens with on its sample book.
		Assert.Equal("48/250 Makers", screen.CountText);
		Assert.Equal("76.79/418.91 BTC", screen.LiquidityText);

		Assert.Equal(["Liquidity", "Bond", "Fee rate", "Spread", "Sell rate", "Buy rate"], screen.Filters.Select(filter => filter.Label));
		Assert.All(screen.Filters, filter => Assert.True(filter.IsEnabled));
		Assert.All(screen.Filters, filter => Assert.Equal(249, filter.Maximum));

		var feeRate = screen.Filters[2];
		Assert.Equal(("0.1 sat/vB", "≤ 0.9 sat/vB", "0.9"), (feeRate.Caption, feeRate.ValueText, feeRate.Text));
		Assert.Equal(249 - 81, feeRate.Position);

		// The bar of a filter that cuts nobody is full.
		Assert.Equal(249, screen.Filters[0].Position);
		Assert.Equal(1, screen.Filters[0].Fill);

		// The makers with a bond, the caption of the others, and the others.
		Assert.Equal(251, screen.Rows.Count);
		Assert.Equal("34 Unbonded Makers", Assert.IsType<BookCaptionViewModel>(screen.Rows[216]).Text);
		Assert.Equal(250, screen.Rows.OfType<BookRowViewModel>().Count());

		var first = Assert.IsType<BookRowViewModel>(screen.Rows[0]);
		Assert.Equal(("misty-noddy", "0.1", "0.6", "2.6", "2", "0.23606352", "1.98661486"), (first.Alias, first.FeeRate, first.Spread, first.SellRate, first.BuyRate, first.Liquidity, first.Bond));
		Assert.True(first.IsAvailable);
		Assert.False(first.IsSelected);
		Assert.False(first.IsOwn);

		Assert.False(screen.IsAnySelected);
		Assert.False(screen.HasSelection);
		Assert.Equal(BookCriterion.FeeRate, screen.SortKey);
		Assert.True(screen.SortAscending);
		Assert.True(screen.IsBuilderShown);
		Assert.False(screen.IsCalculatorShown);
		Assert.Null(screen.BookNotice);
		Assert.StartsWith("The sample Taker brings 2 coins with 0.10000000 BTC.", screen.TakerNotice);

		// Nobody is picked: the taker is alone, and its transaction mints nothing.
		Assert.False(screen.IsPlanning);
		Assert.NotNull(screen.Review);
		Assert.Equal(("0", "1", "0.00"), (screen.Review.Points, screen.Review.Participants, screen.Review.Liquidity));
		Assert.Equal(("0", "0", "0"), (screen.Review.MatchedIn, screen.Review.MatchedOut, screen.Review.UnmatchedOut));
	}

	/// <summary>The triangle sets four of the filters at once. What its corners admit is what the design says.</summary>
	[Theory]
	[InlineData(1, 0, 0, "100% Privacy", "66/250 Makers", "122.87/418.91 BTC")]
	[InlineData(0, 1, 0, "100% Costs", "14/250 Makers", "26.55/418.91 BTC")]
	[InlineData(0, 0, 1, "100% Speed", "59/250 Makers", "93.31/418.91 BTC")]
	[InlineData(0.5, 0.5, 0, "50% Privacy · 50% Costs", null, null)]
	public void TriangleDrivesTheFilters(double privacy, double costs, double speed, string words, string? count, string? liquidity)
	{
		var screen = new TakerScreenViewModel(_market);
		var before = screen.Filters.Select(filter => filter.ValueText).ToArray();

		screen.Strategy = new TriangleWeights(privacy, costs, speed);

		Assert.Equal(words, screen.StrategyText);
		Assert.NotEqual(before, screen.Filters.Select(filter => filter.ValueText));

		// The liquidity is the taker's to set: the triangle does not touch it.
		Assert.Equal(before[0], screen.Filters[0].ValueText);

		if (count is not null)
		{
			Assert.Equal(count, screen.CountText);
			Assert.Equal(liquidity, screen.LiquidityText);
		}
	}

	[Fact]
	public void PrivacyCornerSetsTheLimitsOfTheDesign()
	{
		var screen = new TakerScreenViewModel(_market);

		screen.Strategy = new TriangleWeights(1, 0, 0);

		Assert.Equal(
			["≥ 0.09156593 BTC", "≥ 0.76616473 BTC", "≤ 0.9 sat/vB", "≤ 4 sats", "≤ 4.1 sats/AP", "≥ 0.1 sats/AP"],
			screen.Filters.Select(filter => filter.ValueText));
	}

	[Fact]
	public void SliderOfAFilterTakesTheMarkerOutOfTheTriangle()
	{
		var screen = new TakerScreenViewModel(_market);
		var feeRate = screen.Filters[2];

		// All the way to the strict end: only the best fee rate of the book is left.
		feeRate.Position = 0;

		Assert.Null(screen.Strategy);
		Assert.Equal("", screen.StrategyText);
		Assert.Equal("≤ 0.1 sat/vB", feeRate.ValueText);
		Assert.Equal(0, feeRate.Fill);
		Assert.All(screen.Rows.OfType<BookRowViewModel>().Where(row => row.IsAvailable), row => Assert.Equal("0.1", row.FeeRate));

		// All the way to the loose end: no limit, which shows as the worst fee rate of the book.
		feeRate.Position = feeRate.Maximum;

		Assert.Equal(1, feeRate.Fill);
		Assert.All(screen.Rows.OfType<BookRowViewModel>(), row => Assert.False(row.FeeRateFails));
	}

	[Fact]
	public void LimitCanBeTypedIntoTheBoxOfAFilter()
	{
		var screen = new TakerScreenViewModel(_market);
		var feeRate = screen.Filters[2];

		feeRate.Text = "0.5";
		feeRate.CommitCommand.Execute(null);

		Assert.Null(screen.Strategy);
		Assert.Equal("≤ 0.5 sat/vB", feeRate.ValueText);
		Assert.Equal("0.5", feeRate.Text);

		var available = screen.Rows.OfType<BookRowViewModel>().Where(row => row.IsAvailable).ToArray();
		Assert.NotEmpty(available);
		Assert.All(available, row => Assert.True(decimal.Parse(row.FeeRate, System.Globalization.CultureInfo.InvariantCulture) <= 0.5m));

		// What is no number is not taken, and the box shows the limit again.
		feeRate.Text = "cheap";
		feeRate.CommitCommand.Execute(null);
		Assert.Equal("0.5", feeRate.Text);

		// Escape gives up what was typed.
		feeRate.Text = "0.2";
		feeRate.CancelCommand.Execute(null);
		Assert.Equal("0.5", feeRate.Text);
		Assert.Equal("≤ 0.5 sat/vB", feeRate.ValueText);

		// An empty box is no limit.
		feeRate.Text = "";
		feeRate.CommitCommand.Execute(null);
		Assert.Equal(feeRate.Maximum, feeRate.Position);
		Assert.All(screen.Rows.OfType<BookRowViewModel>(), row => Assert.False(row.FeeRateFails));
	}

	/// <summary>
	/// A row that is ticked stays the row that it was: the table is not made again, so it stays where the user scrolled
	/// it to. The transaction is planned with the maker of the row.
	/// </summary>
	[Fact]
	public async Task TickingAMakerPlansTheTransactionWithItAsync()
	{
		var screen = new TakerScreenViewModel(_market);
		var rows = screen.Rows;
		var row = Assert.IsType<BookRowViewModel>(rows[0]);

		screen.ToggleCommand.Execute(row.Id);

		Assert.Same(rows, screen.Rows);
		Assert.True(row.IsSelected);
		Assert.True(screen.HasSelection);
		Assert.True(screen.IsAnySelected);
		Assert.True(screen.IsSomeSelected);
		Assert.False(screen.IsAllSelected);

		await screen.Planning;

		Assert.NotNull(screen.Review);
		Assert.Equal("2", screen.Review.Participants);
		Assert.Equal("0.24", screen.Review.Liquidity);
		Assert.Contains(screen.Review.Settlement, line => line.Alias == "misty-noddy");

		screen.ToggleCommand.Execute(row.Id);
		await screen.Planning;

		Assert.False(row.IsSelected);
		Assert.False(screen.HasSelection);
		Assert.Equal("1", screen.Review!.Participants);
	}

	[Fact]
	public void RowThatTheFiltersCutCannotBeTicked()
	{
		var screen = new TakerScreenViewModel(_market);
		var cut = screen.Rows.OfType<BookRowViewModel>().First(row => !row.IsAvailable);

		screen.ToggleCommand.Execute(cut.Id);

		Assert.False(cut.IsSelected);
		Assert.False(screen.HasSelection);

		// Its cells that fail their filter are the ones that are dimmed further.
		Assert.Contains(true, new[] { cut.FeeRateFails, cut.SpreadFails, cut.SellRateFails, cut.BuyRateFails, cut.LiquidityFails, cut.BondFails });
	}

	/// <summary>
	/// The box in the head of the table has three states. Ticked, it picks every maker that passes, and the selection
	/// follows the filters from then on. The whole selection of the opening state makes the transaction below.
	/// </summary>
	[Fact]
	public async Task SelectAllPicksEveryMakerThatPassesAndFollowsTheFiltersAsync()
	{
		var screen = new TakerScreenViewModel(_market);

		screen.ToggleAllCommand.Execute(null);

		Assert.True(screen.IsAllSelected);
		Assert.True(screen.IsAnySelected);
		Assert.False(screen.IsSomeSelected);
		Assert.Equal(48, screen.Rows.OfType<BookRowViewModel>().Count(row => row.IsSelected));

		await screen.Planning;

		var review = screen.Review!;
		Assert.Equal(("113", "49", "76.79"), (review.Points, review.Participants, review.Liquidity));
		Assert.Equal(("113", "117", "0"), (review.MatchedIn, review.MatchedOut, review.UnmatchedOut));
		Assert.Equal(48, review.Settlement.Count);
		Assert.Equal(PlanReview.PayLabel, review.TotalLabel);

		// The selection follows the filters.
		screen.Strategy = new TriangleWeights(0, 1, 0);
		Assert.Equal(14, screen.Rows.OfType<BookRowViewModel>().Count(row => row.IsSelected));
		Assert.True(screen.IsAllSelected);

		// One row unticked: some are picked, and the selection does not follow any more.
		var picked = screen.Rows.OfType<BookRowViewModel>().First(row => row.IsSelected);
		screen.ToggleCommand.Execute(picked.Id);
		Assert.True(screen.IsSomeSelected);

		screen.Strategy = new TriangleWeights(1, 0, 0);
		Assert.True(screen.Rows.OfType<BookRowViewModel>().Count(row => row.IsSelected) <= 13);

		// With all picked, the box clears.
		screen.ToggleAllCommand.Execute(null);
		Assert.True(screen.IsAllSelected);
		screen.ToggleAllCommand.Execute(null);
		Assert.False(screen.IsAnySelected);

		await screen.Planning;
		Assert.Equal("1", screen.Review!.Participants);
	}

	[Fact]
	public void HeaderSortsTheTable()
	{
		var screen = new TakerScreenViewModel(_market);

		screen.SortCommand.Execute(BookCriterion.Liquidity);

		Assert.Equal(BookCriterion.Liquidity, screen.SortKey);
		Assert.False(screen.SortAscending);

		var available = screen.Rows.OfType<BookRowViewModel>().TakeWhile(row => row.IsAvailable).Select(row => row.Maker.Liquidity).ToArray();
		Assert.Equal(available.OrderDescending(), available);

		screen.SortCommand.Execute(BookCriterion.Liquidity);
		Assert.True(screen.SortAscending);

		// The filters and the counts are what they were.
		Assert.Equal("48/250 Makers", screen.CountText);
	}

	[Fact]
	public void RightColumnShowsTheBuilderOrTheCalculator()
	{
		var screen = new TakerScreenViewModel(_market);

		screen.ShowCalculatorCommand.Execute(null);
		Assert.True(screen.IsCalculatorShown);
		Assert.False(screen.IsBuilderShown);

		screen.ShowBuilderCommand.Execute(null);
		Assert.True(screen.IsBuilderShown);
	}

	/// <summary>How a taker reaches the makers that it picked is not designed yet. The button is there, cannot be pressed, and says why.</summary>
	[Fact]
	public async Task NothingCanBeSignedAsync()
	{
		var screen = new TakerScreenViewModel(_market);
		screen.ToggleAllCommand.Execute(null);
		await screen.Planning;

		Assert.False(screen.SignCommand.CanExecute(null));
		Assert.Equal("Sample data: there is nothing to sign.", screen.SignNotice);
	}

	/// <summary>A plan of the whole book takes a moment. It is made away from the thread of the screen, which stays free meanwhile.</summary>
	[Fact]
	public async Task PlanIsMadeAwayFromTheThreadOfTheScreenAsync()
	{
		var screenThread = Environment.CurrentManagedThreadId;
		var planThreads = new List<int>();
		using var started = new ManualResetEventSlim(false);
		using var gate = new ManualResetEventSlim(false);

		var screen = new TakerScreenViewModel(_market, selection =>
		{
			lock (planThreads)
			{
				planThreads.Add(Environment.CurrentManagedThreadId);
			}

			started.Set();
			gate.Wait(TimeSpan.FromSeconds(30));
			return TakerScreenViewModel.CreateReview(selection);
		});

		// The screen is there, and can be used, while the first plan is still being made.
		Assert.True(screen.IsPlanning);
		Assert.Null(screen.Review);
		Assert.False(screen.Planning.IsCompleted);

		screen.ToggleAllCommand.Execute(null);
		Assert.Equal(48, screen.Rows.OfType<BookRowViewModel>().Count(row => row.IsSelected));

		// The thread that makes the plan may not have got to it yet: on a busy computer it starts when it starts.
		Assert.True(started.Wait(TimeSpan.FromSeconds(30)));

		// The plan that is being made while this thread made the screen and ticked the makers is made on another one.
		lock (planThreads)
		{
			Assert.NotEqual(screenThread, Assert.Single(planThreads));
		}

		gate.Set();
		await screen.Planning;

		Assert.False(screen.IsPlanning);
		Assert.Equal("49", screen.Review!.Participants);
	}

	/// <summary>
	/// A slider on its way asks for a plan at every step. Plans are made one at a time, and of the ones that were
	/// asked for meanwhile only the last is made.
	/// </summary>
	[Fact]
	public async Task OnlyTheLastOfThePlansThatWereAskedForMeanwhileIsMadeAsync()
	{
		var planned = new List<int>();
		using var gate = new ManualResetEventSlim(false);

		var screen = new TakerScreenViewModel(_market, selection =>
		{
			lock (planned)
			{
				planned.Add(selection.Makers.Count);
			}

			gate.Wait(TimeSpan.FromSeconds(30));
			return TakerScreenViewModel.CreateReview(selection);
		});

		var ids = screen.Rows.OfType<BookRowViewModel>().Where(row => row.IsAvailable).Take(4).Select(row => row.Id).ToArray();

		foreach (var id in ids)
		{
			screen.ToggleCommand.Execute(id);
		}

		gate.Set();
		await screen.Planning;

		// The plan of the empty selection that the screen opened with, and the plan of the four makers.
		Assert.Equal([0, 4], planned);
		Assert.Equal("5", screen.Review!.Participants);
	}

	[Fact]
	public async Task SelectionThatIsPlannedAlreadyIsNotPlannedAgainAsync()
	{
		var plans = 0;
		var screen = new TakerScreenViewModel(_market, selection =>
		{
			Interlocked.Increment(ref plans);
			return TakerScreenViewModel.CreateReview(selection);
		});

		await screen.Planning;

		// What moves no maker into or out of the selection changes nothing about the transaction.
		screen.SortCommand.Execute(BookCriterion.Bond);
		screen.ShowCalculatorCommand.Execute(null);
		screen.Strategy = new TriangleWeights(1, 0, 0);
		screen.Filters[0].Position = 100;
		await screen.Planning;

		Assert.Equal(1, plans);

		screen.ToggleAllCommand.Execute(null);
		await screen.Planning;

		Assert.Equal(2, plans);
	}

	/// <summary>
	/// An offer of this wallet is a row of the book with a "you" tag. Ticking it makes its coins what the taker brings
	/// in place of the sample taker's.
	/// </summary>
	[Fact]
	public async Task OwnOfferIsInTheBookAndBringsItsCoinsAsync()
	{
		using var disposables = new CompositeDisposable();
		var screen = new TakerScreenViewModel(_market);
		screen.Activate(disposables);

		var maker = new MakerScreenViewModel(_market, _ => Task.FromResult(true), () => Task.FromResult(true));
		maker.ShowOffersCommand.Execute(null);
		await maker.PostOfferAsync();

		// The screen follows the market: the offer is in the book without being asked for.
		var own = Assert.Single(screen.Rows.OfType<BookRowViewModel>(), row => row.IsOwn);
		Assert.Equal(_market.Offers.Single().Alias, own.Alias);
		Assert.Equal("2.91270593", own.Liquidity);
		Assert.Equal(MarketFormat.None, own.Bond);
		Assert.Equal(251, screen.Rows.OfType<BookRowViewModel>().Count());
		Assert.Equal("48/250 Makers", screen.CountText);

		// An offer without a bond does not pass the bond filter of the opening state, like any maker without one.
		Assert.False(own.IsAvailable);

		screen.Filters[1].Position = screen.Filters[1].Maximum;
		own = Assert.Single(screen.Rows.OfType<BookRowViewModel>(), row => row.IsOwn);
		Assert.True(own.IsAvailable);

		var other = screen.Rows.OfType<BookRowViewModel>().First(row => row.IsAvailable && !row.IsOwn);
		screen.ToggleCommand.Execute(own.Id);
		screen.ToggleCommand.Execute(other.Id);
		await screen.Planning;

		// The taker and one maker. The taker is not a maker of its own transaction.
		Assert.Equal("2", screen.Review!.Participants);
		Assert.DoesNotContain(screen.Review.Settlement, line => line.Alias == own.Alias);
		Assert.Contains(screen.Review.Settlement, line => line.Alias == other.Alias);
	}

	[Fact]
	public void UnbondedMakersAreCountedInTheirCaption()
	{
		Assert.Equal("34 Unbonded Makers", TakerScreenViewModel.DescribeUnbonded(34));
		Assert.Equal("1 Unbonded Maker", TakerScreenViewModel.DescribeUnbonded(1));
		Assert.Equal("1,250 Unbonded Makers", TakerScreenViewModel.DescribeUnbonded(1_250));
	}

	/// <summary>Without makers there is nothing to filter: the rows under the triangle are there and do nothing.</summary>
	[Fact]
	public async Task EmptyBookHasNoFiltersToMoveAsync()
	{
		var market = new EmptyMarket();
		var screen = new TakerScreenViewModel(market);
		await screen.Planning;

		Assert.Empty(screen.Rows);
		Assert.Equal("0/0 Makers", screen.CountText);
		Assert.All(screen.Filters, filter => Assert.False(filter.IsEnabled));
		Assert.All(screen.Filters, filter => Assert.Equal(MarketFormat.None, filter.ValueText));
		Assert.Equal("Your Nostr relays have no offers of other Makers.", screen.BookNotice);

		// Without coins of the taker there is nothing to plan.
		Assert.Null(screen.Review);
		Assert.False(screen.SignCommand.CanExecute(null));
	}

	/// <summary>A market where nobody offers anything and the taker has ticked no coin.</summary>
	private sealed class EmptyMarket : IMarket
	{
		public bool IsSample => false;
		public IObservable<System.Reactive.Unit> Changed => System.Reactive.Linq.Observable.Never<System.Reactive.Unit>();
		public IReadOnlyList<BookMaker> Book => [];
		public string? BookNotice => "Your Nostr relays have no offers of other Makers.";
		public IReadOnlyList<MarketCoinInfo> Coins => [];
		public IReadOnlyList<MarketBondInfo> Bonds => [];
		public IReadOnlyList<MarketOfferInfo> Offers => [];
		public long OwnBond => 0;
		public WalletWasabi.JoinMarket.Planning.PlannedTaker? Taker => null;
		public string TakerNotice => "Tick the coins that you bring on the Coins tab, or tick an offer of your own in the book.";
		public string SignNotice => "Not available yet.";
		public Task RefreshBookAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken) => Task.FromResult<string?>("no");
		public Task CancelOfferAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken) => Task.FromResult<(BondQuote?, string?)>((null, "no"));
		public Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken) => Task.FromResult<string?>("no");

		public void RedeemBond(MarketBondInfo bond)
		{
		}
	}
}
