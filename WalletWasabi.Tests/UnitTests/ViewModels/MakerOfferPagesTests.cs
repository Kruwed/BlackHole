using System.Collections.Immutable;
using NBitcoin;
using WalletWasabi.Fluent.ViewModels.Wallets.Offers;
using WalletWasabi.Fluent.ViewModels.Wallets.Tabs;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the main screen of the Maker tab of the Coinjoin dialog takes as the terms of an offer, which coins it offers,
/// and how the list of market makers shows the offer of the wallet.
/// </summary>
public class MakerOfferPagesTests
{
	/// <summary>A price of an anonymity point is a whole number of sats.</summary>
	[Theory]
	[InlineData("0", 0L)]
	[InlineData("100", 100L)]
	[InlineData(" 150 ", 150L)]
	[InlineData("1,000", 1_000L)]
	[InlineData("1 500 000", 1_500_000L)]
	[InlineData("2099999997690000", 2_099_999_997_690_000L)]
	public void PriceIsAWholeNumberOfSats(string text, long expectedSats)
	{
		Assert.Equal(Money.Satoshis(expectedSats), MakerOfferViewModel.ParsePrice(text));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("-1")]
	[InlineData("1.5")]
	[InlineData("1e3")]
	[InlineData("0x10")]
	[InlineData("ten")]
	[InlineData("2099999997690001")] // More than there is
	[InlineData("99999999999999999999999999")]
	public void WhatIsNoWholeNumberOfSatsIsNoPrice(string text)
	{
		Assert.Null(MakerOfferViewModel.ParsePrice(text));
	}

	[Fact]
	public void TermsAreReadAsTheyAreTyped()
	{
		var terms = MakerOfferViewModel.ParseTerms(" 2.5 ", "100", "1,500");

		Assert.NotNull(terms);
		Assert.Equal(new FeeRate(2.5m), terms.Value.MinFeeRate);
		Assert.Equal(Money.Satoshis(100), terms.Value.Bid);
		Assert.Equal(Money.Satoshis(1_500), terms.Value.Ask);

		// The lowest fee rate, and prices of nothing.
		Assert.NotNull(MakerOfferViewModel.ParseTerms("0.1", "0", "0"));
	}

	[Theory]
	[InlineData("", "100", "150")]
	[InlineData("2.5", "", "150")]
	[InlineData("2.5", "100", "")]
	[InlineData("0.09", "100", "150")] // Below what nodes relay
	[InlineData("10001", "100", "150")]
	[InlineData("2,5", "100", "150")]
	[InlineData("fast", "100", "150")]
	[InlineData("2.5", "150", "100")] // The ask is below the bid
	[InlineData("2.5", "1.5", "150")]
	public void TermsThatMakeNoOfferAreNotTaken(string minFeeRate, string bid, string ask)
	{
		Assert.Null(MakerOfferViewModel.ParseTerms(minFeeRate, bid, ask));
	}

	/// <summary>The page shows the offer that stands in the way it reads one, so that it can be posted again as it is.</summary>
	[Fact]
	public void StandingOfferIsShownAsItIsTyped()
	{
		var feeRate = MakerOfferViewModel.FormatFeeRate(new FeeRate(2.5m));
		var bid = MakerOfferViewModel.FormatPrice(Money.Satoshis(1_000));
		var ask = MakerOfferViewModel.FormatPrice(Money.Satoshis(1_500_000));

		Assert.Equal("2.5", feeRate);
		Assert.Equal("1000", bid);
		Assert.Equal("1500000", ask);
		Assert.Equal((new FeeRate(2.5m), Money.Satoshis(1_000), Money.Satoshis(1_500_000)), MakerOfferViewModel.ParseTerms(feeRate, bid, ask));

		Assert.Equal("2", MakerOfferViewModel.FormatFeeRate(new FeeRate(2m)));
		Assert.Equal("0.123", MakerOfferViewModel.FormatFeeRate(new FeeRate(0.123m)));
	}

	/// <summary>An offer offers the coins that are ticked on the Coins tab: one or two of them.</summary>
	[Theory]
	[InlineData(1, 0)]
	[InlineData(2, 0)]
	[InlineData(1, 2)] // An offer stands: the ticked coin takes the place of its two.
	[InlineData(2, 1)]
	public void OfferGetsTheTickedCoins(int tickedCount, int standingCount)
	{
		Assert.Equal(OfferCoinsSource.Ticked, MakerOfferViewModel.ChooseCoins(tickedCount, standingCount));
	}

	/// <summary>
	/// With no coin ticked, the offer that stands keeps its coins. Its terms can be changed that way without looking
	/// for its coins in the list again.
	/// </summary>
	[Fact]
	public void StandingOfferKeepsItsCoinsWhenNoCoinIsTicked()
	{
		Assert.Equal(OfferCoinsSource.StandingOffer, MakerOfferViewModel.ChooseCoins(tickedCount: 0, standingCount: 1));
		Assert.Equal(OfferCoinsSource.StandingOffer, MakerOfferViewModel.ChooseCoins(tickedCount: 0, standingCount: 2));

		// Without an offer and without a tick there is nothing to offer.
		Assert.Equal(OfferCoinsSource.None, MakerOfferViewModel.ChooseCoins(tickedCount: 0, standingCount: 0));
	}

	/// <summary>An offer holds two coins at most. More ticked coins are not cut down to two: the user decides which two.</summary>
	[Theory]
	[InlineData(3, 0)]
	[InlineData(3, 2)]
	[InlineData(40, 1)]
	public void MoreTickedCoinsThanAnOfferHoldsMakeNoOffer(int tickedCount, int standingCount)
	{
		Assert.Equal(OfferCoinsSource.TooMany, MakerOfferViewModel.ChooseCoins(tickedCount, standingCount));
	}

	[Fact]
	public void PageTellsWhichCoinsTheOfferGetsOrWhatIsToBeDoneFirst()
	{
		Assert.Equal(
			"The offer holds the coin that is ticked on the Coins tab.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 1, standingCount: 0));
		Assert.Equal(
			"The offer holds the 2 coins that are ticked on the Coins tab.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 2, standingCount: 0));
		Assert.Equal(
			"The offer gets the 2 coins that are ticked on the Coins tab, in place of what it holds now.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 2, standingCount: 1));
		Assert.Equal(
			"No coin is ticked on the Coins tab, so the offer keeps the coin that it holds now.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 0, standingCount: 1));
		Assert.Equal(
			"No coin is ticked on the Coins tab, so the offer keeps the 2 coins that it holds now.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 0, standingCount: 2));
		Assert.Equal(
			"No coin is ticked. Close this dialog, tick one or two coins on the Coins tab and come back.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 0, standingCount: 0));
		Assert.Equal(
			"An offer holds two coins at most, and 3 are ticked on the Coins tab. Coins on the same address are ticked together.",
			MakerOfferViewModel.DescribeCoins(tickedCount: 3, standingCount: 2));
	}

	/// <summary>
	/// The list of market makers has the columns Market Maker, Fee Rate, Spread, Bid, Ask, Liquidity and Bond.
	/// The offer of this wallet is a row of it. An offer names no bond yet.
	/// </summary>
	[Fact]
	public void OfferOfTheWalletIsARowOfTheMarketMakers()
	{
		var offer = new MakerOffer(new FeeRate(2.5m), Bid: Money.Satoshis(100), Ask: Money.Satoshis(1_150), Liquidity: Money.Coins(0.5m));
		var state = new MakerOfferState([new OutPoint(uint256.One, 0)], offer, DateTimeOffset.UnixEpoch, Problem: null);

		var row = OrderBookTabViewModel.CreateRow(state);

		Assert.Equal("This wallet", row.Maker);
		Assert.Equal("2.5 sat/vB", row.FeeRate);
		Assert.Equal("1,050 sats", row.Spread);
		Assert.Equal("100 sats", row.Bid);
		Assert.Equal("1,150 sats", row.Ask);
		Assert.Equal("0.50 000 000 BTC", row.Liquidity);
		Assert.Equal("", row.Bond);
		Assert.False(row.HasProblem);
	}

	[Fact]
	public void RowTellsWhenTheRelaysCouldNotBeTold()
	{
		var offer = new MakerOffer(new FeeRate(1m), Money.Zero, Money.Satoshis(1), Money.Coins(1));
		var state = new MakerOfferState([new OutPoint(uint256.One, 0)], offer, DateTimeOffset.UnixEpoch, "No Nostr relay took it: relay.example: refused");

		var row = OrderBookTabViewModel.CreateRow(state);

		Assert.True(row.HasProblem);
		Assert.Equal("No Nostr relay took it: relay.example: refused", row.Problem);

		// One sat is one sat.
		Assert.Equal("0 sats", row.Bid);
		Assert.Equal("1 sat", row.Ask);
		Assert.Equal("1 sat", row.Spread);
	}
}
