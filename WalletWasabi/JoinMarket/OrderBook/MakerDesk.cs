using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace WalletWasabi.JoinMarket.OrderBook;

/// <summary>What a maker has set on its side of the market: the tab, the picked coins, and what is typed into the two forms.</summary>
public sealed record MakerDeskState
{
	public MakerTab Tab { get; init; } = MakerTab.Bond;

	/// <summary>The point of the strategy triangle that the offer and the split were set from. Null once one of them was changed by hand.</summary>
	public TriangleWeights? Strategy { get; init; }

	/// <summary>The share of the wallet that goes into the fidelity bond. A bond that exists fixes it.</summary>
	public double BondShare { get; init; } = 0.5;

	/// <summary>The coins that are picked.</summary>
	public ImmutableHashSet<string> Selected { get; init; } = [];

	public CoinSortKey SortKey { get; init; } = CoinSortKey.Amount;

	public bool SortAscending { get; init; }

	/// <summary>Whether the list names the coins by their addresses. Otherwise by the outputs that they are.</summary>
	public bool ShowsAddresses { get; init; }

	/// <summary>The amount of the offer as it was typed or set. Null while it follows the picked coins.</summary>
	public string? OfferAmount { get; init; }

	public string OfferFeeRate { get; init; } = "";

	public string OfferSellRate { get; init; } = "";

	public string OfferBuyRate { get; init; } = "";

	/// <summary>The amount of the bond as it was typed or set. Null while it follows the picked coins.</summary>
	public string? BondAmount { get; init; }

	public string BondBlocks { get; init; } = MakerDesk.DefaultBondBlocks;

	public string BondFeeRate { get; init; } = MakerDesk.DefaultBondFeeRate;

	public string? OfferError { get; init; }

	public string? BondError { get; init; }
}

/// <summary>What marks a row of the maker's coin list.</summary>
public enum CoinMark
{
	None,

	/// <summary>A picked coin without anonymity points. It counts in an offer like any other, and has no points to sell.</summary>
	NoPoints,

	/// <summary>A picked coin that comes from the same transaction as a larger picked coin. It is not counted.</summary>
	SharedOrigin,

	/// <summary>A picked coin beyond the first three. It only adds the cost of its input.</summary>
	Extra
}

/// <summary>A row of the maker's coin list.</summary>
/// <param name="IsBarred">Whether the coin is greyed out and cannot be picked: on Lock Coins, a coin that is not private.</param>
/// <param name="IsTaken">Whether a posted offer holds the coin. It cannot be picked again.</param>
public sealed record MakerCoinRow(MarketCoin Coin, bool IsSelected, CoinMark Mark, bool IsBarred, bool IsTaken)
{
	/// <summary>Whether a click on the row picks the coin or lets it go.</summary>
	public bool CanToggle => !Coin.IsBond && !IsTaken && !IsBarred;
}

/// <summary>One of the bars under the maker's triangle: a share of the wallet, or the place of a rate in the book.</summary>
/// <param name="Key">"liq", "bond", "minFee", "sell" or "buy".</param>
/// <param name="Percent">Where the slider stands, from 0 to 100.</param>
/// <param name="Caption">What the bar says on its left: "50% of 5.8254 BTC", "beats 51% of the book".</param>
/// <param name="ValueText">What the bar says on its right: the amount, or the rate with its unit.</param>
/// <param name="InputText">What stands in the box that takes a typed value.</param>
public sealed record MakerBarRow(string Key, string Label, int Percent, string Caption, string ValueText, string InputText);

/// <summary>What the maker's side of the market shows for a state.</summary>
/// <param name="Coins">The coins in the order of the list: the bonds on top, then by the column that the list is sorted by.</param>
/// <param name="AllocationRows">The split of the wallet: Liquidity and Bond.</param>
/// <param name="RateRows">The rates of the offer: Fee rate, Sell rate, Buy rate.</param>
/// <param name="IsAllocationEnabled">Whether the split can be moved: on Lock Coins, while no bond exists.</param>
/// <param name="AreRatesEnabled">Whether the rates can be moved: on List Offers.</param>
/// <param name="OfferAmount">What stands in the amount box of the offer.</param>
/// <param name="BondAmount">What stands in the amount box of the bond.</param>
/// <param name="OfferPoints">The points of the offer as its button tells them: "Offer 4.3 AP".</param>
/// <param name="OfferSellRate">What stands in the sell rate box of the offer: what was set, or 0 for an offer without points.</param>
/// <param name="IsSellRateFixed">
/// Whether the sell rate is not the maker's to set: the picked coins have no anonymity points, so the offer has none
/// to sell and lists a sell rate of 0.
/// </param>
public sealed record MakerDeskView(
	IReadOnlyList<MakerCoinRow> Coins,
	IReadOnlyList<MakerBarRow> AllocationRows,
	IReadOnlyList<MakerBarRow> RateRows,
	bool IsAllocationEnabled,
	bool AreRatesEnabled,
	string OfferAmount,
	string BondAmount,
	string OfferPoints,
	string OfferSellRate,
	bool IsSellRateFixed,
	SelectAllState SelectAll);

/// <summary>An offer that is ready to be posted.</summary>
/// <param name="CoinIds">Every picked coin: the ones that count and the ones that only add inputs.</param>
/// <param name="Amount">The amount that is offered, in sats.</param>
/// <param name="Points">The anonymity points that the offer tells.</param>
/// <param name="MatchedInputs">How many of the coins count as matched inputs: the private ones, three at most.</param>
public sealed record OfferDraft(ImmutableArray<string> CoinIds, long Amount, decimal FeeRate, decimal SellRate, decimal BuyRate, decimal Points, int MatchedInputs);

/// <summary>A fidelity bond that is ready to be locked.</summary>
/// <param name="CoinIds">The picked coins. The bond is paid out of them.</param>
/// <param name="Amount">The amount that is locked, in sats.</param>
/// <param name="Blocks">For how many blocks it is locked.</param>
/// <param name="FeeRate">The fee rate of the transaction that locks it, in sats per virtual byte.</param>
/// <param name="Estimate">The size and the fee of that transaction as they are estimated before it is built.</param>
public sealed record BondDraft(ImmutableArray<string> CoinIds, long SelectedTotal, long Amount, int Blocks, decimal FeeRate, BondTransactionEstimate Estimate)
{
	/// <summary>"2,016 blocks".</summary>
	public string BlocksText => $"{MarketFormat.Whole(Blocks)} {(Blocks == 1 ? "block" : "blocks")}";

	/// <summary>The title of the question that is asked before the bond is locked.</summary>
	public string Title => $"Confirm Bond for {MarketFormat.Btc(Amount)} BTC";

	/// <summary>"Locked for 2,016 blocks (2 weeks)".</summary>
	public string LockText => $"Locked for {BlocksText} ({BondFigures.DescribeDuration(Blocks)})";

	/// <summary>"Fee rate 1 sat/vByte (154 sats)".</summary>
	public string FeeText => DescribeFee(FeeRate, Estimate.Fee);

	public static string DescribeFee(decimal feeRate, decimal fee) =>
		$"Fee rate {MarketFormat.Plain(feeRate)} sat/vByte ({MarketFormat.Whole(fee)} sats)";
}

/// <summary>
/// The maker's side of the market over one order book and one set of coins: what the strategy triangle, a bar, a tab
/// or a tick of a coin makes of the state, what a state shows, and whether its forms can be sent.
/// </summary>
public sealed class MakerDesk
{
	public const string DefaultBondBlocks = "2016";
	public const string DefaultBondFeeRate = "1";

	public const string SelectCoinsError = "Select the coins to offer.";
	public const string OfferAmountError = "Enter an amount to offer.";
	public const string OfferFeeRateError = "Enter a minimum fee rate.";
	public const string OfferSellRateError = "Enter a sell rate.";
	public const string OfferBuyRateError = "Enter a buy rate.";
	public const string BondAmountError = "Enter an amount to lock.";
	public const string BondBlocksError = "Enter a lock time in blocks.";
	public const string BondFeeRateError = "Enter a fee rate for the bonding transaction.";

	/// <summary>The sell rate that an offer without anonymity points lists: it has none to sell.</summary>
	public const string NoPointsSellRate = "0";

	/// <param name="book">The offers of the other makers: the rates of an offer are placed among theirs.</param>
	/// <param name="coins">The coins of the wallet, the ones that are locked in a bond included.</param>
	/// <param name="taken">The coins that posted offers hold.</param>
	/// <param name="offered">The amount that posted offers are for together, in sats.</param>
	public MakerDesk(IReadOnlyList<BookMaker> book, IReadOnlyList<MarketCoin> coins, IReadOnlySet<string> taken, long offered)
	{
		Book = book;
		Coins = coins;
		Taken = taken;
		Offered = offered;
	}

	public IReadOnlyList<BookMaker> Book { get; }

	public IReadOnlyList<MarketCoin> Coins { get; }

	public IReadOnlySet<string> Taken { get; }

	public long Offered { get; }

	/// <summary>Whether coins of the wallet are locked in a bond. A bond fixes the split of the wallet, and no second one is locked next to it.</summary>
	public bool HasBond => Coins.Any(coin => coin.IsBond);

	/// <summary>What the coins of the wallet hold together, in sats.</summary>
	public long Total => Coins.Sum(coin => coin.Amount);

	/// <summary>
	/// The state that the screen opens with: on Lock Coins until a bond exists, on List Offers afterwards, at the middle
	/// of the strategy triangle.
	/// </summary>
	public MakerDeskState Open() =>
		ApplyStrategy(
			new MakerDeskState
			{
				Tab = HasBond ? MakerTab.Offers : MakerTab.Bond,
				Selected = [.. MakerCoins.GetDefaultOfferCoins(Coins, Taken, CoinSortKey.Amount, ascending: false)]
			},
			TriangleWeights.Centre);

	/// <summary>
	/// Sets the offer and the split of the wallet from a point of the strategy triangle, and picks the coins for the
	/// tab that is open. Without a book there is nothing to place the rates by: they stay as they are.
	/// </summary>
	public MakerDeskState ApplyStrategy(MakerDeskState state, TriangleWeights weights)
	{
		var terms = MakerStrategy.GetTerms(Book, weights);

		if (Book.Count > 0)
		{
			state = state with
			{
				OfferFeeRate = MarketFormat.FeeRate(terms.FeeRate),
				OfferSellRate = MarketFormat.Rate(terms.SellRate),
				OfferBuyRate = MarketFormat.Rate(terms.BuyRate)
			};
		}

		return ApplyAllocation(state with { Strategy = weights }, terms.BondShare);
	}

	/// <summary>
	/// Splits the wallet between the bond and the liquidity by the given share, picks the coins for the tab that is
	/// open, and fills the amount of that tab.
	/// </summary>
	public MakerDeskState ApplyAllocation(MakerDeskState state, double bondShare)
	{
		var allocation = MakerCoins.Allocate(Coins, Taken, Offered, bondShare, state.Tab, state.SortKey, state.SortAscending);
		var amount = allocation.Amount is { } sats ? MarketFormat.Btc(sats) : null;

		return state with
		{
			BondShare = allocation.BondShare,
			Selected = [.. allocation.CoinIds],
			OfferAmount = state.Tab == MakerTab.Offers ? amount : null,
			BondAmount = state.Tab == MakerTab.Bond ? amount : null,
			OfferError = null,
			BondError = null
		};
	}

	/// <summary>Moves the split of the wallet by hand: the bond gets the given share. An existing bond does not let the split move.</summary>
	public MakerDeskState SetBondShare(MakerDeskState state, double bondShare) =>
		HasBond
			? state
			: ApplyAllocation(state with { Strategy = null }, Math.Clamp(bondShare, 0, 1));

	/// <summary>Sets the split by a typed amount, in BTC, of the bond or of the liquidity.</summary>
	public MakerDeskState SetAllocationAmount(MakerDeskState state, bool isBond, string text)
	{
		if (HasBond || Total <= 0 || MarketFormat.ParseNumber(text) is not { } btc)
		{
			return state;
		}

		var share = Math.Clamp((double)(btc * 100_000_000m) / Total, 0, 1);

		return SetBondShare(state, isBond ? share : 1 - share);
	}

	/// <summary>Sets one rate of the offer by hand. The marker of the strategy triangle lets go.</summary>
	/// <param name="rate">The fee rate, the sell rate or the buy rate.</param>
	public MakerDeskState SetRate(MakerDeskState state, BookCriterion rate, decimal value)
	{
		value = Math.Max(0, value);
		state = state with { Strategy = null, OfferError = null };

		return rate switch
		{
			BookCriterion.FeeRate => state with { OfferFeeRate = MarketFormat.FeeRate(value) },
			BookCriterion.SellRate => state with { OfferSellRate = MarketFormat.Rate(value) },
			BookCriterion.BuyRate => state with { OfferBuyRate = MarketFormat.Rate(value) },
			_ => state
		};
	}

	/// <summary>Sets one rate of the offer to where it beats the given share of the book.</summary>
	/// <param name="percent">The share of the makers that the rate is to beat, in percent.</param>
	public MakerDeskState SetRatePosition(MakerDeskState state, BookCriterion rate, double percent) =>
		SetRate(state, rate, MakerStrategy.GetRateAt(Book, rate, Math.Clamp(percent, 0, 100) / 100));

	/// <summary>Sets one rate of the offer to a typed value. What is no number changes nothing.</summary>
	public MakerDeskState SetRateText(MakerDeskState state, BookCriterion rate, string text) =>
		MarketFormat.ParseNumber(text) is { } value ? SetRate(state, rate, value) : state;

	/// <summary>
	/// Opens a tab. Each tab brings its own pick of coins: Lock Coins the first private coin, List Offers its three.
	/// With a strategy, the coins and the amount are those of the strategy for the tab.
	/// </summary>
	public MakerDeskState SetTab(MakerDeskState state, MakerTab tab)
	{
		if (tab == state.Tab)
		{
			return state;
		}

		ImmutableHashSet<string> selected = tab == MakerTab.Bond
			? [.. MakerCoins.Sort(Coins.Where(coin => !coin.IsBond && !Taken.Contains(coin.Id) && coin.IsPrivate), state.SortKey, state.SortAscending).Take(1).Select(coin => coin.Id)]
			: [.. MakerCoins.GetDefaultOfferCoins(Coins, Taken, state.SortKey, state.SortAscending)];

		state = state with { Tab = tab, Selected = selected, BondAmount = null, OfferAmount = null, BondError = null, OfferError = null };

		return state.Strategy is { } weights ? ApplyStrategy(state, weights) : state;
	}

	/// <summary>
	/// Sorts the list by a column: the largest or the most points first, and the other way round the next time. The
	/// coins are picked anew in that order.
	/// </summary>
	public MakerDeskState SortBy(MakerDeskState state, CoinSortKey key)
	{
		state = state with { SortKey = key, SortAscending = state.SortKey == key && !state.SortAscending };

		return state.Strategy is { } weights ? ApplyStrategy(state, weights) : ApplyAllocation(state, state.BondShare);
	}

	/// <summary>Picks a coin, or lets it go. The amount follows the picked coins again.</summary>
	public MakerDeskState Toggle(MakerDeskState state, string coinId)
	{
		if (Coins.FirstOrDefault(coin => coin.Id == coinId) is not { } coin || !CanPick(state, coin))
		{
			return state;
		}

		// A set without the coin that is as large as before did not have it.
		var without = state.Selected.Remove(coinId);

		return state with
		{
			Selected = without.Count == state.Selected.Count ? state.Selected.Add(coinId) : without,
			BondAmount = null,
			OfferAmount = null
		};
	}

	/// <summary>Picks every coin that can be picked, or none if all of them are picked already.</summary>
	public MakerDeskState ToggleAll(MakerDeskState state)
	{
		var free = Coins.Where(coin => CanPick(state, coin)).Select(coin => coin.Id).ToArray();

		return state with
		{
			Selected = GetSelectAllState(state, free) == SelectAllState.All ? [] : [.. free],
			BondAmount = null,
			OfferAmount = null
		};
	}

	/// <summary>The list names the coins by their addresses, or by the outputs that they are.</summary>
	public MakerDeskState ToggleAddresses(MakerDeskState state) => state with { ShowsAddresses = !state.ShowsAddresses };

	/// <summary>
	/// Carries a state over to this desk from the desk of other coins or of another book. Coins that are gone, locked
	/// or in an offer by now are not picked any more. When that takes a coin out of what was picked, the strategy picks
	/// again if the marker is in the triangle; without the marker the rest stays picked, and the amount follows it.
	/// </summary>
	public MakerDeskState Rebase(MakerDeskState state)
	{
		ImmutableHashSet<string> kept = [.. Coins.Where(coin => state.Selected.Contains(coin.Id) && CanPick(state, coin)).Select(coin => coin.Id)];

		if (kept.Count == state.Selected.Count)
		{
			return state;
		}

		state = state with { Selected = kept, OfferAmount = null, BondAmount = null };

		return state.Strategy is { } weights ? ApplyStrategy(state, weights) : state;
	}

	/// <summary>After an offer was posted: the next three coins are picked, and the rates stay for the next offer.</summary>
	public MakerDeskState AfterOfferPosted(MakerDeskState state)
	{
		state = state with
		{
			Selected = [.. MakerCoins.GetDefaultOfferCoins(Coins, Taken, state.SortKey, state.SortAscending)],
			OfferAmount = null,
			OfferError = null,
			BondAmount = null
		};

		return state.Strategy is not null ? ApplyAllocation(state, state.BondShare) : state;
	}

	/// <summary>After an offer was taken back: its coins can be picked again.</summary>
	public MakerDeskState AfterOfferCancelled(MakerDeskState state)
	{
		state = state with { OfferError = null };

		return state.Strategy is { } weights ? ApplyStrategy(state, weights) : Rebase(state);
	}

	/// <summary>After a bond was locked: the screen moves on to List Offers, with the coins that the strategy picks there.</summary>
	public MakerDeskState AfterBondLocked(MakerDeskState state)
	{
		state = state with
		{
			Tab = MakerTab.Offers,
			Selected = [],
			BondAmount = null,
			BondBlocks = DefaultBondBlocks,
			BondFeeRate = DefaultBondFeeRate,
			BondError = null
		};

		return state.Strategy is { } weights ? ApplyStrategy(state, weights) : ApplyAllocation(state, state.BondShare);
	}

	/// <summary>What the picked coins are worth as an offer.</summary>
	public OfferFigures GetOfferFigures(MakerDeskState state) =>
		MakerCoins.GetOfferFigures(Coins, state.Selected, state.SortKey, state.SortAscending);

	/// <summary>What the state shows.</summary>
	public MakerDeskView GetView(MakerDeskState state)
	{
		var figures = GetOfferFigures(state);
		var selected = Coins.Where(coin => state.Selected.Contains(coin.Id)).ToArray();
		var direction = state.SortAscending ? 1 : -1;

		// The bonds on top, then by the column of the list. Between coins that the column does not tell apart, the one of more points, or the larger one.
		var rows = Coins
			.OrderBy(coin => coin.IsBond ? 0 : 1)
			.ThenBy(coin => direction * (state.SortKey == CoinSortKey.Points ? coin.Points ?? -1 : coin.Amount))
			.ThenByDescending(coin => state.SortKey == CoinSortKey.Amount ? coin.Points ?? -1 : coin.Amount)
			.Select(coin =>
			{
				var isSelected = state.Selected.Contains(coin.Id);

				return new MakerCoinRow(
					coin,
					isSelected,
					GetMark(state, figures, coin, isSelected),
					IsBarred: state.Tab == MakerTab.Bond && !coin.IsBond && !coin.IsPrivate,
					IsTaken: Taken.Contains(coin.Id));
			})
			.ToArray();

		var free = Coins.Where(coin => CanPick(state, coin)).Select(coin => coin.Id).ToArray();
		var isSellRateFixed = IsSellRateFixed(state, figures);

		return new MakerDeskView(
			rows,
			GetAllocationRows(state),
			GetRateRows(state, isSellRateFixed),
			IsAllocationEnabled: state.Tab == MakerTab.Bond && !HasBond,
			AreRatesEnabled: state.Tab == MakerTab.Offers,
			state.OfferAmount ?? (selected.Length > 0 && figures.Amount > 0 ? MarketFormat.Btc(figures.Amount) : ""),
			state.BondAmount ?? (selected.Length > 0 ? MarketFormat.Btc(selected.Sum(coin => coin.Amount)) : ""),
			figures.PointsText,
			isSellRateFixed ? NoPointsSellRate : state.OfferSellRate,
			isSellRateFixed,
			GetSelectAllState(state, free));
	}

	/// <summary>Checks the form of the offer. Either the offer is ready to be posted, or the state says what is missing.</summary>
	public (OfferDraft? Draft, MakerDeskState State) ValidateOffer(MakerDeskState state)
	{
		var view = GetView(state);
		var figures = GetOfferFigures(state);
		var selected = Coins.Where(coin => state.Selected.Contains(coin.Id)).ToArray();
		var amount = MarketFormat.ParseBtc(view.OfferAmount);

		// An offer without points has none to sell: its sell rate is 0, whatever was set before.
		var sellRateText = view.OfferSellRate;

		string? error = null;

		if (selected.Length == 0)
		{
			error = SelectCoinsError;
		}
		else if (amount is not > 0)
		{
			error = OfferAmountError;
		}
		else if (amount > figures.Amount)
		{
			error = $"Amount exceeds the counted coins: {MarketFormat.Btc(amount.Value)} BTC offered, {MarketFormat.Btc(figures.Amount)} BTC across the three largest coins from distinct origins.";
		}
		else if (MarketFormat.ParseNumber(state.OfferFeeRate) is not > 0)
		{
			error = OfferFeeRateError;
		}
		else if (MarketFormat.ParseNumber(sellRateText) is null)
		{
			error = OfferSellRateError;
		}
		else if (MarketFormat.ParseNumber(state.OfferBuyRate) is null)
		{
			error = OfferBuyRateError;
		}

		if (error is not null)
		{
			return (null, state with { OfferError = error });
		}

		var feeRate = MarketFormat.ParseNumber(state.OfferFeeRate)!.Value;
		var sellRate = MarketFormat.ParseNumber(sellRateText)!.Value;
		var buyRate = MarketFormat.ParseNumber(state.OfferBuyRate)!.Value;

		// What the market does not take as the terms of an offer, whatever was typed.
		if (MakerOffer.ValidateTerms(feeRate, buyRate, sellRate, figures.HasPoints) is { } reason)
		{
			return (null, state with { OfferError = reason });
		}

		var draft = new OfferDraft(
			[.. selected.Select(coin => coin.Id)],
			amount!.Value,
			feeRate,
			sellRate,
			buyRate,
			figures.Points,
			Math.Min(MakerCoins.CountedCoins, selected.Length));

		return (draft, state with { OfferError = null });
	}

	/// <summary>Checks the form of the bond. Either the bond is ready to be locked, or the state says what is missing.</summary>
	public (BondDraft? Draft, MakerDeskState State) ValidateBond(MakerDeskState state)
	{
		var view = GetView(state);
		var selected = Coins.Where(coin => state.Selected.Contains(coin.Id)).ToArray();
		var total = selected.Sum(coin => coin.Amount);
		var amount = MarketFormat.ParseBtc(view.BondAmount);
		var blocks = int.TryParse(state.BondBlocks, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedBlocks) ? parsedBlocks : 0;
		var feeRate = MarketFormat.ParseNumber(state.BondFeeRate);

		string? error = null;

		if (amount is not > 0)
		{
			error = BondAmountError;
		}
		else if (amount > total)
		{
			error = $"Amount exceeds the selected coins: {MarketFormat.Btc(amount.Value)} BTC requested, {MarketFormat.Btc(total)} BTC selected.";
		}
		else if (blocks <= 0)
		{
			error = BondBlocksError;
		}
		else if (feeRate is not > 0)
		{
			error = BondFeeRateError;
		}

		if (error is not null)
		{
			return (null, state with { BondError = error });
		}

		var draft = new BondDraft(
			[.. selected.Select(coin => coin.Id)],
			total,
			amount!.Value,
			blocks,
			feeRate!.Value,
			BondFigures.EstimateTransaction(total, amount.Value, selected.Length, feeRate.Value));

		return (draft, state with { BondError = null });
	}

	/// <summary>
	/// What is left of a text that is typed into a box for a number: digits and one decimal point, and after the point
	/// as many digits as the box takes.
	/// </summary>
	/// <param name="maxDecimals">How many decimals the box takes. None for a whole number, and null for any number of them.</param>
	public static string CleanNumber(string text, int? maxDecimals = null)
	{
		var cleaned = new StringBuilder();
		var hasPoint = false;
		var decimals = 0;

		foreach (var character in text)
		{
			if (character is >= '0' and <= '9')
			{
				if (hasPoint && maxDecimals is { } limit && decimals >= limit)
				{
					continue;
				}

				decimals += hasPoint ? 1 : 0;
				cleaned.Append(character);
			}
			else if (character == '.' && !hasPoint && maxDecimals != 0)
			{
				hasPoint = true;
				cleaned.Append(character);
			}
		}

		return cleaned.ToString();
	}

	/// <summary>Whether a coin can be picked on the tab that is open: not a bond, not in an offer, and on Lock Coins a private one.</summary>
	private bool CanPick(MakerDeskState state, MarketCoin coin) =>
		!coin.IsBond && !Taken.Contains(coin.Id) && !(state.Tab == MakerTab.Bond && !coin.IsPrivate);

	private static SelectAllState GetSelectAllState(MakerDeskState state, IReadOnlyCollection<string> free) =>
		state.Selected.Count == 0
			? SelectAllState.None
			: free.Count > 0 && free.All(state.Selected.Contains)
				? SelectAllState.All
				: SelectAllState.Some;

	/// <summary>The mark of a row. A row takes the first one that applies.</summary>
	private static CoinMark GetMark(MakerDeskState state, OfferFigures figures, MarketCoin coin, bool isSelected)
	{
		if (!isSelected)
		{
			return CoinMark.None;
		}

		if (!coin.HasPoints)
		{
			return CoinMark.NoPoints;
		}

		if (state.Tab != MakerTab.Offers)
		{
			return CoinMark.None;
		}

		return figures.SharedOrigin.Contains(coin.Id)
			? CoinMark.SharedOrigin
			: figures.Extra.Contains(coin.Id) ? CoinMark.Extra : CoinMark.None;
	}

	private MakerBarRow[] GetAllocationRows(MakerDeskState state)
	{
		var total = Total / 100_000_000m;

		MakerBarRow Row(string key, string label, double share)
		{
			var percent = (int)BookRules.RoundHalfUp(share * 100);
			var amount = ((decimal)share * total).ToString("0.0000", CultureInfo.InvariantCulture);

			return new MakerBarRow(
				key,
				label,
				percent,
				string.Create(CultureInfo.InvariantCulture, $"{percent}% of {total:0.0000} BTC"),
				$"{amount} BTC",
				amount);
		}

		return [Row("liq", "Liquidity", 1 - state.BondShare), Row("bond", "Bond", state.BondShare)];
	}

	/// <summary>
	/// Whether the offer that the state would make has no anonymity points: coins are picked for an offer, and none of
	/// the ones that count has a point. Such an offer lists a sell rate of 0.
	/// </summary>
	private static bool IsSellRateFixed(MakerDeskState state, OfferFigures figures) =>
		state.Tab == MakerTab.Offers && state.Selected.Count > 0 && !figures.HasPoints;

	private MakerBarRow[] GetRateRows(MakerDeskState state, bool isSellRateFixed)
	{
		MakerBarRow Row(string key, string label, BookCriterion rate, string text, string unit, Func<decimal, string> format)
		{
			if (MarketFormat.ParseNumber(text) is not { } value)
			{
				return new MakerBarRow(key, label, 0, "", MarketFormat.None, "");
			}

			var percent = MakerStrategy.GetBeatenPercent(Book, rate, value);

			return new MakerBarRow(key, label, percent, $"beats {percent}% of the book", $"{format(value)} {unit}", format(value));
		}

		return
		[
			Row("minFee", "Fee rate", BookCriterion.FeeRate, state.OfferFeeRate, "sat/vB", MarketFormat.FeeRate),
			Row("sell", "Sell rate", BookCriterion.SellRate, isSellRateFixed ? NoPointsSellRate : state.OfferSellRate, "sats / AP", MarketFormat.Rate),
			Row("buy", "Buy rate", BookCriterion.BuyRate, state.OfferBuyRate, "sats / AP", MarketFormat.Rate)
		];
	}
}
