namespace WalletWasabi.JoinMarket.Planning;

/// <summary>Who pays for a line of the fee table of a taker's coinjoin.</summary>
public enum FeePayer
{
	Taker,
	Maker
}

/// <summary>A line of the fee table: a rule, how often it applies, and the size that it bills each time.</summary>
/// <param name="Each">The size that is billed every time, in virtual bytes.</param>
public sealed record FeeRow(string Rule, int Count, int Each, FeePayer PaidBy)
{
	public int VirtualBytes => Count * Each;

	/// <summary>What the line costs at a fee rate, in sats. Fee rates have decimals, so this can be a fraction of a sat.</summary>
	public decimal GetSats(decimal feeRate) => VirtualBytes * feeRate;
}

/// <summary>What is billed for a taker's coinjoin, and to whom.</summary>
/// <param name="FeeRate">The fee rate of the transaction, in sats per virtual byte.</param>
/// <param name="UnmatchedMakerOutputs">How many outputs of makers are alone with their value. The taker pays the maker of each for it.</param>
/// <param name="Forfeit">The sats that the participants leave to the miners because outputs for them would cost more.</param>
public sealed record FeeTable(IReadOnlyList<FeeRow> Rows, decimal FeeRate, int UnmatchedMakerOutputs, long Forfeit)
{
	public int TotalVirtualBytes => Rows.Sum(row => row.VirtualBytes);

	public int MakerVirtualBytes => Rows.Where(row => row.PaidBy == FeePayer.Maker).Sum(row => row.VirtualBytes);

	public int TakerVirtualBytes => TotalVirtualBytes - MakerVirtualBytes;

	/// <summary>What the taker pays in fees: everything that is billed to it, and what is left to the miners.</summary>
	public decimal TakerFee => TakerVirtualBytes * FeeRate + Forfeit;

	/// <summary>What the makers pay in fees: their inputs beyond the ones that the taker pays for, and their payment outputs.</summary>
	public decimal MakerFee => MakerVirtualBytes * FeeRate;

	/// <summary>Everything that is billed, at the fee rate, and what is left to the miners.</summary>
	public decimal MiningFee => TotalVirtualBytes * FeeRate + Forfeit;

	/// <summary>The part of what the taker pays that reaches the miners.</summary>
	public decimal TakerToMiners => (TotalVirtualBytes - MakerVirtualBytes - MarketRules.InputVb * UnmatchedMakerOutputs) * FeeRate + Forfeit;

	/// <summary>The part of what the taker pays that goes to the makers who got an output that is alone with its value.</summary>
	public decimal TakerToMakers => MarketRules.InputVb * UnmatchedMakerOutputs * FeeRate;
}

/// <summary>What one maker and the taker owe each other for the anonymity points that the transaction swaps.</summary>
/// <param name="OriginPoints">The points of the coins that the maker puts in, as its offer tells them.</param>
/// <param name="Delta">The points of the transaction less the points of the maker: what the maker gains.</param>
/// <param name="Rate">The rate that applies: the buy rate of the maker if it gains points, its sell rate if it loses points, and none if neither.</param>
/// <param name="Sats">What the maker pays the taker. A negative amount is what the taker pays the maker.</param>
public sealed record SettlementRow(string Alias, decimal OriginPoints, decimal Delta, decimal? Rate, decimal Sats);

/// <summary>What the makers and the taker of a transaction owe each other for anonymity points.</summary>
/// <param name="MakersPay">What the makers that gain points pay the taker together.</param>
/// <param name="TakerPays">What the taker pays the makers that lose points together.</param>
public sealed record Settlement(IReadOnlyList<SettlementRow> Rows, decimal MakersPay, decimal TakerPays)
{
	/// <summary>What the taker gets out of the settlement. A negative amount is what it pays.</summary>
	public decimal Net => MakersPay - TakerPays;
}

/// <summary>How many outputs and inputs of each kind a planned transaction has.</summary>
/// <param name="MatchedOutputs">The outputs of makers and of the taker whose value another output has. Payment outputs are not among them.</param>
/// <param name="UnmatchedMakerOutputs">The outputs of makers, other than payment outputs, that are alone with their value.</param>
/// <param name="UnmatchedTakerOutputs">The outputs of the taker that are alone with their value.</param>
/// <param name="Payments">The payment outputs of the makers.</param>
/// <param name="MatchedPayments">The payment outputs whose value another output has.</param>
/// <param name="ExtraMakerInputs">The inputs of makers beyond the ones that count as matched.</param>
public sealed record PlanCounts(int MatchedOutputs, int UnmatchedMakerOutputs, int UnmatchedTakerOutputs, int Payments, int MatchedPayments, int ExtraMakerInputs)
{
	/// <summary>All the outputs that are alone with their value, payment outputs included.</summary>
	public int UnmatchedOutputs => UnmatchedMakerOutputs + UnmatchedTakerOutputs + (Payments - MatchedPayments);
}

/// <summary>
/// The rules of a taker's coinjoin with makers: what is billed, at which sizes and to whom, and how anonymity points
/// are settled in sats.
/// </summary>
/// <remarks>
/// Sizes are fixed, the ones of Taproot coins: an input that is spent with its key, an output, and what a transaction
/// has besides its inputs and outputs. Amounts are whole sats. Fee rates and the rates of a point have decimals, so
/// fees and settlements can be fractions of a sat here: how they are rounded is a matter of the transaction that is
/// signed in the end, not of the plan.
/// </remarks>
public static class MarketRules
{
	/// <summary>The size that an input is billed with, in virtual bytes.</summary>
	public const int InputVb = 57;

	/// <summary>The size that an output is billed with if another output of the transaction has its value.</summary>
	public const int OutputVb = 43;

	/// <summary>The size that an output is billed with if it is alone with its value. The size of an input out of it goes to the maker who got the output.</summary>
	public const int UnmatchedOutputVb = 100;

	/// <summary>The size that the transaction is billed with besides its inputs and outputs.</summary>
	public const int OverheadVb = 11;

	/// <summary>No output is smaller than this many sats.</summary>
	public const long Dust = 330;

	/// <summary>How many of the inputs of a maker count as matched, and are paid for by the taker.</summary>
	public const int MaxMatchedInputsPerMaker = 3;

	/// <summary>How many outputs a maker has at most, its payment outputs not counted.</summary>
	public const int MaxMakerOutputs = 4;

	/// <summary>How many outputs the taker has at most.</summary>
	public const int MaxTakerOutputs = 10;

	public const string OverheadRule = "Transaction overhead";
	public const string TakerInputsRule = "Taker inputs";
	public const string TakerChangeRule = "Taker change";
	public const string MatchedInputsRule = "Matched inputs";
	public const string MatchedOutputsRule = "Matched outputs";
	public const string UnmatchedOutputsRule = "Unmatched outputs";
	public const string ExtraMakerInputsRule = "Extra Maker inputs";
	public const string ExtraMakerOutputsRule = "Extra Maker outputs";

	/// <summary>
	/// The fee rate of a transaction: the highest of the lowest fee rates that the makers take part at. Without makers there is none.
	/// </summary>
	public static decimal GetFeeRate(IEnumerable<decimal> minFeeRatesOfMakers) =>
		minFeeRatesOfMakers.DefaultIfEmpty(0m).Max();

	/// <summary>The points of a transaction: what its outputs are worth, but not more than its matched inputs.</summary>
	public static int GetPoints(int matchedInputs, int outputPoints) => Math.Min(matchedInputs, outputPoints);

	/// <summary>The fee table of a transaction.</summary>
	/// <param name="takerInputs">How many inputs the taker has.</param>
	/// <param name="matchedInputs">How many inputs of the makers count as matched: three per maker at most.</param>
	/// <param name="forfeit">The sats that the participants leave to the miners.</param>
	public static FeeTable GetFees(decimal feeRate, int takerInputs, int matchedInputs, PlanCounts counts, long forfeit)
	{
		var rows = new List<FeeRow>
		{
			new(OverheadRule, 1, OverheadVb, FeePayer.Taker),
			new(TakerInputsRule, takerInputs, InputVb, FeePayer.Taker)
		};

		// The change of the taker is billed like any output that is alone with its value, and there is no maker to credit for it.
		if (counts.UnmatchedTakerOutputs > 0)
		{
			rows.Add(new FeeRow(TakerChangeRule, counts.UnmatchedTakerOutputs, UnmatchedOutputVb, FeePayer.Taker));
		}

		rows.Add(new FeeRow(MatchedInputsRule, matchedInputs, InputVb, FeePayer.Taker));
		rows.Add(new FeeRow(MatchedOutputsRule, counts.MatchedOutputs, OutputVb, FeePayer.Taker));
		rows.Add(new FeeRow(UnmatchedOutputsRule, counts.UnmatchedMakerOutputs, UnmatchedOutputVb, FeePayer.Taker));
		rows.Add(new FeeRow(ExtraMakerInputsRule, counts.ExtraMakerInputs, InputVb, FeePayer.Maker));
		rows.Add(new FeeRow(ExtraMakerOutputsRule, counts.Payments, OutputVb, FeePayer.Maker));

		return new FeeTable(rows, feeRate, counts.UnmatchedMakerOutputs, forfeit);
	}

	/// <summary>
	/// What a maker pays the taker for the points that a transaction gives it: for every point that the transaction is
	/// worth more than the coins of the maker were, the maker pays its buy rate, and for every point that it is worth
	/// less, the taker pays the sell rate of the maker. A negative amount is what the taker pays.
	/// </summary>
	/// <param name="points">The points of the transaction.</param>
	/// <param name="originPoints">The points of the coins that the maker puts in.</param>
	public static decimal GetSettlement(decimal points, decimal originPoints, decimal buyRate, decimal sellRate)
	{
		var delta = points - originPoints;

		return delta > 0 ? delta * buyRate : delta * sellRate;
	}

	/// <summary>The settlement of a transaction of the given points with its makers.</summary>
	public static Settlement Settle(decimal points, IEnumerable<PlannedMaker> makers)
	{
		var rows = new List<SettlementRow>();
		decimal makersPay = 0, takerPays = 0;

		foreach (var maker in makers)
		{
			var delta = points - maker.Points;
			var sats = GetSettlement(points, maker.Points, maker.BuyRate, maker.SellRate);

			if (sats > 0)
			{
				makersPay += sats;
			}
			else
			{
				takerPays -= sats;
			}

			rows.Add(new SettlementRow(maker.Alias, maker.Points, delta, delta > 0 ? maker.BuyRate : delta < 0 ? maker.SellRate : null, sats));
		}

		return new Settlement(rows, makersPay, takerPays);
	}

	/// <summary>
	/// What the taker pays in all: what is billed to it, what is left to the miners, less what it gets out of the
	/// settlement. A negative amount is what the taker is paid for the transaction.
	/// </summary>
	public static decimal GetTakerCost(FeeTable fees, Settlement settlement) =>
		fees.TakerToMiners + fees.TakerToMakers - settlement.Net;
}
