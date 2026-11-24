using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>What a taker can rank and filter makers by.</summary>
public enum BookCriterion
{
	/// <summary>The lowest fee rate that the maker takes part at, in sats per virtual byte. Lower is better for the taker.</summary>
	FeeRate,

	/// <summary>The sell rate less the buy rate, in sats per anonymity point. Lower is better.</summary>
	Spread,

	/// <summary>What the maker pays for a point that it gains. Higher is better.</summary>
	BuyRate,

	/// <summary>What the maker wants for a point that it loses. Lower is better.</summary>
	SellRate,

	/// <summary>The coins that the maker has locked in a fidelity bond, in BTC. Higher is better.</summary>
	Bond,

	/// <summary>The amount that the maker offers, in BTC. Higher is better.</summary>
	Liquidity
}

/// <summary>The offer of a maker as the order book shows it and as a taker plans with it.</summary>
/// <param name="Id">A number that tells the offers of a book apart. The lower one goes first where nothing else decides.</param>
/// <param name="Alias">The name that the book shows for the maker.</param>
/// <param name="MinFeeRate">The lowest fee rate that the maker takes part at, in sats per virtual byte.</param>
/// <param name="BuyRate">What the maker pays for an anonymity point that it gains, in sats.</param>
/// <param name="SellRate">What the maker wants for an anonymity point that it loses, in sats.</param>
/// <param name="Spread">The sell rate less the buy rate.</param>
/// <param name="Bond">The coins that the maker has locked in a fidelity bond, in sats. Zero if it has none.</param>
/// <param name="Liquidity">The amount that the maker offers, in sats.</param>
/// <param name="MatchedInputs">How many inputs of the maker count as matched: three at most.</param>
/// <param name="ExtraInputs">How many more inputs the maker brings.</param>
/// <param name="Points">The anonymity points of the coins behind the offer.</param>
/// <param name="Payments">The outputs with which the maker pays somebody out of its liquidity, in sats.</param>
/// <param name="IsOwn">Whether the offer is one of this wallet.</param>
public sealed record BookMaker(
	long Id,
	string Alias,
	decimal MinFeeRate,
	decimal BuyRate,
	decimal SellRate,
	decimal Spread,
	long Bond,
	long Liquidity,
	int MatchedInputs,
	int ExtraInputs,
	decimal Points,
	IReadOnlyList<long> Payments,
	bool IsOwn = false)
{
	private const decimal SatsPerBtc = 100_000_000m;

	public bool IsBonded => Bond > 0;

	public int InputCount => MatchedInputs + ExtraInputs;

	/// <summary>
	/// The value of the offer for a criterion. Rates are in their own units, and the bond and the liquidity are in
	/// BTC, which is the unit in which a taker types the limits of its filters.
	/// </summary>
	public decimal GetValue(BookCriterion criterion) =>
		criterion switch
		{
			BookCriterion.FeeRate => MinFeeRate,
			BookCriterion.Spread => Spread,
			BookCriterion.BuyRate => BuyRate,
			BookCriterion.SellRate => SellRate,
			BookCriterion.Bond => Bond / SatsPerBtc,
			BookCriterion.Liquidity => Liquidity / SatsPerBtc,
			_ => throw new ArgumentOutOfRangeException(nameof(criterion))
		};

	/// <summary>The maker as the plan of a transaction takes it.</summary>
	public PlannedMaker ToPlannedMaker() =>
		new(Alias, MinFeeRate, BuyRate, SellRate, Points, MatchedInputs, ExtraInputs, Liquidity, Payments);
}
