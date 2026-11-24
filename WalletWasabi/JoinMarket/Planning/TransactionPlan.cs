namespace WalletWasabi.JoinMarket.Planning;

/// <summary>A maker that a taker picked for a coinjoin, as far as the plan of the transaction needs to know it.</summary>
/// <param name="Alias">The name of the maker. The plan only hands it back.</param>
/// <param name="MinFeeRate">The lowest fee rate that the maker takes part at, in sats per virtual byte.</param>
/// <param name="BuyRate">What the maker pays for an anonymity point that it gains, in sats.</param>
/// <param name="SellRate">What the maker wants for an anonymity point that it loses, in sats.</param>
/// <param name="Points">The anonymity points of the coins that the maker puts in, as its offer tells them.</param>
/// <param name="MatchedInputs">How many inputs of the maker count as matched: the taker pays for them. Three at most.</param>
/// <param name="ExtraInputs">How many more inputs the maker brings. It pays for them itself.</param>
/// <param name="Liquidity">The amount that the maker offers, in sats.</param>
/// <param name="Payments">The outputs with which the maker pays somebody out of that amount, in sats.</param>
public sealed record PlannedMaker(
	string Alias,
	decimal MinFeeRate,
	decimal BuyRate,
	decimal SellRate,
	decimal Points,
	int MatchedInputs,
	int ExtraInputs,
	long Liquidity,
	IReadOnlyList<long> Payments)
{
	/// <summary>
	/// A maker by the number of its inputs alone, as the design has it: the first three of them count as matched and
	/// the others are extra.
	/// </summary>
	public static PlannedMaker FromInputCount(string alias, decimal minFeeRate, decimal buyRate, decimal sellRate, decimal points, int inputCount, long liquidity, IReadOnlyList<long> payments) =>
		new(
			alias,
			minFeeRate,
			buyRate,
			sellRate,
			points,
			Math.Min(inputCount, MarketRules.MaxMatchedInputsPerMaker),
			Math.Max(0, inputCount - MarketRules.MaxMatchedInputsPerMaker),
			liquidity,
			payments);
}

/// <summary>The taker of a coinjoin, as far as the plan of the transaction needs to know it.</summary>
/// <param name="InputCount">How many inputs the taker brings.</param>
/// <param name="Amount">What the inputs of the taker hold together, in sats.</param>
public sealed record PlannedTaker(int InputCount, long Amount);

/// <summary>The outputs that a plan gives to a maker.</summary>
/// <param name="CountedInputs">How many inputs of the maker count as matched.</param>
/// <param name="Amount">The liquidity of the maker less its payment outputs: what its other outputs add up to, with what it forfeits.</param>
/// <param name="Picks">The values of its outputs, in the order in which they were chosen.</param>
/// <param name="Change">The value of its remainder: an output of a value of its own, which other participants match. Zero if it has none.</param>
/// <param name="Forfeit">The sats that the maker leaves to the miners.</param>
/// <param name="Pinned">The outputs among the picks that match the remainder of a participant before it.</param>
/// <param name="Payments">Its payment outputs.</param>
public sealed record PlannedMakerOutputs(string Alias, int CountedInputs, long Amount, IReadOnlyList<long> Picks, long Change, long Forfeit, IReadOnlyList<long> Pinned, IReadOnlyList<long> Payments)
{
	/// <summary>The values of all the outputs that the plan gives to the maker, payment outputs not included.</summary>
	public IEnumerable<long> Outputs => Change != 0 ? Picks.Append(Change) : Picks;
}

/// <summary>The outputs that a plan gives to the taker.</summary>
/// <param name="Picks">The values of its outputs, in the order in which they were chosen.</param>
/// <param name="Change">The value of the output that carries what is left. Zero if there is none.</param>
/// <param name="Forfeit">The sats that the taker leaves to the miners.</param>
public sealed record PlannedTakerOutputs(int InputCount, long Amount, IReadOnlyList<long> Picks, long Change, long Forfeit)
{
	/// <summary>What the outputs of the taker and what it forfeits add up to: its amount and what it expects to gain from the settlement.</summary>
	public long Total => Picks.Sum() + Change + Forfeit;

	public IEnumerable<long> Outputs => Change != 0 ? Picks.Append(Change) : Picks;
}

/// <summary>
/// The plan of a taker's coinjoin: which output values every participant gets, and everything that follows from them.
/// </summary>
/// <param name="FeeRate">The fee rate of the transaction: the highest of the lowest fee rates of the makers.</param>
/// <param name="MatchedInputs">The inputs of the makers that count as matched.</param>
/// <param name="OutputPoints">What the outputs of the transaction are worth together.</param>
/// <param name="Points">The anonymity points of the transaction: the lower of the two.</param>
/// <param name="Makers">The outputs of the makers, in the order in which the makers were given.</param>
/// <param name="Tally">Every output value with the number of outputs that have it, the largest value first.</param>
/// <param name="TakerCost">What the taker pays in all, in sats. A negative amount is what the taker is paid.</param>
/// <param name="Objective">The cost that the planner weighed the plan with. It only serves to compare plans.</param>
/// <param name="ChainOrder">The places of the makers in the order in which the plan that won went through them.</param>
public sealed record TransactionPlan(
	decimal FeeRate,
	int MatchedInputs,
	int OutputPoints,
	int Points,
	IReadOnlyList<PlannedMakerOutputs> Makers,
	PlannedTakerOutputs Taker,
	IReadOnlyList<(long Value, int Count)> Tally,
	PlanCounts Counts,
	FeeTable Fees,
	Settlement Settlement,
	decimal TakerCost,
	double Objective,
	IReadOnlyList<int> ChainOrder)
{
	/// <summary>The makers and the taker.</summary>
	public int Participants => Makers.Count + 1;
}
