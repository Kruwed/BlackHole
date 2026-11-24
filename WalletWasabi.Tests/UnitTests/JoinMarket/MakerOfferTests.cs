using NBitcoin;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The offer of a maker: coins that a taker may put into its coinjoin, a fee rate that the transaction has to pay at
/// least, and the two rates of an anonymity point.
/// </summary>
public class MakerOfferTests
{
	private static readonly MakerOffer Offer = new(MinFeeRate: 2m, BuyRate: 100m, SellRate: 150m, Liquidity: 50_000_000, Points: 2.5m, MatchedInputs: 3, ExtraInputs: 1);

	[Fact]
	public void SpreadIsWhatLiesBetweenTheTwoRates()
	{
		Assert.Equal(50m, Offer.Spread);
		Assert.Equal(0m, (Offer with { SellRate = 100m }).Spread);
		Assert.Equal(0.5m, (Offer with { BuyRate = 149.5m }).Spread);
	}

	[Fact]
	public void CoinsOfAnOfferAreTheOnesThatCountAsMatchedAndTheOthers()
	{
		Assert.Equal(4, Offer.CoinCount);
		Assert.Equal(1, (Offer with { MatchedInputs = 1, ExtraInputs = 0 }).CoinCount);
	}

	/// <summary>
	/// A maker that pays more for a point than it sells one for would pay a taker for giving it points and taking
	/// them back again. Such an offer is not made.
	/// </summary>
	[Fact]
	public void SellRateIsNotBelowTheBuyRate()
	{
		Assert.Null(MakerOffer.ValidateTerms(1m, buyRate: 100m, sellRate: 100m));
		Assert.Null(MakerOffer.ValidateTerms(1m, buyRate: 0m, sellRate: 0m));
		Assert.Equal("The sell rate cannot be lower than the buy rate.", MakerOffer.ValidateTerms(1m, buyRate: 100m, sellRate: 99.9m));
	}

	[Theory]
	[InlineData("0.1", "0", "0", true)]
	[InlineData("10000", "0", "0", true)]
	[InlineData("0.099", "0", "0", false)] // Below what nodes relay
	[InlineData("10000.1", "0", "0", false)]
	[InlineData("1", "-1", "0", false)]
	[InlineData("1", "0", "-0.1", false)]
	[InlineData("1", "0.5", "0.5", true)] // A rate is not a whole number of sats
	[InlineData("1", "5", "100000000", true)]
	[InlineData("1", "5", "100000000.1", false)]
	public void TermsHaveTheirLimits(string feeRate, string buyRate, string sellRate, bool isValid)
	{
		Assert.Equal(isValid, MakerOffer.ValidateTerms(Parse(feeRate), Parse(buyRate), Parse(sellRate)) is null);
	}

	[Fact]
	public void OfferHasCoinsInIt()
	{
		Assert.Null(Offer.Validate());

		// What an output cannot hold is no liquidity.
		Assert.Null((Offer with { Liquidity = 330 }).Validate());
		Assert.NotNull((Offer with { Liquidity = 329 }).Validate());
		Assert.NotNull((Offer with { Liquidity = 0 }).Validate());
		Assert.NotNull((Offer with { Liquidity = -1 }).Validate());

		// Every bitcoin there is, and no more.
		Assert.Null((Offer with { Liquidity = 2_099_999_997_690_000 }).Validate());
		Assert.NotNull((Offer with { Liquidity = 2_099_999_997_690_001 }).Validate());

		Assert.NotNull((Offer with { SellRate = 99m }).Validate());
	}

	[Theory]
	[InlineData(1, 0, true)]
	[InlineData(3, 0, true)]
	[InlineData(3, 17, true)] // Twenty coins in all
	[InlineData(0, 1, true)] // Coins that are not private count for nothing, and can be offered all the same
	[InlineData(0, 0, false)]
	[InlineData(4, 0, false)] // Three coins of a maker count as matched, and no more
	[InlineData(3, 18, false)]
	[InlineData(-1, 2, false)]
	[InlineData(1, -1, false)]
	public void OfferHasOneToTwentyCoinsOfWhichThreeCountAtMost(int matched, int extra, bool isValid)
	{
		Assert.Equal(isValid, (Offer with { MatchedInputs = matched, ExtraInputs = extra }).Validate() is null);
	}

	[Theory]
	[InlineData("0", true)]
	[InlineData("0.5", true)]
	[InlineData("1000000", true)]
	[InlineData("-0.5", false)]
	[InlineData("1000000.5", false)]
	public void PointsOfAnOfferAreZeroOrMore(string points, bool isValid)
	{
		Assert.Equal(isValid, (Offer with { Points = Parse(points) }).Validate() is null);
	}

	[Fact]
	public void ProofOfABondIsWellFormedOrTheOfferIsNone()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, new OutPoint(uint256.One, 0), 900_000, new byte[32]);

		Assert.Null((Offer with { Bond = proof }).Validate());
		Assert.NotNull((Offer with { Bond = proof with { Signature = "00" } }).Validate());
		Assert.NotNull((Offer with { Bond = proof with { Key = "not a key" } }).Validate());
		Assert.NotNull((Offer with { Bond = proof with { UnlockHeight = 0 } }).Validate());
	}

	private static decimal Parse(string number) => decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
}
