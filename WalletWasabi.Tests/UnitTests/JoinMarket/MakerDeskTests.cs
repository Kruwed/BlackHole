using System.Collections.Generic;
using System.Linq;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Sample;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// What a maker does with its coins: it splits the wallet between a bond and offers with the triangle or by hand,
/// picks coins, names its rates, and locks a bond or posts an offer. The wallet of these tests is the sample wallet
/// of the design, 16 coins that hold 5.82541186 BTC, and the book is its sample book.
/// </summary>
public class MakerDeskTests
{
	private static readonly IReadOnlyList<BookMaker> Book = SampleMarket.Makers;
	private static readonly IReadOnlyList<MarketCoin> Wallet = [.. SampleMarket.Coins.Select(coin => coin.ToMarketCoin())];

	// The coins of the sample wallet by their ids, the largest first: 62a5f470:2 (1.99759426 BTC, 4 points),
	// 62a5f470:0 (1.20382563, 4), 24e9e7b4:2 (0.74256326, 22), e9fa7162:2 (0.49768835, 6), e9fa7162:0 (0.30929659, 6).
	private const string Largest = "13";
	private const string SecondOfTheSameOrigin = "1";
	private const string Third = "10";
	private const string Fourth = "2";
	private const string FifthOfTheFourthsOrigin = "9";
	private const string Sixth = "3";

	private readonly MakerDesk _desk = new(Book, Wallet, new HashSet<string>(), offered: 0);

	/// <summary>
	/// Without a bond the screen opens on Lock Coins, with the marker in the middle of the triangle: half of the wallet
	/// for the bond, and the largest coins picked until they cover it.
	/// </summary>
	[Fact]
	public void DeskOpensOnLockCoinsWithHalfOfTheWalletForTheBond()
	{
		var state = _desk.Open();
		var view = _desk.GetView(state);

		Assert.False(_desk.HasBond);
		Assert.Equal(MakerTab.Bond, state.Tab);
		Assert.Equal(TriangleWeights.Centre, state.Strategy);
		Assert.Equal(0.5, state.BondShare, 9);

		// 1.99759426 does not cover 2.91270593, and 1.20382563 more does.
		Assert.Equal([SecondOfTheSameOrigin, Largest], state.Selected.Order());
		Assert.Equal("2.91270593", view.BondAmount);
		Assert.Equal("2016", state.BondBlocks);
		Assert.Equal("1", state.BondFeeRate);

		Assert.True(view.IsAllocationEnabled);
		Assert.False(view.AreRatesEnabled);

		Assert.Equal(
			[
				new MakerBarRow("liq", "Liquidity", 50, "50% of 5.8254 BTC", "2.9127 BTC", "2.9127"),
				new MakerBarRow("bond", "Bond", 50, "50% of 5.8254 BTC", "2.9127 BTC", "2.9127")
			],
			view.AllocationRows);

		// The rates that the middle of the triangle gives on the sample book.
		Assert.Equal(("0.6", "2.6", "1.3"), (state.OfferFeeRate, state.OfferSellRate, state.OfferBuyRate));
		Assert.Equal(
			[
				new MakerBarRow("minFee", "Fee rate", 44, "beats 44% of the book", "0.6 sat/vB", "0.6"),
				new MakerBarRow("sell", "Sell rate", 48, "beats 48% of the book", "2.6 sats / AP", "2.6"),
				new MakerBarRow("buy", "Buy rate", 45, "beats 45% of the book", "1.3 sats / AP", "1.3")
			],
			view.RateRows);
	}

	/// <summary>The list has the largest coin on top, and between coins of one amount the one of more points.</summary>
	[Fact]
	public void ListIsSortedByAmountAndCanBeSortedByPoints()
	{
		var state = _desk.Open();
		var rows = _desk.GetView(state).Coins;

		Assert.Equal(16, rows.Count);
		Assert.Equal([Largest, SecondOfTheSameOrigin, Third, Fourth, FifthOfTheFourthsOrigin, Sixth], rows.Take(6).Select(row => row.Coin.Id));
		Assert.Equal(rows.Select(row => row.Coin.Amount).OrderDescending(), rows.Select(row => row.Coin.Amount));

		// The first click on the other column: the most points first. The second: the fewest.
		state = _desk.SortBy(state, CoinSortKey.Points);
		Assert.Equal((CoinSortKey.Points, false), (state.SortKey, state.SortAscending));

		rows = _desk.GetView(state).Coins;
		Assert.Equal(22m, rows[0].Coin.Points);
		Assert.Equal(rows.Select(row => row.Coin.Points).OrderDescending(), rows.Select(row => row.Coin.Points));

		// Two coins of 21 points: the larger one first.
		var twentyOne = rows.Where(row => row.Coin.Points == 21m).Select(row => row.Coin.Amount).ToArray();
		Assert.Equal([9_573_512L, 4_070_621L], twentyOne);

		state = _desk.SortBy(state, CoinSortKey.Points);
		Assert.True(state.SortAscending);
		Assert.Equal(1m, _desk.GetView(state).Coins[0].Coin.Points);

		// The strategy picks along the order of the list. With the most points first, the bond takes the coins of 22 to
		// 6 points, 2.42 BTC in twelve coins, and then the larger of the two largest coins, which have 4 points each.
		state = _desk.SortBy(_desk.Open(), CoinSortKey.Points);

		Assert.Equal(13, state.Selected.Count);
		Assert.Contains(Third, state.Selected);
		Assert.Contains(Largest, state.Selected);
		Assert.DoesNotContain(SecondOfTheSameOrigin, state.Selected);
	}

	/// <summary>List Offers picks the three largest private coins that come from three different transactions.</summary>
	[Fact]
	public void ListOffersPicksThreeCoinsOfDifferentOrigins()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);
		var view = _desk.GetView(state);

		Assert.Equal(MakerTab.Offers, state.Tab);

		// The second largest coin comes from the transaction of the largest, and is left out.
		Assert.Equal([Third, Largest, Fourth], state.Selected.Order(System.StringComparer.Ordinal));

		// The half of the wallet that is not for the bond: 2.91270593 of the 3.23784587 that the three coins hold.
		Assert.Equal("2.91270593", view.OfferAmount);

		// (4 × 1.99759426 + 22 × 0.74256326 + 6 × 0.49768835) ÷ 3.23784587 = 8.44
		Assert.Equal("8.4", view.OfferPoints);

		Assert.False(view.IsAllocationEnabled);
		Assert.True(view.AreRatesEnabled);
		Assert.All(view.Coins, row => Assert.Equal(CoinMark.None, row.Mark));
		Assert.All(view.Coins, row => Assert.False(row.IsBarred));

		// Back on Lock Coins the largest coin is picked, and more until the bond is covered.
		state = _desk.SetTab(state, MakerTab.Bond);
		Assert.Equal([SecondOfTheSameOrigin, Largest], state.Selected.Order());
		Assert.Same(state, _desk.SetTab(state, MakerTab.Bond));
	}

	[Fact]
	public void AmountFollowsTheCoinsThatArePicked()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);

		state = _desk.Toggle(state, Largest);

		Assert.Equal([Third, Fourth], state.Selected.Order(System.StringComparer.Ordinal));
		Assert.Equal("1.24025161", _desk.GetView(state).OfferAmount);

		// (22 × 0.74256326 + 6 × 0.49768835) ÷ 1.24025161 = 15.58
		Assert.Equal("15.6", _desk.GetView(state).OfferPoints);

		state = _desk.Toggle(_desk.Toggle(state, Third), Fourth);

		Assert.Empty(state.Selected);
		Assert.Equal("", _desk.GetView(state).OfferAmount);
		Assert.Equal(SelectAllState.None, _desk.GetView(state).SelectAll);

		// On Lock Coins the strategy picks its coins again. Once one is ticked by hand, the amount is what the picked coins hold.
		state = _desk.SetTab(state, MakerTab.Bond);
		Assert.Equal("2.91270593", _desk.GetView(state).BondAmount);

		state = _desk.Toggle(state, Third);
		Assert.Equal("3.94398315", _desk.GetView(state).BondAmount);

		state = _desk.Toggle(state, SecondOfTheSameOrigin);
		Assert.Equal("2.74015752", _desk.GetView(state).BondAmount);
	}

	/// <summary>
	/// A picked coin of the same transaction as a larger picked coin is red. A private coin beyond the first three that
	/// are picked is orange: it only adds an input to pay for. The first three are counted along the list, the red one
	/// among them, as the design has it.
	/// </summary>
	[Fact]
	public void PickedCoinsThatDoNotCountAreMarked()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);

		state = _desk.Toggle(state, SecondOfTheSameOrigin);
		state = _desk.Toggle(state, Sixth);

		var marks = _desk.GetView(state).Coins.Where(row => row.IsSelected).ToDictionary(row => row.Coin.Id, row => row.Mark);

		Assert.Equal(5, marks.Count);
		Assert.Equal(CoinMark.None, marks[Largest]);
		Assert.Equal(CoinMark.SharedOrigin, marks[SecondOfTheSameOrigin]);
		Assert.Equal(CoinMark.None, marks[Third]);
		Assert.Equal(CoinMark.Extra, marks[Fourth]);
		Assert.Equal(CoinMark.Extra, marks[Sixth]);

		// What the offer can hold is the three largest of different origins, whatever else is picked:
		// 1.99759426 + 0.74256326 + 0.49768835.
		Assert.Equal(323_784_587, _desk.GetOfferFigures(state).Amount);
	}

	/// <summary>A coin without points is yellow in an offer, and cannot be locked at all.</summary>
	[Fact]
	public void CoinThatIsNotPrivateIsMarkedInAnOfferAndBarredFromABond()
	{
		MarketCoin[] wallet =
		[
			new("a", "t1", 50_000_000, 3m),
			new("b", "t2", 40_000_000, 0m),
			new("c", "t3", 30_000_000, 0.5m),
			new("d", "t4", 20_000_000, null)
		];
		var desk = new MakerDesk(Book, wallet, new HashSet<string>(), 0);

		// Lock Coins: only the coin with a point or more can be picked.
		var state = desk.Open();
		var rows = desk.GetView(state).Coins.ToDictionary(row => row.Coin.Id);

		Assert.Equal(["a"], state.Selected);
		Assert.False(rows["a"].IsBarred);
		Assert.True(rows["a"].CanToggle);
		Assert.All(new[] { "b", "c", "d" }, id => Assert.True(rows[id].IsBarred));
		Assert.All(new[] { "b", "c", "d" }, id => Assert.False(rows[id].CanToggle));
		Assert.Same(state, desk.Toggle(state, "b"));

		Assert.Equal(["a"], desk.ToggleAll(desk.Toggle(state, "a")).Selected);

		// List Offers: every coin can be picked, and the ones that are not private are marked.
		state = desk.SetTab(state, MakerTab.Offers);
		state = desk.Toggle(desk.Toggle(desk.Toggle(state, "b"), "c"), "d");
		rows = desk.GetView(state).Coins.ToDictionary(row => row.Coin.Id);

		Assert.Equal(CoinMark.None, rows["a"].Mark);
		Assert.All(new[] { "b", "c", "d" }, id => Assert.Equal(CoinMark.NotPrivate, rows[id].Mark));
		Assert.All(rows.Values, row => Assert.False(row.IsBarred));

		// Only the private coin counts for what is offered.
		Assert.Equal(50_000_000, desk.GetOfferFigures(state).Amount);
		Assert.Equal("3.0", desk.GetView(state).OfferPoints);
	}

	[Fact]
	public void BoxInTheHeadOfTheListPicksEveryCoinThatCanBePicked()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);

		Assert.Equal(SelectAllState.Some, _desk.GetView(state).SelectAll);

		state = _desk.ToggleAll(state);
		Assert.Equal(16, state.Selected.Count);
		Assert.Equal(SelectAllState.All, _desk.GetView(state).SelectAll);

		state = _desk.ToggleAll(state);
		Assert.Empty(state.Selected);
		Assert.Equal(SelectAllState.None, _desk.GetView(state).SelectAll);
	}

	/// <summary>A coin that is in an offer already cannot be picked for another, and the strategy passes it over.</summary>
	[Fact]
	public void CoinOfAnOfferIsNotPickedAgain()
	{
		var desk = new MakerDesk(Book, Wallet, new HashSet<string> { Largest, Third }, offered: 250_000_000);
		var state = desk.SetTab(desk.Open(), MakerTab.Offers);
		var rows = desk.GetView(state).Coins.ToDictionary(row => row.Coin.Id);

		Assert.True(rows[Largest].IsTaken);
		Assert.False(rows[Largest].CanToggle);
		Assert.False(rows[Largest].IsSelected);
		Assert.Same(state, desk.Toggle(state, Largest));

		Assert.Equal([SecondOfTheSameOrigin, Fourth, Sixth], state.Selected.Order(System.StringComparer.Ordinal));

		state = desk.ToggleAll(state);
		Assert.Equal(14, state.Selected.Count);
		Assert.Equal(SelectAllState.All, desk.GetView(state).SelectAll);
	}

	/// <summary>The bars of the split can be moved and typed into. That takes the marker out of the triangle.</summary>
	[Fact]
	public void SplitOfTheWalletCanBeSetByHand()
	{
		var state = _desk.SetBondShare(_desk.Open(), 0.25);
		var view = _desk.GetView(state);

		Assert.Null(state.Strategy);
		Assert.Equal(0.25, state.BondShare, 9);
		Assert.Equal(("75% of 5.8254 BTC", "4.3691 BTC"), (view.AllocationRows[0].Caption, view.AllocationRows[0].ValueText));
		Assert.Equal(("25% of 5.8254 BTC", "1.4564 BTC"), (view.AllocationRows[1].Caption, view.AllocationRows[1].ValueText));

		// A quarter of the wallet, 1.45635297 BTC, is covered by the largest coin alone.
		Assert.Equal([Largest], state.Selected);
		Assert.Equal("1.45635297", view.BondAmount);

		// An amount that is typed into a bar is a share of the wallet.
		state = _desk.SetAllocationAmount(state, isBond: true, "2.91270593");
		Assert.Equal(0.5, state.BondShare, 6);

		state = _desk.SetAllocationAmount(state, isBond: false, "5.82541186");
		Assert.Equal(0, state.BondShare, 6);

		Assert.Equal(1, _desk.SetBondShare(state, 7).BondShare);
		Assert.Same(state, _desk.SetAllocationAmount(state, isBond: true, "a lot"));
	}

	[Fact]
	public void RatesCanBeSetByHand()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);

		state = _desk.SetRateText(state, BookCriterion.FeeRate, "0.7");

		Assert.Null(state.Strategy);
		Assert.Equal("0.7", state.OfferFeeRate);
		Assert.Equal(("2.6", "1.3"), (state.OfferSellRate, state.OfferBuyRate));
		Assert.Same(state, _desk.SetRateText(state, BookCriterion.FeeRate, "cheap"));

		// A bar stands for a place in the book: the rate that undercuts that share of the makers.
		state = _desk.SetRatePosition(state, BookCriterion.SellRate, 70);
		Assert.Equal(MarketFormat.Rate(MakerStrategy.GetRateAt(Book, BookCriterion.SellRate, 0.7)), state.OfferSellRate);

		var row = _desk.GetView(state).RateRows.Single(candidate => candidate.Key == "sell");
		Assert.InRange(row.Percent, 65, 75);
		Assert.Equal($"beats {row.Percent}% of the book", row.Caption);

		Assert.Equal("0", _desk.SetRate(state, BookCriterion.BuyRate, -3m).OfferBuyRate);

		// The picked coins and the amount are not touched by the rates.
		Assert.Equal(3, state.Selected.Count);
	}

	[Fact]
	public void ListCanNameTheCoinsByTheirAddresses()
	{
		var state = _desk.Open();

		Assert.False(state.ShowsAddresses);
		Assert.True(_desk.ToggleAddresses(state).ShowsAddresses);
		Assert.False(_desk.ToggleAddresses(_desk.ToggleAddresses(state)).ShowsAddresses);
	}

	/// <summary>What the form of an offer says when it cannot be posted, one message at a time, in the order of the design.</summary>
	[Fact]
	public void OfferIsCheckedBeforeItIsPosted()
	{
		var open = _desk.SetTab(_desk.Open(), MakerTab.Offers);

		Assert.Equal("Select the coins to offer.", ErrorOfOffer(open with { Selected = [] }));
		Assert.Equal("Enter an amount to offer.", ErrorOfOffer(open with { OfferAmount = "" }));
		Assert.Equal("Enter an amount to offer.", ErrorOfOffer(open with { OfferAmount = "0" }));
		Assert.Equal(
			"Amount exceeds the counted coins: 5.00000000 BTC offered, 3.23784587 BTC across the three largest private coins from distinct origins.",
			ErrorOfOffer(open with { OfferAmount = "5" }));
		Assert.Equal("Enter a minimum fee rate.", ErrorOfOffer(open with { OfferFeeRate = "" }));
		Assert.Equal("Enter a minimum fee rate.", ErrorOfOffer(open with { OfferFeeRate = "0" }));
		Assert.Equal("Enter a sell rate.", ErrorOfOffer(open with { OfferSellRate = "" }));
		Assert.Equal("Enter a buy rate.", ErrorOfOffer(open with { OfferBuyRate = "" }));

		// What the market does not take, whatever was typed.
		Assert.Equal("The sell rate cannot be lower than the buy rate.", ErrorOfOffer(open with { OfferSellRate = "1", OfferBuyRate = "2" }));
		Assert.StartsWith("The fee rate is between", ErrorOfOffer(open with { OfferFeeRate = "0.01" }));

		// The first thing that is wrong is the one that is told.
		Assert.Equal("Select the coins to offer.", ErrorOfOffer(open with { Selected = [], OfferAmount = "", OfferFeeRate = "" }));
	}

	[Fact]
	public void OfferThatCanBePostedTellsItsCoinsItsAmountItsRatesAndItsPoints()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers) with { OfferError = "old" };

		var (draft, checkedState) = _desk.ValidateOffer(state);

		Assert.NotNull(draft);
		Assert.Null(checkedState.OfferError);
		Assert.Equal([Third, Largest, Fourth], draft.CoinIds.Order(System.StringComparer.Ordinal));
		Assert.Equal(291_270_593, draft.Amount);
		Assert.Equal((0.6m, 2.6m, 1.3m), (draft.FeeRate, draft.SellRate, draft.BuyRate));
		Assert.Equal(3, draft.MatchedInputs);
		Assert.Equal(8.44m, decimal.Round(draft.Points, 2));

		// A zero rate is a rate.
		(draft, _) = _desk.ValidateOffer(state with { OfferSellRate = "0", OfferBuyRate = "0" });
		Assert.NotNull(draft);
	}

	[Fact]
	public void BondIsCheckedBeforeItIsLocked()
	{
		var open = _desk.Open();

		Assert.Equal("Enter an amount to lock.", ErrorOfBond(open with { Selected = [], BondAmount = null }));
		Assert.Equal("Enter an amount to lock.", ErrorOfBond(open with { BondAmount = "" }));
		Assert.Equal("Enter an amount to lock.", ErrorOfBond(open with { BondAmount = "0" }));
		Assert.Equal(
			"Amount exceeds the selected coins: 9.00000000 BTC requested, 3.20141989 BTC selected.",
			ErrorOfBond(open with { BondAmount = "9" }));
		Assert.Equal("Enter a lock time in blocks.", ErrorOfBond(open with { BondBlocks = "" }));
		Assert.Equal("Enter a lock time in blocks.", ErrorOfBond(open with { BondBlocks = "0" }));
		Assert.Equal("Enter a fee rate for the bonding transaction.", ErrorOfBond(open with { BondFeeRate = "" }));
		Assert.Equal("Enter a fee rate for the bonding transaction.", ErrorOfBond(open with { BondFeeRate = "0" }));
	}

	/// <summary>What the question before the lock says: the amount, the lock with its length in words, and the fee.</summary>
	[Fact]
	public void BondThatCanBeLockedTellsWhatTheConfirmationSays()
	{
		var (draft, state) = _desk.ValidateBond(_desk.Open());

		Assert.NotNull(draft);
		Assert.Null(state.BondError);
		Assert.Equal([SecondOfTheSameOrigin, Largest], draft.CoinIds.Order());
		Assert.Equal(320_141_989, draft.SelectedTotal);
		Assert.Equal(291_270_593, draft.Amount);
		Assert.Equal(2016, draft.Blocks);
		Assert.Equal(1m, draft.FeeRate);

		Assert.Equal("Confirm Bond for 2.91270593 BTC", draft.Title);
		Assert.Equal("Locked for 2,016 blocks (2 weeks)", draft.LockText);

		// Two inputs, the bond and the rest: 11 + 2 × 57 + 2 × 43 virtual bytes.
		Assert.Equal("Fee rate 1 sat/vByte (211 sats)", draft.FeeText);

		// One input and no rest: 11 + 57 + 43 virtual bytes, at 2.5 sat/vB 277.5 sats.
		(draft, _) = _desk.ValidateBond(_desk.Toggle(_desk.Open(), SecondOfTheSameOrigin) with { BondBlocks = "144", BondFeeRate = "2.5" });

		Assert.NotNull(draft);
		Assert.Equal(199_759_426, draft.Amount);
		Assert.Equal(277.5m, draft.Estimate.Fee);
		Assert.Equal("Locked for 144 blocks (1 day)", draft.LockText);
		Assert.Equal("Fee rate 2.5 sat/vByte (278 sats)", draft.FeeText);

		// The wording of a lock of one block is the one of the design.
		(draft, _) = _desk.ValidateBond(_desk.Open() with { BondBlocks = "1" });
		Assert.Equal("Locked for 1 block (0.2 hours)", draft!.LockText);
	}

	/// <summary>After an offer the next three coins are picked, and the rates stay for the next offer.</summary>
	[Fact]
	public void NextCoinsArePickedAfterAnOffer()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);
		state = _desk.SetRateText(state, BookCriterion.FeeRate, "0.7") with { OfferError = "old" };

		var after = new MakerDesk(Book, Wallet, new HashSet<string> { Largest, Third, Fourth }, offered: 291_270_593);
		state = after.AfterOfferPosted(state);

		Assert.Equal(MakerTab.Offers, state.Tab);
		Assert.Equal([SecondOfTheSameOrigin, Sixth, FifthOfTheFourthsOrigin], state.Selected.Order(System.StringComparer.Ordinal));
		Assert.Equal("0.7", state.OfferFeeRate);
		Assert.Null(state.OfferError);
		Assert.Null(state.Strategy);

		// 1.20382563 + 0.30929659 + 0.24766418
		Assert.Equal("1.76078640", after.GetView(state).OfferAmount);
	}

	[Fact]
	public void ScreenMovesOnToListOffersAfterABond()
	{
		MarketCoin[] wallet = [new("bond", "b", 291_270_593, 0m, IsBond: true), .. Wallet.Where(coin => coin.Id != Largest && coin.Id != SecondOfTheSameOrigin)];
		var after = new MakerDesk(Book, wallet, new HashSet<string>(), 0);
		var state = after.AfterBondLocked(_desk.Open() with { BondBlocks = "5000", BondFeeRate = "3", BondError = "old" });

		Assert.True(after.HasBond);
		Assert.Equal(MakerTab.Offers, state.Tab);
		Assert.Equal(("2016", "1"), (state.BondBlocks, state.BondFeeRate));
		Assert.Null(state.BondError);
		Assert.Equal([Third, Fourth, Sixth], state.Selected.Order(System.StringComparer.Ordinal));

		var view = after.GetView(state);

		// The bond is on top of the list, and nothing picks it.
		Assert.Equal("bond", view.Coins[0].Coin.Id);
		Assert.False(view.Coins[0].CanToggle);
		Assert.Same(state, after.Toggle(state, "bond"));

		// A bond that exists is the split: the bars do not move it.
		Assert.False(view.IsAllocationEnabled);
		Assert.Same(state, after.SetBondShare(state, 0.9));
		Assert.Same(state, after.SetAllocationAmount(state, isBond: true, "1"));

		// With a bond the screen opens on List Offers.
		Assert.Equal(MakerTab.Offers, after.Open().Tab);
	}

	/// <summary>
	/// The wallet changes under the screen. When a picked coin is gone, the strategy picks again. Without the marker in
	/// the triangle, what is left stays picked and the amount follows it.
	/// </summary>
	[Fact]
	public void SelectionFollowsTheCoinsOfTheWallet()
	{
		var state = _desk.SetTab(_desk.Open(), MakerTab.Offers);
		MarketCoin[] without = [.. Wallet.Where(coin => coin.Id != Largest)];
		var later = new MakerDesk(Book, without, new HashSet<string>(), 0);

		// Nothing that was picked is gone: nothing changes.
		var same = new MakerDesk(Book, [.. Wallet, new MarketCoin("new", "t", 1_000, 0m)], new HashSet<string>(), 0);
		Assert.Same(state, same.Rebase(state));

		// With the marker: the three largest of different origins among what is left.
		var rebased = later.Rebase(state);
		Assert.Equal(TriangleWeights.Centre, rebased.Strategy);
		Assert.Equal([SecondOfTheSameOrigin, Third, Fourth], rebased.Selected.Order(System.StringComparer.Ordinal));

		// Half of the 3.82781760 BTC that are left.
		Assert.Equal("1.91390880", later.GetView(rebased).OfferAmount);

		// Without the marker.
		var byHand = _desk.SetRateText(state, BookCriterion.FeeRate, "0.7") with { OfferAmount = "2" };
		rebased = later.Rebase(byHand);

		Assert.Null(rebased.Strategy);
		Assert.Equal([Third, Fourth], rebased.Selected.Order(System.StringComparer.Ordinal));
		Assert.Equal("1.24025161", later.GetView(rebased).OfferAmount);
		Assert.Equal("0.7", rebased.OfferFeeRate);

		// A coin that went into an offer meanwhile is not picked any more either.
		var taken = new MakerDesk(Book, Wallet, new HashSet<string> { Third }, 0);
		Assert.DoesNotContain(Third, taken.Rebase(byHand).Selected);
	}

	/// <summary>Without a book the triangle has no rates to give: it sets the split, and the rates are typed.</summary>
	[Fact]
	public void WithoutABookTheTriangleSetsTheSplitOnly()
	{
		var desk = new MakerDesk([], Wallet, new HashSet<string>(), 0);
		var state = desk.SetTab(desk.Open(), MakerTab.Offers);

		Assert.Equal(("", "", ""), (state.OfferFeeRate, state.OfferSellRate, state.OfferBuyRate));
		Assert.Equal(3, state.Selected.Count);
		Assert.Equal("Enter a minimum fee rate.", desk.ValidateOffer(state).State.OfferError);

		state = desk.SetRateText(desk.SetRateText(desk.SetRateText(state, BookCriterion.FeeRate, "1.5"), BookCriterion.SellRate, "300"), BookCriterion.BuyRate, "120");

		// What was typed stays when the marker is moved.
		state = desk.ApplyStrategy(state, new TriangleWeights(1, 0, 0));
		Assert.Equal(("1.5", "300", "120"), (state.OfferFeeRate, state.OfferSellRate, state.OfferBuyRate));
		Assert.NotNull(desk.ValidateOffer(state).Draft);
	}

	[Theory]
	[InlineData("1.25", null, "1.25")]
	[InlineData(" 1,250.5 BTC", null, "1250.5")]
	[InlineData("1.2.3", null, "1.23")]
	[InlineData("0.123456789", 8, "0.12345678")]
	[InlineData("2.67", 1, "2.6")]
	[InlineData("2016 blocks", 0, "2016")]
	[InlineData("20.16", 0, "2016")]
	[InlineData("-5", null, "5")]
	[InlineData("abc", null, "")]
	public void TypedNumberKeepsItsDigitsAndOnePoint(string typed, int? maxDecimals, string expected)
	{
		Assert.Equal(expected, MakerDesk.CleanNumber(typed, maxDecimals));
	}

	private string? ErrorOfOffer(MakerDeskState state)
	{
		var (draft, checkedState) = _desk.ValidateOffer(state);

		Assert.Null(draft);
		return checkedState.OfferError;
	}

	private string? ErrorOfBond(MakerDeskState state)
	{
		var (draft, checkedState) = _desk.ValidateBond(state);

		Assert.Null(draft);
		return checkedState.BondError;
	}
}
