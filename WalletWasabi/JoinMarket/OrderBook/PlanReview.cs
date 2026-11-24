using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>How widely an output value is shared, which is what a taker reads off its colour.</summary>
public enum ValueKind
{
	/// <summary>Three or more outputs have the value: a point each.</summary>
	SetOfThree,

	/// <summary>Two outputs have the value: half a point each.</summary>
	Pair,

	/// <summary>One output is alone with the value: no point, and the higher fee.</summary>
	Alone
}

/// <summary>A value of the planned transaction with the outputs that have it.</summary>
/// <param name="ValueText">The value in BTC.</param>
/// <param name="CountText">How many outputs have it: "× 3".</param>
/// <param name="Owners">Whose outputs they are: "2 Makers · 1 Taker".</param>
public sealed record PlanOutputRow(long Value, int Count, string ValueText, string CountText, string Owners, ValueKind Kind)
{
	public bool IsPair => Kind == ValueKind.Pair;

	public bool IsAlone => Kind == ValueKind.Alone;
}

/// <summary>A line of the price calculator.</summary>
/// <param name="Detail">The size and how often it applies: "57 vB × 9". Empty where there is none to tell.</param>
/// <param name="VirtualBytes">The size of the line: "513 vB".</param>
/// <param name="Sats">What the line costs, in whole sats.</param>
/// <param name="IsPaidByMakers">Whether the makers pay the line. The taker pays the others.</param>
public sealed record PlanFeeRow(string Rule, string Detail, string VirtualBytes, string Sats, bool IsPaidByMakers);

/// <summary>What one maker and the taker settle for anonymity points.</summary>
/// <param name="OriginPoints">The points of the coins of the maker.</param>
/// <param name="Delta">What the maker gains in points, with its sign.</param>
/// <param name="Rate">The rate that applies, or a dash if none does.</param>
/// <param name="RateKind">Positive if the rate is the buy rate of the maker, negative if it is its sell rate, zero if there is none.</param>
/// <param name="Sats">What the maker pays the taker, with its sign.</param>
public sealed record PlanSettlementRow(string Alias, string OriginPoints, string Delta, string Rate, int RateKind, string Sats)
{
	/// <summary>Whether the maker gains points and pays its buy rate for them.</summary>
	public bool IsBuying => RateKind > 0;

	/// <summary>Whether the maker loses points and is paid its sell rate for them.</summary>
	public bool IsSelling => RateKind < 0;
}

/// <summary>
/// The plan of a transaction as the taker's side of the market shows it: the figures and the output values of the
/// transaction builder, and the lines of the price calculator.
/// </summary>
/// <param name="Points">The anonymity points of the transaction.</param>
/// <param name="Liquidity">What the picked makers offer together, in BTC with two decimals.</param>
/// <param name="MatchedIn">The inputs of the makers that count as matched.</param>
/// <param name="MatchedOut">What the outputs are worth together: a pair counts one, a set of three counts three.</param>
/// <param name="UnmatchedOut">The outputs that are alone with their value, payment outputs included.</param>
/// <param name="Outputs">The output values, the largest first.</param>
/// <param name="MiningFeeRate">The fee rate of the transaction with one decimal, or a dash without makers.</param>
/// <param name="TotalLabel">"Total you pay", or "Total you receive" if the taker is paid.</param>
/// <param name="Total">What the taker pays or receives in all, in whole sats and without a sign.</param>
public sealed record PlanReview(
	bool HasMakers,
	string Points,
	string Participants,
	string Liquidity,
	string MatchedIn,
	string MatchedOut,
	string UnmatchedOut,
	IReadOnlyList<PlanOutputRow> Outputs,
	IReadOnlyList<PlanFeeRow> FeeRows,
	string TakerVirtualBytes,
	string TakerFee,
	string MakerVirtualBytes,
	string MakerFee,
	string MiningFeeRate,
	string TotalVirtualBytes,
	string MiningFee,
	IReadOnlyList<PlanSettlementRow> Settlement,
	string TakerPays,
	string MakersPay,
	string NetSettlement,
	string TotalLabel,
	string Total)
{
	public const string ForfeitRule = "Forfeited to miners";
	public const string PayLabel = "Total you pay";
	public const string ReceiveLabel = "Total you receive";

	/// <summary>How widely a value is shared, by the number of outputs that have it.</summary>
	public static ValueKind GetKind(int count) =>
		count >= 3 ? ValueKind.SetOfThree : count == 2 ? ValueKind.Pair : ValueKind.Alone;

	/// <summary>What a plan shows.</summary>
	/// <param name="liquidity">What the picked makers offer together, in sats.</param>
	public static PlanReview Create(TransactionPlan plan, long liquidity)
	{
		var hasMakers = plan.Makers.Count > 0;
		var fees = plan.Fees;

		var feeRows = fees.Rows
			.Select(row => new PlanFeeRow(
				row.Rule,
				row.Rule == MarketRules.OverheadRule ? "" : $"{row.Each} vB × {row.Count}",
				$"{MarketFormat.Whole(row.VirtualBytes)} vB",
				MarketFormat.Whole(row.GetSats(fees.FeeRate)),
				row.PaidBy == FeePayer.Maker))
			.ToList();

		if (fees.Forfeit > 0)
		{
			feeRows.Add(new PlanFeeRow(ForfeitRule, "", "", MarketFormat.Whole(fees.Forfeit), IsPaidByMakers: false));
		}

		// The makers that cost the taker the most come first, and the ones that pay it the most last.
		var settlement = plan.Settlement.Rows
			.OrderBy(row => row.Sats)
			.Select(row => new PlanSettlementRow(
				row.Alias,
				MarketFormat.Points(row.OriginPoints),
				MarketFormat.SignedPoints(row.Delta),
				row.Rate is { } rate ? MarketFormat.Rate(rate) : MarketFormat.None,
				Math.Sign(row.Delta),
				MarketFormat.Signed(row.Sats)))
			.ToArray();

		return new PlanReview(
			hasMakers,
			MarketFormat.Whole(plan.Points),
			MarketFormat.Whole(plan.Participants),
			MarketFormat.Btc(liquidity, 2),
			MarketFormat.Whole(plan.MatchedInputs),
			MarketFormat.Whole(plan.OutputPoints),
			MarketFormat.Whole(plan.Counts.UnmatchedOutputs),
			GetOutputs(plan),
			feeRows,
			$"{MarketFormat.Whole(fees.TakerVirtualBytes)} vB",
			MarketFormat.Whole(fees.TakerFee),
			$"{MarketFormat.Whole(fees.MakerVirtualBytes)} vB",
			MarketFormat.Whole(fees.MakerFee),
			hasMakers ? MarketFormat.FeeRate(plan.FeeRate) : MarketFormat.None,
			$"{MarketFormat.Whole(fees.TotalVirtualBytes)} vB",
			MarketFormat.Whole(fees.MiningFee),
			settlement,
			MarketFormat.Whole(plan.Settlement.TakerPays),
			MarketFormat.Whole(plan.Settlement.MakersPay),
			MarketFormat.Signed(plan.Settlement.Net),
			plan.TakerCost >= 0 ? PayLabel : ReceiveLabel,
			MarketFormat.Whole(Math.Abs(plan.TakerCost)));
	}

	/// <summary>The values of the outputs of a plan with the outputs that have each, the largest value first.</summary>
	private static PlanOutputRow[] GetOutputs(TransactionPlan plan)
	{
		var byValue = new Dictionary<long, (int Makers, int Payments, int Taker, int TakerChange)>();

		void Count(long value, Func<(int Makers, int Payments, int Taker, int TakerChange), (int, int, int, int)> add) =>
			byValue[value] = add(byValue.GetValueOrDefault(value));

		foreach (var maker in plan.Makers)
		{
			foreach (var value in maker.Outputs)
			{
				Count(value, owners => (owners.Makers + 1, owners.Payments, owners.Taker, owners.TakerChange));
			}

			foreach (var value in maker.Payments)
			{
				Count(value, owners => (owners.Makers, owners.Payments + 1, owners.Taker, owners.TakerChange));
			}
		}

		foreach (var value in plan.Taker.Picks)
		{
			Count(value, owners => (owners.Makers, owners.Payments, owners.Taker + 1, owners.TakerChange));
		}

		if (plan.Taker.Change != 0)
		{
			Count(plan.Taker.Change, owners => (owners.Makers, owners.Payments, owners.Taker, owners.TakerChange + 1));
		}

		return [.. byValue
			.OrderByDescending(entry => entry.Key)
			.Select(entry =>
			{
				var (makers, payments, taker, takerChange) = entry.Value;
				var count = makers + payments + taker + takerChange;
				var owners = new List<string>();

				if (makers > 0)
				{
					owners.Add($"{makers} {(makers > 1 ? "Makers" : "Maker")}");
				}

				if (payments > 0)
				{
					owners.Add($"{payments} {(payments > 1 ? "Payments" : "Payment")}");
				}

				if (taker > 0)
				{
					owners.Add($"{taker} Taker");
				}

				if (takerChange > 0)
				{
					owners.Add("Taker change");
				}

				return new PlanOutputRow(entry.Key, count, MarketFormat.Btc(entry.Key), $"× {count}", string.Join(" · ", owners), GetKind(count));
			})];
	}
}
