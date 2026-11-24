using WalletWasabi.FidelityBonds;
using WalletWasabi.Helpers;
using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket;

/// <summary>
/// What a maker offers to the takers of coinjoins: the use of some of its coins in the coinjoin of a taker, at a price
/// that depends on what that coinjoin does to the anonymity points of the maker.
/// </summary>
/// <remarks>
/// The anonymity points of a coin are destroyed when the coin is spent. So the points of the coins that the maker puts
/// into the coinjoin of a taker are gone, and the coins that the maker gets out of it have the points of the new
/// transaction in their place. That links the two coinjoins like a swap, in which points are bought and sold: for
/// every point that the new transaction is worth more than the coins of the maker were, the maker pays the taker its
/// <see cref="BuyRate"/>, and for every point that it is worth less, the taker pays the maker its
/// <see cref="SellRate"/>, see <see cref="MarketRules.GetSettlement"/>.
/// </remarks>
/// <param name="MinFeeRate">The lowest mining fee rate of a coinjoin that the maker takes part in, in sats per virtual byte.</param>
/// <param name="BuyRate">What the maker pays for an anonymity point that it gains, in sats.</param>
/// <param name="SellRate">What the maker wants for an anonymity point that it loses, in sats.</param>
/// <param name="Liquidity">The amount that the maker offers, in sats.</param>
/// <param name="Points">The anonymity points of the coins behind the offer: the mean of the points of the coins that count, weighted by their amounts.</param>
/// <param name="MatchedInputs">How many coins of the offer count as matched inputs: the private ones, three at most. The taker pays for them.</param>
/// <param name="ExtraInputs">How many more coins the maker brings. It pays for them itself.</param>
/// <param name="Bond">The proof of the fidelity bond that stands behind the offer, if one does.</param>
public sealed record MakerOffer(
	decimal MinFeeRate,
	decimal BuyRate,
	decimal SellRate,
	long Liquidity,
	decimal Points,
	int MatchedInputs,
	int ExtraInputs,
	BondProof? Bond = null)
{
	/// <summary>The most coins that an offer holds, the ones that only add inputs included.</summary>
	public const int MaxCoinCount = 20;

	/// <summary>No rate of a point is higher than this, in sats: what an offer says has to stay a number that can be reckoned with.</summary>
	public const decimal MaxRate = 100_000_000m;

	/// <summary>No offer tells more points than this.</summary>
	public const decimal MaxPoints = 1_000_000m;

	/// <summary>What the maker earns when it sells a point and buys it back.</summary>
	public decimal Spread => SellRate - BuyRate;

	public int CoinCount => MatchedInputs + ExtraInputs;

	/// <summary>Tells why these are not the terms of an offer, or null if they are.</summary>
	public static string? ValidateTerms(decimal minFeeRate, decimal buyRate, decimal sellRate)
	{
		var lowest = Constants.MinRelayFeeRate.SatoshiPerByte;
		var highest = Constants.AbsurdlyHighFeeRate.SatoshiPerByte;

		if (minFeeRate < lowest || minFeeRate > highest)
		{
			return FormattableString.Invariant($"The fee rate is between {lowest:0.###} and {highest:0} sat/vB.");
		}

		if (buyRate < 0 || buyRate > MaxRate)
		{
			return "The buy rate is zero or more sats.";
		}

		if (sellRate < 0 || sellRate > MaxRate)
		{
			return "The sell rate is zero or more sats.";
		}

		// A maker that pays more for a point than it sells one for can be emptied by a taker that gives it points and takes them back.
		if (sellRate < buyRate)
		{
			return "The sell rate cannot be lower than the buy rate.";
		}

		return null;
	}

	/// <summary>Tells why this is not an offer that can be made, or null if it is.</summary>
	public string? Validate()
	{
		if (ValidateTerms(MinFeeRate, BuyRate, SellRate) is { } reason)
		{
			return reason;
		}

		if (Liquidity < MarketRules.Dust || Liquidity > Constants.MaximumNumberOfSatoshis)
		{
			return "An offer has coins in it.";
		}

		if (Points < 0 || Points > MaxPoints)
		{
			return "The points of an offer are zero or more.";
		}

		if (MatchedInputs is < 0 or > MarketRules.MaxMatchedInputsPerMaker || ExtraInputs < 0 || CoinCount is < 1 or > MaxCoinCount)
		{
			return FormattableString.Invariant($"An offer has one to {MaxCoinCount} coins, and {MarketRules.MaxMatchedInputsPerMaker} of them at most count as matched.");
		}

		if (Bond is { IsWellFormed: false })
		{
			return "The proof of the fidelity bond is not well formed.";
		}

		return null;
	}
}
