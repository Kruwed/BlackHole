using NBitcoin;
using WalletWasabi.Helpers;

namespace WalletWasabi.JoinMarket;

/// <summary>
/// What a market maker offers to the takers of JoinMarket-style coinjoins: the use of some of its coins in the coinjoin
/// of a taker, at a price that depends on what that coinjoin does to the anonymity points of the maker.
/// </summary>
/// <remarks>
/// The anonymity points of a coin are destroyed when the coin is spent. So the points of the coins that the maker puts
/// into the coinjoin of a taker are gone, and the coins that the maker gets out of it have the points of the new
/// transaction in their place. That links the two coinjoins like a swap, in which points are bought and sold: for every
/// point that the maker has more afterwards it pays the taker the <see cref="Bid"/>, and for every point that it has
/// less the taker pays it the <see cref="Ask"/>.
/// </remarks>
/// <param name="MinFeeRate">The lowest mining fee rate of a coinjoin that the maker takes part in.</param>
/// <param name="Bid">What the maker pays for an anonymity point that it gains.</param>
/// <param name="Ask">What the maker wants for an anonymity point that it loses.</param>
/// <param name="Liquidity">The amount of the coins that the maker offers.</param>
public record MakerOffer(FeeRate MinFeeRate, Money Bid, Money Ask, Money Liquidity)
{
	/// <summary>The most coins that an offer holds.</summary>
	public const int MaxCoinCount = 2;

	/// <summary>What the maker earns when it sells a point and buys it back.</summary>
	public Money Spread => Ask - Bid;

	/// <summary>Tells why these are not the terms of an offer, or null if they are.</summary>
	public static string? ValidateTerms(FeeRate minFeeRate, Money bid, Money ask)
	{
		if (minFeeRate < Constants.MinRelayFeeRate || minFeeRate > Constants.AbsurdlyHighFeeRate)
		{
			return FormattableString.Invariant($"The fee rate is between {Constants.MinRelayFeeRate.SatoshiPerByte:0.###} and {Constants.AbsurdlyHighFeeRate.SatoshiPerByte:0} sat/vB.");
		}

		if (bid < Money.Zero || bid > Constants.MaximumNumberOfBitcoinsMoney)
		{
			return "The bid is zero or more sats.";
		}

		if (ask < Money.Zero || ask > Constants.MaximumNumberOfBitcoinsMoney)
		{
			return "The ask is zero or more sats.";
		}

		// A maker that pays more for a point than it sells one for can be emptied by a taker that gives it points and takes them back.
		if (ask < bid)
		{
			return "The ask cannot be lower than the bid.";
		}

		return null;
	}

	/// <summary>Tells why this is not an offer that can be made, or null if it is.</summary>
	public string? Validate() =>
		ValidateTerms(MinFeeRate, Bid, Ask) is { } reason
			? reason
			: Liquidity <= Money.Zero || Liquidity > Constants.MaximumNumberOfBitcoinsMoney
				? "An offer has coins in it."
				: null;

	/// <summary>
	/// What the taker of a coinjoin pays the maker for what the coinjoin did to the anonymity points of the maker.
	/// A negative amount is what the maker pays the taker.
	/// </summary>
	/// <param name="pointsBefore">The anonymity points of the coins that the maker put into the coinjoin. They are destroyed by it.</param>
	/// <param name="pointsAfter">The anonymity points of the coins that the maker got out of the coinjoin.</param>
	public Money GetCompensation(int pointsBefore, int pointsAfter)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(pointsBefore);
		ArgumentOutOfRangeException.ThrowIfNegative(pointsAfter);

		long difference = (long)pointsAfter - pointsBefore;

		return difference >= 0
			? Money.Satoshis(checked(-Bid.Satoshi * difference))
			: Money.Satoshis(checked(Ask.Satoshi * -difference));
	}
}
