using NBitcoin;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The offer of a market maker: coins that a taker may put into its coinjoin, and the two prices of an anonymity point.
/// </summary>
public class MakerOfferTests
{
	private static readonly MakerOffer Offer = new(new FeeRate(2m), Bid: Money.Satoshis(100), Ask: Money.Satoshis(150), Liquidity: Money.Coins(0.5m));

	/// <summary>
	/// The coinjoin of a taker destroys the points of the coins that the maker puts in, and what the maker gets out has
	/// the points of the new transaction. For every point that the maker has more afterwards it pays the bid.
	/// </summary>
	[Theory]
	[InlineData(0, 1, -100)]
	[InlineData(0, 5, -500)]
	[InlineData(3, 4, -100)]
	[InlineData(2, 10, -800)]
	public void MakerPaysTheBidForThePointsThatItGains(int pointsBefore, int pointsAfter, long expectedSats)
	{
		Assert.Equal(Money.Satoshis(expectedSats), Offer.GetCompensation(pointsBefore, pointsAfter));
	}

	/// <summary>For every point that the maker has less afterwards, the taker pays the ask.</summary>
	[Theory]
	[InlineData(1, 0, 150)]
	[InlineData(5, 0, 750)]
	[InlineData(4, 3, 150)]
	[InlineData(10, 2, 1200)]
	public void TakerPaysTheAskForThePointsThatTheMakerLoses(int pointsBefore, int pointsAfter, long expectedSats)
	{
		Assert.Equal(Money.Satoshis(expectedSats), Offer.GetCompensation(pointsBefore, pointsAfter));
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(7, 7)]
	public void NobodyPaysWhenThePointsStayTheSame(int pointsBefore, int pointsAfter)
	{
		Assert.Equal(Money.Zero, Offer.GetCompensation(pointsBefore, pointsAfter));
	}

	[Fact]
	public void PointsAreNotNegative()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => Offer.GetCompensation(-1, 0));
		Assert.Throws<ArgumentOutOfRangeException>(() => Offer.GetCompensation(0, -1));
	}

	[Fact]
	public void SpreadIsWhatLiesBetweenTheTwoPrices()
	{
		Assert.Equal(Money.Satoshis(50), Offer.Spread);
		Assert.Equal(Money.Zero, (Offer with { Ask = Money.Satoshis(100) }).Spread);
	}

	/// <summary>
	/// A maker that bids more for a point than it asks for one would pay a taker for giving it points and taking
	/// them back again. Such an offer is not made.
	/// </summary>
	[Fact]
	public void AskIsNotBelowTheBid()
	{
		Assert.Null(MakerOffer.ValidateTerms(new FeeRate(1m), Money.Satoshis(100), Money.Satoshis(100)));
		Assert.Null(MakerOffer.ValidateTerms(new FeeRate(1m), Money.Zero, Money.Zero));
		Assert.Equal("The ask cannot be lower than the bid.", MakerOffer.ValidateTerms(new FeeRate(1m), Money.Satoshis(100), Money.Satoshis(99)));
	}

	[Theory]
	[InlineData("0.1", 0, 0, true)]
	[InlineData("10000", 0, 0, true)]
	[InlineData("0.099", 0, 0, false)] // Below what nodes relay
	[InlineData("10000.1", 0, 0, false)]
	[InlineData("1", -1, 0, false)]
	[InlineData("1", 0, -1, false)]
	[InlineData("1", 5, 2_099_999_997_690_000, true)] // Every bitcoin there is
	[InlineData("1", 5, 2_099_999_997_690_001, false)]
	public void TermsHaveTheirLimits(string satsPerVirtualByte, long bid, long ask, bool isValid)
	{
		var feeRate = new FeeRate(decimal.Parse(satsPerVirtualByte, System.Globalization.CultureInfo.InvariantCulture));

		Assert.Equal(isValid, MakerOffer.ValidateTerms(feeRate, Money.Satoshis(bid), Money.Satoshis(ask)) is null);
	}

	[Fact]
	public void OfferHasCoinsInIt()
	{
		Assert.Null(Offer.Validate());
		Assert.NotNull((Offer with { Liquidity = Money.Zero }).Validate());
		Assert.NotNull((Offer with { Liquidity = Money.Satoshis(-1) }).Validate());
		Assert.NotNull((Offer with { Ask = Money.Satoshis(99) }).Validate());
	}
}
