using System.Collections.Immutable;
using System.Linq;
using NBitcoin;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.Fluent.ViewModels.Wallets.Tabs;
using WalletWasabi.JoinMarket;
using WalletWasabi.JoinMarket.OrderBook;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// The tab of the order book: the makers that the Nostr relays tell of, with what they offer, and the offers of this
/// wallet among them. It is the book to look at; makers are picked on the Taker tab of the Coinjoin dialog.
/// </summary>
public class OrderBookTabTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

	[Fact]
	public void BookHasTheMakersWithABondFirstAndCountsTheOthers()
	{
		var cheap = Key('1');
		var dear = Key('2');
		var plain = Key('3');
		var other = Key('4');
		var book = new OrderBookSnapshot(
			[
				new BookEntry(dear, Offer(minFeeRate: 5m), Bond: 10_000_000, Now),
				new BookEntry(plain, Offer(minFeeRate: 1m), Bond: 0, Now),
				new BookEntry(cheap, Offer(minFeeRate: 2m), Bond: 30_000_000, Now),
				new BookEntry(other, Offer(minFeeRate: 3m), Bond: 0, Now)
			],
			Now,
			[],
			BondProblem: null);

		var rows = OrderBookTabViewModel.CreateRows(book, [], ownBond: 0);

		// The lowest fee rate first within each part.
		Assert.Equal(5, rows.Count);
		Assert.Equal(MakerAlias.Get(cheap), Assert.IsType<BookRowViewModel>(rows[0]).Alias);
		Assert.Equal(MakerAlias.Get(dear), Assert.IsType<BookRowViewModel>(rows[1]).Alias);
		Assert.Equal("2 Unbonded Makers", Assert.IsType<BookCaptionViewModel>(rows[2]).Text);
		Assert.Equal(MakerAlias.Get(plain), Assert.IsType<BookRowViewModel>(rows[3]).Alias);
		Assert.Equal(MakerAlias.Get(other), Assert.IsType<BookRowViewModel>(rows[4]).Alias);

		var first = (BookRowViewModel)rows[0];
		Assert.Equal(("2.0", "50", "150", "100", "0.50000000", "0.30000000"), (first.FeeRate, first.Spread, first.SellRate, first.BuyRate, first.Liquidity, first.Bond));
		Assert.False(first.IsOwn);
		Assert.Null(first.ToggleCommand);
		Assert.Equal(MarketFormat.None, ((BookRowViewModel)rows[3]).Bond);

		// Nothing is filtered here: no row is greyed out.
		Assert.All(rows.OfType<BookRowViewModel>(), row => Assert.True(row.IsAvailable));
	}

	/// <summary>The offers of this wallet are in the book with a tag, and with the bond of the wallet.</summary>
	[Fact]
	public void OffersOfTheWalletAreInTheBookWithATag()
	{
		var maker = Key('1');
		var mine = Key('2');
		var book = new OrderBookSnapshot([new BookEntry(maker, Offer(minFeeRate: 2m), Bond: 0, Now)], Now, [], null);
		var posted = new PostedOffer(1, mine, [new OutPoint(uint256.One, 0)], 60_000_000, Offer(minFeeRate: 1m), Bond: 20_000_000, Now, Problem: null);

		var rows = OrderBookTabViewModel.CreateRows(book, [posted], ownBond: 20_000_000);

		var own = Assert.IsType<BookRowViewModel>(rows[0]);
		Assert.True(own.IsOwn);
		Assert.Equal(posted.Alias, own.Alias);
		Assert.Equal("0.20000000", own.Bond);
		Assert.Equal("1 Unbonded Maker", Assert.IsType<BookCaptionViewModel>(rows[1]).Text);
		Assert.False(Assert.IsType<BookRowViewModel>(rows[2]).IsOwn);

		// Without a bond the offer of the wallet is among the makers without one.
		rows = OrderBookTabViewModel.CreateRows(book, [posted], ownBond: 0);
		Assert.Equal("2 Unbonded Makers", Assert.IsType<BookCaptionViewModel>(rows[0]).Text);
		Assert.True(Assert.IsType<BookRowViewModel>(rows[1]).IsOwn);
	}

	/// <summary>A maker without anonymity points has none to sell: its row has a sell rate of 0, and its spread is below zero.</summary>
	[Fact]
	public void MakerWithoutPointsIsListedWithASellRateOfZero()
	{
		var pointless = new MakerOffer(2m, BuyRate: 100m, SellRate: 0m, Liquidity: 50_000_000, Points: 0m, MatchedInputs: 2, ExtraInputs: 0);
		var book = new OrderBookSnapshot([new BookEntry(Key('1'), pointless, Bond: 0, Now)], Now, [], null);

		var row = OrderBookTabViewModel.CreateRows(book, [], ownBond: 0).OfType<BookRowViewModel>().Single();

		Assert.Equal(("0", "100", "−100"), (row.SellRate, row.BuyRate, row.Spread));
	}

	/// <summary>Two makers that their keys give the same name are told apart by the start of their keys.</summary>
	[Fact]
	public void MakersOfOneNameAreToldApart()
	{
		var first = "0000" + "11" + new string('a', 58);
		var second = "0000" + "12" + new string('a', 58);
		var book = new OrderBookSnapshot([new BookEntry(first, Offer(1m), 0, Now), new BookEntry(second, Offer(2m), 0, Now)], Now, [], null);

		var rows = OrderBookTabViewModel.CreateRows(book, [], 0).OfType<BookRowViewModel>().ToArray();

		Assert.Equal(["amber-heron-000011", "amber-heron-000012"], rows.Select(row => row.Alias));
	}

	[Fact]
	public void TabSaysWhatThereIsToSayAboutTheBook()
	{
		var relay = new Uri("wss://relay.example");
		var entry = new BookEntry(Key('1'), Offer(1m), 0, Now);

		Assert.Equal("Asking your Nostr relays for offers…", OrderBookTabViewModel.DescribeBook(OrderBookSnapshot.Empty));
		Assert.Equal(
			"Your Nostr relays have no offers of other Makers.",
			OrderBookTabViewModel.DescribeBook(new OrderBookSnapshot([], Now, [new BookRelay(relay, null)], null)));
		Assert.Equal(
			"No Nostr relay answered: relay.example: Connection refused",
			OrderBookTabViewModel.DescribeBook(new OrderBookSnapshot([], Now, [new BookRelay(relay, "Connection refused")], null)));
		Assert.Equal(
			"There is no Nostr relay to ask.",
			OrderBookTabViewModel.DescribeBook(new OrderBookSnapshot([], Now, [], null)));

		// A book with offers has nothing to say, unless the node could not be asked about the bonds.
		Assert.Null(OrderBookTabViewModel.DescribeBook(new OrderBookSnapshot([entry], Now, [new BookRelay(relay, null)], null)));
		Assert.Equal(
			"The Bitcoin node could not be asked about the fidelity bonds: Connection refused",
			OrderBookTabViewModel.DescribeBook(new OrderBookSnapshot([entry], Now, [new BookRelay(relay, null)], "The Bitcoin node could not be asked about the fidelity bonds: Connection refused")));
	}

	[Fact]
	public void ColumnsAreTheOnesOfTheDesign()
	{
		Assert.Equal(["Maker", "Fee rate", "Spread", "Sell rate", "Buy rate", "Liquidity", "Bond"], OrderBookTabViewModel.Columns);
		Assert.Equal(System.TimeSpan.FromSeconds(60), OrderBookTabViewModel.RefreshPeriod);
	}

	private static MakerOffer Offer(decimal minFeeRate) =>
		new(minFeeRate, BuyRate: 100m, SellRate: 150m, Liquidity: 50_000_000, Points: 2m, MatchedInputs: 2, ExtraInputs: 0);

	private static string Key(char digit) => new(digit, 64);
}
