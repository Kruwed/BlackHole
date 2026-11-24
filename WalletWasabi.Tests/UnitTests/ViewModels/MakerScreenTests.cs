using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The maker's side of the market for anonymity points: the strategy triangle with the split of the wallet and the
/// rates of the offer, the coins of the wallet, and the two things that a maker does with them: Lock Coins and List
/// Offers. The market of these tests is the sample market of the design: 16 coins that hold 5.82541186 BTC.
/// </summary>
public class MakerScreenTests
{
	private readonly SampleMarketModel _market = new();
	private readonly List<BondQuote> _questions = [];
	private bool _confirms = true;

	[Fact]
	public void ScreenOpensOnLockCoinsWithTheMarkerInTheMiddleOfTheTriangle()
	{
		var screen = CreateScreen();

		Assert.Equal(("Earn AP", "Earn sats", "Remix often"), (screen.TopName, screen.LeftName, screen.RightName));
		Assert.Equal(TriangleWeights.Centre, screen.Strategy);
		Assert.Equal("33% AP · 33% Sats · 33% Remix", screen.StrategyText);

		Assert.True(screen.IsBondTab);
		Assert.False(screen.IsOffersTab);
		Assert.False(screen.HasBond);

		// The form of the bond: half of the wallet, two weeks, 1 sat/vB.
		Assert.Equal(("2.91270593", "2016", "1"), (screen.BondAmount, screen.BondBlocks, screen.BondFeeRate));
		Assert.False(screen.HasBondError);

		// The list: the largest coin first, and the two that cover the bond picked.
		Assert.Equal(16, screen.Coins.Count);
		Assert.Equal("UTXO", screen.ReferenceHeader);
		Assert.True(screen.IsSortedByAmount);
		Assert.False(screen.IsSortedByPoints);

		var first = screen.Coins[0];
		Assert.Equal(("62a5f470…4a64e5:2", "4", "1.99759426"), (first.Reference, first.Points, first.Amount));
		Assert.Equal([true, true, false], screen.Coins.Take(3).Select(row => row.IsSelected));
		Assert.All(screen.Coins, row => Assert.True(row.CanToggle));
		Assert.All(screen.Coins, row => Assert.True(row.IsFree));
		Assert.True(screen.IsSomeSelected);
	}

	/// <summary>
	/// The rows under the triangle: the split of the wallet is live on Lock Coins while there is no bond, and the
	/// rates of the offer are live on List Offers. The others are there, dimmed, and do nothing.
	/// </summary>
	[Fact]
	public void RowsUnderTheTriangleAreLiveOnTheTabThatTheyBelongTo()
	{
		var screen = CreateScreen();

		Assert.Equal(["Liquidity", "Bond"], screen.AllocationRows.Select(row => row.Label));
		Assert.Equal(["Fee rate", "Sell rate", "Buy rate"], screen.RateRows.Select(row => row.Label));

		Assert.All(screen.AllocationRows, row => Assert.True(row.IsEnabled));
		Assert.All(screen.RateRows, row => Assert.False(row.IsEnabled));
		Assert.All(screen.AllocationRows, row => Assert.Equal(("50% of 5.8254 BTC", "2.9127 BTC", "2.9127", 50d), (row.Caption, row.ValueText, row.Text, row.Position)));

		screen.ShowOffersCommand.Execute(null);

		Assert.True(screen.IsOffersTab);
		Assert.All(screen.AllocationRows, row => Assert.False(row.IsEnabled));
		Assert.All(screen.RateRows, row => Assert.True(row.IsEnabled));
		Assert.Equal(
			[("beats 44% of the book", "0.6 sat/vB", "0.6"), ("beats 48% of the book", "2.6 sats / AP", "2.6"), ("beats 45% of the book", "1.3 sats / AP", "1.3")],
			screen.RateRows.Select(row => (row.Caption, row.ValueText, row.Text)));

		screen.ShowBondCommand.Execute(null);

		Assert.True(screen.IsBondTab);
		Assert.All(screen.AllocationRows, row => Assert.True(row.IsEnabled));
		Assert.All(screen.RateRows, row => Assert.False(row.IsEnabled));
	}

	[Fact]
	public void ListOffersOpensWithThreeCoinsAndTheRatesOfTheStrategy()
	{
		var screen = CreateScreen();

		screen.ShowOffersCommand.Execute(null);

		Assert.Equal(("2.91270593", "0.6", "2.6", "1.3"), (screen.OfferAmount, screen.OfferFeeRate, screen.OfferSellRate, screen.OfferBuyRate));
		Assert.Equal("Offer 8.4 AP", screen.OfferButtonText);
		Assert.False(screen.HasOfferError);

		// The second largest coin comes from the transaction of the largest, and is passed over.
		Assert.Equal([true, false, true, true, false], screen.Coins.Take(5).Select(row => row.IsSelected));
		Assert.Empty(screen.Offers);
	}

	/// <summary>The triangle sets the rates and the split. Its corners are the ones of the design.</summary>
	[Theory]
	[InlineData(1, 0, 0, "100% AP", 50)]
	[InlineData(0, 1, 0, "100% Sats", 30)]
	[InlineData(0, 0, 1, "100% Remix", 70)]
	public void TriangleDrivesTheSplitAndTheRates(double top, double left, double right, string words, int bondPercent)
	{
		var screen = CreateScreen();
		var weights = new TriangleWeights(top, left, right);

		screen.Strategy = weights;

		Assert.Equal(words, screen.StrategyText);
		Assert.Equal(bondPercent, screen.AllocationRows[1].Position);
		Assert.Equal(100 - bondPercent, screen.AllocationRows[0].Position);
		Assert.Equal($"{bondPercent}% of 5.8254 BTC", screen.AllocationRows[1].Caption);

		var terms = MakerStrategy.GetTerms(SampleMarket.Makers, weights);
		Assert.Equal(MarketFormat.FeeRate(terms.FeeRate), screen.OfferFeeRate);
		Assert.Equal(MarketFormat.Rate(terms.SellRate), screen.OfferSellRate);
		Assert.Equal(MarketFormat.Rate(terms.BuyRate), screen.OfferBuyRate);
	}

	[Fact]
	public void BarOfTheSplitMovesTheSplitAndTakesTheMarkerOutOfTheTriangle()
	{
		var screen = CreateScreen();

		screen.AllocationRows[1].Position = 25;

		Assert.Null(screen.Strategy);
		Assert.Equal("", screen.StrategyText);
		Assert.Equal((75d, 25d), (screen.AllocationRows[0].Position, screen.AllocationRows[1].Position));
		Assert.Equal("1.45635297", screen.BondAmount);
		Assert.Equal([true, false], screen.Coins.Take(2).Select(row => row.IsSelected));

		// The bar of the liquidity is the same split from the other side.
		screen.AllocationRows[0].Position = 40;
		Assert.Equal(60, screen.AllocationRows[1].Position);

		// An amount that is typed into a bar is a share of the wallet.
		screen.AllocationRows[1].Text = "2.91270593";
		screen.AllocationRows[1].CommitCommand.Execute(null);
		Assert.Equal(50, screen.AllocationRows[1].Position);
		Assert.Equal("2.9127", screen.AllocationRows[1].Text);
	}

	[Fact]
	public void BarOfARateSetsTheRateOfTheOffer()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);
		var sell = screen.RateRows[1];

		sell.Position = 70;

		Assert.Null(screen.Strategy);
		Assert.Equal(MarketFormat.Rate(MakerStrategy.GetRateAt(SampleMarket.Makers, BookCriterion.SellRate, 0.7)), screen.OfferSellRate);
		Assert.Equal(screen.OfferSellRate, sell.Text);
		Assert.StartsWith("beats ", sell.Caption);

		// A rate that is typed into its bar is the rate.
		sell.Text = "3";
		sell.CommitCommand.Execute(null);
		Assert.Equal("3", screen.OfferSellRate);
		Assert.Equal("3 sats / AP", sell.ValueText);

		// The other rates and the coins stay.
		Assert.Equal(("0.6", "1.3"), (screen.OfferFeeRate, screen.OfferBuyRate));
		Assert.Equal(3, screen.Coins.Count(row => row.IsSelected));
	}

	/// <summary>A row that is ticked stays the row that it was: the list is not made again.</summary>
	[Fact]
	public void TickingACoinKeepsTheListAndMovesTheAmount()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);
		var rows = screen.Coins;
		var second = rows[1];

		screen.ToggleCommand.Execute(second.Id);

		Assert.Same(rows, screen.Coins);
		Assert.True(second.IsSelected);

		// It comes from the transaction of a larger picked coin: red, and it does not count.
		Assert.True(second.IsSharedOrigin);
		Assert.False(second.IsExtra);
		Assert.False(second.HasNoPoints);
		Assert.Equal("3.23784587", screen.OfferAmount);

		screen.ToggleCommand.Execute(rows[0].Id);

		Assert.False(rows[0].IsSelected);
		Assert.False(second.IsSharedOrigin);
		Assert.Equal("2.44407724", screen.OfferAmount);
	}

	[Fact]
	public void BoxInTheHeadOfTheListHasThreeStates()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);

		Assert.True(screen.IsSomeSelected);
		Assert.True(screen.IsAnySelected);
		Assert.False(screen.IsAllSelected);

		screen.ToggleAllCommand.Execute(null);
		Assert.True(screen.IsAllSelected);
		Assert.False(screen.IsSomeSelected);
		Assert.All(screen.Coins, row => Assert.True(row.IsSelected));

		// Beyond the first three, a private coin is orange: it only adds an input to pay for.
		Assert.Contains(screen.Coins, row => row.IsExtra);

		screen.ToggleAllCommand.Execute(null);
		Assert.False(screen.IsAnySelected);
		Assert.All(screen.Coins, row => Assert.False(row.IsSelected));
		Assert.Equal("", screen.OfferAmount);
	}

	[Fact]
	public void HeadsOfTheListSortItAndSwitchBetweenOutputsAndAddresses()
	{
		var screen = CreateScreen();

		screen.SortByPointsCommand.Execute(null);

		Assert.True(screen.IsSortedByPoints);
		Assert.False(screen.IsSortAscending);
		Assert.Equal("22", screen.Coins[0].Points);

		screen.SortByPointsCommand.Execute(null);
		Assert.True(screen.IsSortAscending);
		Assert.Equal("1", screen.Coins[0].Points);

		screen.SortByAmountCommand.Execute(null);
		Assert.True(screen.IsSortedByAmount);
		Assert.Equal("1.99759426", screen.Coins[0].Amount);

		var rows = screen.Coins;
		screen.ToggleAddressesCommand.Execute(null);

		Assert.Equal("Address", screen.ReferenceHeader);
		Assert.Same(rows, screen.Coins);
		Assert.StartsWith("bc1", screen.Coins[0].Reference);
		Assert.Contains("…", screen.Coins[0].Reference);

		screen.ToggleAddressesCommand.Execute(null);
		Assert.Equal("62a5f470…4a64e5:2", screen.Coins[0].Reference);
	}

	/// <summary>What is typed into a box of a form is taken as a number: digits, and one point where the number can have one.</summary>
	[Fact]
	public void FormsTakeNumbers()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);

		screen.OfferFeeRate = "0.7 sat";
		Assert.Equal("0.7", screen.OfferFeeRate);

		screen.OfferSellRate = "3.19";
		Assert.Equal("3.1", screen.OfferSellRate);

		screen.OfferBuyRate = "x";
		Assert.Equal("", screen.OfferBuyRate);

		screen.OfferAmount = "1.123456789";
		Assert.Equal("1.12345678", screen.OfferAmount);

		screen.BondBlocks = "4,320.5";
		Assert.Equal("43205", screen.BondBlocks);

		screen.BondFeeRate = "2.5";
		Assert.Equal("2.5", screen.BondFeeRate);

		// The bar of a rate shows what was typed into the form.
		Assert.Equal(("0.7", "0.7 sat/vB"), (screen.RateRows[0].Text, screen.RateRows[0].ValueText));
	}

	/// <summary>Every message of the form of an offer, as the design words them.</summary>
	[Fact]
	public async Task OfferThatCannotBePostedSaysWhyAsync()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);

		screen.OfferAmount = "";
		await screen.PostOfferAsync();
		Assert.Equal("Enter an amount to offer.", screen.OfferError);
		Assert.True(screen.HasOfferError);

		// Typing takes the message away.
		screen.OfferAmount = "5";
		Assert.False(screen.HasOfferError);

		await screen.PostOfferAsync();
		Assert.Equal(
			"Amount exceeds the counted coins: 5.00000000 BTC offered, 3.23784587 BTC across the three largest coins from distinct origins.",
			screen.OfferError);

		screen.OfferAmount = "1";
		screen.OfferFeeRate = "";
		await screen.PostOfferAsync();
		Assert.Equal("Enter a minimum fee rate.", screen.OfferError);

		screen.OfferFeeRate = "1";
		screen.OfferSellRate = "";
		await screen.PostOfferAsync();
		Assert.Equal("Enter a sell rate.", screen.OfferError);

		screen.OfferSellRate = "2";
		screen.OfferBuyRate = "";
		await screen.PostOfferAsync();
		Assert.Equal("Enter a buy rate.", screen.OfferError);

		screen.OfferBuyRate = "3";
		await screen.PostOfferAsync();
		Assert.Equal("The sell rate cannot be lower than the buy rate.", screen.OfferError);

		screen.ToggleAllCommand.Execute(null);
		screen.ToggleAllCommand.Execute(null);
		await screen.PostOfferAsync();
		Assert.Equal("Select the coins to offer.", screen.OfferError);

		Assert.Empty(screen.Offers);
		Assert.Empty(_market.Offers);
	}

	/// <summary>
	/// A posted offer is listed under the form with what it tells, its coins cannot be picked again, the next three
	/// coins are picked, and the rates stay for the next offer. Cancel takes it back.
	/// </summary>
	[Fact]
	public async Task OfferIsPostedListedAndTakenBackAsync()
	{
		var screen = CreateScreen();
		screen.ShowOffersCommand.Execute(null);
		screen.OfferFeeRate = "0.7";

		await screen.PostOfferAsync();

		Assert.False(screen.HasOfferError);
		Assert.False(screen.IsBusy);

		var card = Assert.Single(screen.Offers);
		Assert.Matches("^[a-z]+-[a-z]+$", card.Alias);
		Assert.Equal("active", card.Status);
		Assert.False(card.HasProblem);
		Assert.Equal("3 Matched", card.Coins);
		Assert.Equal(("2.91270593", "3.23784587"), (card.Amount, card.Total));
		Assert.Equal("8.4", card.Points);
		Assert.Equal(("0.7 sat/vB", "1.3 sats / AP", "2.6 sats / AP", "1.3 sats / AP"), (card.FeeRate, card.Spread, card.SellRate, card.BuyRate));

		// The coins of the offer are taken, and the next three of different origins are picked.
		var rows = screen.Coins.ToDictionary(row => row.Id);
		Assert.All(new[] { "13", "10", "2" }, id => Assert.True(rows[id].IsTaken));
		Assert.All(new[] { "13", "10", "2" }, id => Assert.False(rows[id].CanToggle));
		Assert.Equal(["1", "3", "9"], screen.Coins.Where(row => row.IsSelected).Select(row => row.Id).Order());
		Assert.Equal("0.7", screen.OfferFeeRate);

		// A second offer next to the first.
		await screen.PostOfferAsync();
		Assert.Equal(2, screen.Offers.Count);
		Assert.NotEqual(screen.Offers[0].Alias, screen.Offers[1].Alias);

		// Taken back: its coins can be picked again.
		await screen.CancelOfferAsync(card.Id);

		Assert.Single(screen.Offers);
		rows = screen.Coins.ToDictionary(row => row.Id);
		Assert.All(new[] { "13", "10", "2" }, id => Assert.False(rows[id].IsTaken));
		Assert.All(new[] { "1", "3", "9" }, id => Assert.True(rows[id].IsTaken));
	}

	[Fact]
	public async Task OfferThatTheMarketDoesNotTakeSaysWhatTheMarketSaidAsync()
	{
		var market = new RefusingMarket();
		var screen = new MakerScreenViewModel(market, _ => Task.FromResult(true), () => Task.FromResult(true));
		screen.ShowOffersCommand.Execute(null);

		await screen.PostOfferAsync();

		Assert.Equal("The offer was not posted. No Nostr relay took it: relay.example: refused", screen.OfferError);
		Assert.Empty(screen.Offers);
		Assert.False(screen.IsBusy);

		// The picked coins and the form are what they were.
		Assert.Equal(3, screen.Coins.Count(row => row.IsSelected));
		Assert.Equal("2.91270593", screen.OfferAmount);
	}

	/// <summary>Every message of the form of a bond, as the design words them.</summary>
	[Fact]
	public async Task BondThatCannotBeLockedSaysWhyAsync()
	{
		var screen = CreateScreen();

		screen.BondAmount = "";
		await screen.LockBondAsync();
		Assert.Equal("Enter an amount to lock.", screen.BondError);
		Assert.True(screen.HasBondError);

		screen.BondAmount = "9";
		Assert.False(screen.HasBondError);
		await screen.LockBondAsync();
		Assert.Equal("Amount exceeds the selected coins: 9.00000000 BTC requested, 3.20141989 BTC selected.", screen.BondError);

		screen.BondAmount = "1";
		screen.BondBlocks = "";
		await screen.LockBondAsync();
		Assert.Equal("Enter a lock time in blocks.", screen.BondError);

		screen.BondBlocks = "2016";
		screen.BondFeeRate = "";
		await screen.LockBondAsync();
		Assert.Equal("Enter a fee rate for the bonding transaction.", screen.BondError);

		// Nothing was asked, and nothing was locked.
		Assert.Empty(_questions);
		Assert.Empty(screen.Bonds);
		Assert.False(screen.HasBond);
	}

	/// <summary>Before coins are locked a question states the amount, the lock and the fee. Without a yes nothing is locked.</summary>
	[Fact]
	public async Task BondIsNotLockedWithoutAYesAsync()
	{
		var screen = CreateScreen();
		_confirms = false;

		await screen.LockBondAsync();

		var question = Assert.Single(_questions);
		Assert.Equal("Confirm Bond for 2.91270593 BTC", question.Title);
		Assert.Equal("Locked for 2,016 blocks (2 weeks)", question.LockText);
		Assert.Equal("Fee rate 1 sat/vByte (211 sats)", question.FeeText);

		Assert.False(screen.HasBond);
		Assert.False(screen.HasBondError);
		Assert.True(screen.IsBondTab);
		Assert.False(screen.IsBusy);
		Assert.Equal(16, screen.Coins.Count);
	}

	/// <summary>
	/// Once a bond exists, Lock Coins tells about it: its amount, its lock, and where it stands among the bonds of
	/// the book. The bond is on top of the list with a padlock, the split is fixed, and the screen is on List Offers.
	/// </summary>
	[Fact]
	public async Task BondIsLockedAndToldAsync()
	{
		var screen = CreateScreen();

		await screen.LockBondAsync();

		Assert.Single(_questions);
		Assert.True(screen.HasBond);
		Assert.False(screen.HasBondError);
		Assert.True(screen.IsOffersTab);

		var card = Assert.Single(screen.Bonds);
		var standing = BondFigures.GetStanding(291_270_593, SampleMarket.Makers);
		Assert.Equal("2.91270593 BTC", card.Amount);
		Assert.Equal("2,016 blocks", card.LockedFor);
		Assert.Equal("2,016 blocks left", card.UnlockTime);
		Assert.Equal($"{standing.Rank} of 217", card.Rank);
		Assert.Equal($"Top {standing.TopPercent}%", card.Percentile);
		Assert.InRange(standing.Rank, 1, 19);
		Assert.False(card.CanRedeem);
		Assert.False(card.HasStatus);

		// The bond on top of the list, with a padlock and the blocks that it is still locked for in place of a tick.
		var bond = screen.Coins[0];
		Assert.True(bond.IsBond);
		Assert.False(bond.IsFree);
		Assert.False(bond.CanToggle);
		Assert.Equal("2,016", bond.LockText);
		Assert.Equal("2.91270593", bond.Amount);

		// The two coins went into the bond, and what was left of them came back.
		Assert.DoesNotContain(screen.Coins, row => row.Id is "13" or "1");
		Assert.Contains(screen.Coins, row => row.Amount == "0.28871185");

		// The bond is the split: 2.91270593 of the 5.82540975 that the wallet has now.
		Assert.All(screen.AllocationRows, row => Assert.False(row.IsEnabled));
		Assert.Equal(50, screen.AllocationRows[1].Position);

		// The form of the next bond would start from its defaults.
		Assert.Equal(("2016", "1"), (screen.BondBlocks, screen.BondFeeRate));

		// And the offers of the wallet show the bond in the book.
		Assert.Equal(291_270_593, _market.OwnBond);
		Assert.Equal(3, screen.Coins.Count(row => row.IsSelected));
	}

	[Fact]
	public async Task BondThatTheWalletCouldNotLockSaysWhyAndGivingUpSaysNothingAsync()
	{
		var market = new RefusingMarket();
		var screen = new MakerScreenViewModel(market, _ => Task.FromResult(true), () => Task.FromResult(true));

		await screen.LockBondAsync();
		Assert.Equal("The bond was not locked. Not enough coins.", screen.BondError);
		Assert.True(screen.IsBondTab);

		// The user gave the password dialog up: that is no error to tell.
		market.LockAnswer = "";
		await screen.LockBondAsync();
		Assert.False(screen.HasBondError);
		Assert.True(screen.IsBondTab);

		// The wallet could not even tell what the bond would cost.
		market.QuoteError = "The Bitcoin node does not answer.";
		await screen.LockBondAsync();
		Assert.Equal("The Bitcoin node does not answer.", screen.BondError);
		Assert.False(screen.IsBusy);
	}

	/// <summary>
	/// A wallet whose coins have no anonymity points can offer them all the same. Such an offer has no points to sell:
	/// it lists a sell rate of 0, and the box of the sell rate is not the maker's to fill.
	/// </summary>
	[Fact]
	public async Task OfferOfCoinsWithoutPointsListsASellRateOfZeroAsync()
	{
		var market = new PlainWalletMarket();
		var screen = new MakerScreenViewModel(market, _ => Task.FromResult(true), () => Task.FromResult(true));

		// Lock Coins: only the coin with points can be locked.
		Assert.Equal(["x", "y", "z"], screen.Coins.Select(row => row.Id));
		Assert.Equal([false, false, true], screen.Coins.Select(row => row.CanToggle));

		screen.ShowOffersCommand.Execute(null);

		// List Offers: the three largest coins are picked, with points or without, and the ones without are yellow.
		Assert.All(screen.Coins, row => Assert.True(row.CanToggle));
		Assert.All(screen.Coins, row => Assert.True(row.IsSelected));
		Assert.Equal([true, true, false], screen.Coins.Select(row => row.HasNoPoints));

		// (0 × 0.5 + 0 × 0.3 + 2 × 0.1) ÷ 0.9 = 0.22
		Assert.Equal("Offer 0.2 AP", screen.OfferButtonText);
		Assert.Equal("0.45000000", screen.OfferAmount);
		Assert.True(screen.IsSellRateEnabled);
		Assert.Null(screen.SellRateNote);
		Assert.Equal("2.6", screen.OfferSellRate);

		// Without the coin that has points, the offer has none to sell.
		screen.ToggleCommand.Execute("z");

		Assert.Equal("Offer 0.0 AP", screen.OfferButtonText);
		Assert.Equal("0.80000000", screen.OfferAmount);
		Assert.Equal("0", screen.OfferSellRate);
		Assert.False(screen.IsSellRateEnabled);
		Assert.Equal("The picked coins have no anonymity points to sell, so the offer lists a sell rate of 0.", screen.SellRateNote);

		// The bar of the sell rate shows it, and does nothing. The other two rates are the maker's as before.
		Assert.Equal(("0 sats / AP", "0", false), (screen.RateRows[1].ValueText, screen.RateRows[1].Text, screen.RateRows[1].IsEnabled));
		Assert.Equal(("0.6", true), (screen.RateRows[0].Text, screen.RateRows[0].IsEnabled));
		Assert.Equal(("1.3", true), (screen.RateRows[2].Text, screen.RateRows[2].IsEnabled));

		// What is typed into the box is not taken.
		screen.OfferSellRate = "5";
		Assert.Equal("0", screen.OfferSellRate);

		// With the coin that has points again, the sell rate is what it was.
		screen.ToggleCommand.Execute("z");
		Assert.True(screen.IsSellRateEnabled);
		Assert.Equal("2.6", screen.OfferSellRate);
		Assert.True(screen.RateRows[1].IsEnabled);

		screen.ToggleCommand.Execute("z");
		await screen.PostOfferAsync();

		Assert.False(screen.HasOfferError);

		var draft = Assert.Single(market.Posted);
		Assert.Equal(["x", "y"], draft.CoinIds.Order());
		Assert.Equal(80_000_000, draft.Amount);
		Assert.Equal(0m, draft.Points);
		Assert.Equal(0m, draft.SellRate);
		Assert.Equal((0.6m, 1.3m), (draft.FeeRate, draft.BuyRate));
		Assert.Equal(2, draft.MatchedInputs);
	}

	/// <summary>The screen follows the market: what another part of the application does with the coins shows without being asked for.</summary>
	[Fact]
	public async Task ScreenFollowsTheMarketAsync()
	{
		using var disposables = new CompositeDisposable();
		var screen = CreateScreen();
		screen.Activate(disposables);
		screen.ShowOffersCommand.Execute(null);

		// An offer that was not posted from this screen.
		var other = CreateScreen();
		other.ShowOffersCommand.Execute(null);
		await other.PostOfferAsync();

		Assert.Single(screen.Offers);
		Assert.True(screen.Coins.Single(row => row.Id == "13").IsTaken);

		// The coins that this screen had picked went into that offer: the strategy picks the next ones.
		Assert.Equal(["1", "3", "9"], screen.Coins.Where(row => row.IsSelected).Select(row => row.Id).Order());
	}

	private MakerScreenViewModel CreateScreen() =>
		new(
			_market,
			quote =>
			{
				_questions.Add(quote);
				return Task.FromResult(_confirms);
			},
			() => Task.FromResult(true));

	/// <summary>The sample book, and a wallet of three coins of which the two largest have no anonymity points. It keeps what is posted.</summary>
	private sealed class PlainWalletMarket : IMarket
	{
		private readonly SampleMarketModel _sample = new();

		public List<OfferDraft> Posted { get; } = [];

		public bool IsSample => true;
		public IObservable<System.Reactive.Unit> Changed => System.Reactive.Linq.Observable.Never<System.Reactive.Unit>();
		public IReadOnlyList<BookMaker> Book => _sample.Book;
		public string? BookNotice => null;

		public IReadOnlyList<MarketCoinInfo> Coins { get; } =
		[
			new(new MarketCoin("x", "first", 50_000_000, 0m), new string('1', 64), 0, "bc1qfirst"),
			new(new MarketCoin("y", "second", 30_000_000, 0m), new string('2', 64), 1, "bc1qsecond"),
			new(new MarketCoin("z", "third", 10_000_000, 2m), new string('3', 64), 0, "bc1qthird")
		];

		public IReadOnlyList<MarketBondInfo> Bonds => [];
		public IReadOnlyList<MarketOfferInfo> Offers => [];
		public long OwnBond => 0;
		public PlannedTaker? Taker => null;
		public string TakerNotice => "";
		public string SignNotice => "";
		public Task RefreshBookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken)
		{
			Posted.Add(draft);
			return Task.FromResult<string?>(null);
		}

		public Task CancelOfferAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken) =>
			_sample.QuoteBondAsync(draft, cancellationToken);

		public Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken) =>
			Task.FromResult<string?>("no");

		public void RedeemBond(MarketBondInfo bond)
		{
		}
	}

	/// <summary>The sample market, where the relays take no offer and the wallet cannot lock.</summary>
	private sealed class RefusingMarket : IMarket
	{
		private readonly SampleMarketModel _sample = new();

		public string? LockAnswer { get; set; } = "The bond was not locked. Not enough coins.";

		public string? QuoteError { get; set; }

		public bool IsSample => true;
		public IObservable<System.Reactive.Unit> Changed => _sample.Changed;
		public IReadOnlyList<BookMaker> Book => _sample.Book;
		public string? BookNotice => null;
		public IReadOnlyList<MarketCoinInfo> Coins => _sample.Coins;
		public IReadOnlyList<MarketBondInfo> Bonds => _sample.Bonds;
		public IReadOnlyList<MarketOfferInfo> Offers => _sample.Offers;
		public long OwnBond => 0;
		public PlannedTaker? Taker => _sample.Taker;
		public string TakerNotice => _sample.TakerNotice;
		public string SignNotice => _sample.SignNotice;
		public Task RefreshBookAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<string?> PostOfferAsync(OfferDraft draft, CancellationToken cancellationToken) =>
			Task.FromResult<string?>("The offer was not posted. No Nostr relay took it: relay.example: refused");

		public Task CancelOfferAsync(int id, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task<(BondQuote? Quote, string? Error)> QuoteBondAsync(BondDraft draft, CancellationToken cancellationToken) =>
			QuoteError is null ? _sample.QuoteBondAsync(draft, cancellationToken) : Task.FromResult<(BondQuote?, string?)>((null, QuoteError));

		public Task<string?> LockBondAsync(BondQuote quote, Func<Task<bool>> authorize, CancellationToken cancellationToken) =>
			Task.FromResult(LockAnswer);

		public void RedeemBond(MarketBondInfo bond)
		{
		}
	}
}
