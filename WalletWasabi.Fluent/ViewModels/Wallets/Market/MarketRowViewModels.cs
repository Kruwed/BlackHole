using System.Windows.Input;
using ReactiveUI;
using WalletWasabi.Fluent.Models.Wallets.Market;
using WalletWasabi.JoinMarket.OrderBook;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Market;

/// <summary>
/// A maker in the order book, as a row shows it. What the maker offers does not change while the row is there; whether
/// it is picked, and which filters cut it, does, and the row follows that without being made again.
/// </summary>
public partial class BookRowViewModel : ReactiveObject
{
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isAvailable;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _feeRateFails;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _spreadFails;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _sellRateFails;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _buyRateFails;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _liquidityFails;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _bondFails;

	/// <param name="toggleCommand">What a click on the row does, with the id of the maker. Null where makers are only looked at.</param>
	public BookRowViewModel(BookRow row, ICommand? toggleCommand)
	{
		var maker = row.Maker;

		Maker = maker;
		Id = maker.Id;
		Alias = maker.Alias;
		IsOwn = maker.IsOwn;
		FeeRate = MarketFormat.FeeRate(maker.MinFeeRate);
		Spread = MarketFormat.Rate(maker.Spread);
		SellRate = MarketFormat.Rate(maker.SellRate);
		BuyRate = MarketFormat.Rate(maker.BuyRate);
		Liquidity = MarketFormat.Btc(maker.Liquidity);
		Bond = MarketFormat.BtcOrNone(maker.Bond);
		ToggleCommand = toggleCommand;

		Update(row);
	}

	/// <summary>The maker of the row.</summary>
	public BookMaker Maker { get; }

	public long Id { get; }

	public string Alias { get; }

	/// <summary>Whether the offer is one of this wallet. It carries a "you" tag then.</summary>
	public bool IsOwn { get; }

	public string FeeRate { get; }

	public string Spread { get; }

	public string SellRate { get; }

	public string BuyRate { get; }

	public string Liquidity { get; }

	public string Bond { get; }

	public ICommand? ToggleCommand { get; }

	/// <summary>Takes over what changes about the row of a maker: whether it is picked, and what the filters make of it.</summary>
	public void Update(BookRow row)
	{
		IsSelected = row.IsSelected;
		IsAvailable = row.IsAvailable;
		FeeRateFails = row.Fails[BookRules.IndexOf(BookCriterion.FeeRate)];
		SpreadFails = row.Fails[BookRules.IndexOf(BookCriterion.Spread)];
		SellRateFails = row.Fails[BookRules.IndexOf(BookCriterion.SellRate)];
		BuyRateFails = row.Fails[BookRules.IndexOf(BookCriterion.BuyRate)];
		LiquidityFails = row.Fails[BookRules.IndexOf(BookCriterion.Liquidity)];
		BondFails = row.Fails[BookRules.IndexOf(BookCriterion.Bond)];
	}
}

/// <summary>The line between the two parts of the order book: "34 Unbonded Makers".</summary>
public class BookCaptionViewModel(string text)
{
	public string Text { get; } = text;
}

/// <summary>
/// A coin of the wallet, as a row of the maker's list shows it. The coin stays what it is while the row is there; whether
/// it is picked, how it is marked and how it is named changes, and the row follows that without being made again.
/// </summary>
public partial class MakerCoinRowViewModel : ReactiveObject
{
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _reference = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _lockText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSelected;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isNotPrivate;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isSharedOrigin;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isExtra;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isBarred;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isTaken;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _canToggle;

	/// <param name="info">Where the coin is, if the market tells. Without it the coin is named by its id.</param>
	/// <param name="toggleCommand">What a click on the row does, with the id of the coin.</param>
	public MakerCoinRowViewModel(MakerCoinRow row, MarketCoinInfo? info, bool showsAddresses, ICommand toggleCommand)
	{
		var coin = row.Coin;

		Coin = coin;
		Id = coin.Id;
		Points = coin.Points is { } points ? MarketFormat.Points(points) : "?";
		Amount = MarketFormat.Btc(coin.Amount);
		IsBond = coin.IsBond;
		ToggleCommand = toggleCommand;

		Update(row, info, showsAddresses);
	}

	/// <summary>The coin of the row.</summary>
	public MarketCoin Coin { get; }

	public string Id { get; }

	public string Points { get; }

	public string Amount { get; }

	/// <summary>Whether the coin is locked in a fidelity bond. Such a coin has a padlock in place of a tick.</summary>
	public bool IsBond { get; }

	public bool IsFree => !IsBond;

	/// <summary>How the row is marked, if it is: the first that applies of not private, shared origin and extra.</summary>
	public CoinMark Mark { get; private set; }

	public ICommand ToggleCommand { get; }

	/// <summary>Takes over what changes about the row of a coin.</summary>
	public void Update(MakerCoinRow row, MarketCoinInfo? info, bool showsAddresses)
	{
		Reference = info is null ? row.Coin.Id : showsAddresses ? info.AddressText : info.OutPointText;
		LockText = info?.BlocksLeft is { } blocks ? MarketFormat.Whole(blocks) : "";
		IsSelected = row.IsSelected;
		Mark = row.Mark;
		IsNotPrivate = row.Mark == CoinMark.NotPrivate;
		IsSharedOrigin = row.Mark == CoinMark.SharedOrigin;
		IsExtra = row.Mark == CoinMark.Extra;
		IsBarred = row.IsBarred;
		IsTaken = row.IsTaken;
		CanToggle = row.CanToggle;
	}
}

/// <summary>An offer that the wallet has on the market, as a card on the List Offers tab.</summary>
public class OfferCardViewModel
{
	public OfferCardViewModel(MarketOfferInfo offer, ICommand cancelCommand)
	{
		Id = offer.Id;
		Alias = offer.Alias;
		Status = offer.Problem is null ? "active" : "not posted";
		Problem = offer.Problem;
		Coins = offer.CoinsText;
		Amount = MarketFormat.Btc(offer.Amount);
		Total = MarketFormat.Btc(offer.CoinsTotal);
		Points = MakerCoins.FormatPoints(offer.Points);
		FeeRate = $"{MarketFormat.FeeRate(offer.FeeRate)} sat/vB";
		Spread = $"{MarketFormat.Rate(offer.SellRate - offer.BuyRate)} sats / AP";
		SellRate = $"{MarketFormat.Rate(offer.SellRate)} sats / AP";
		BuyRate = $"{MarketFormat.Rate(offer.BuyRate)} sats / AP";
		CancelCommand = cancelCommand;
	}

	public int Id { get; }

	public string Alias { get; }

	public string Status { get; }

	/// <summary>Why the offer may not be on the market, or null if nothing is known against it.</summary>
	public string? Problem { get; }

	public bool HasProblem => Problem is not null;

	public string Coins { get; }

	/// <summary>The amount that is offered.</summary>
	public string Amount { get; }

	/// <summary>What the coins of the offer hold together.</summary>
	public string Total { get; }

	public string Points { get; }

	public string FeeRate { get; }

	public string Spread { get; }

	public string SellRate { get; }

	public string BuyRate { get; }

	public ICommand CancelCommand { get; }
}

/// <summary>A fidelity bond of the wallet, as the Lock Coins tab tells it.</summary>
public class BondCardViewModel
{
	public BondCardViewModel(MarketBondInfo bond, BondStanding standing, ICommand redeemCommand)
	{
		Bond = bond;
		Amount = $"{MarketFormat.Btc(bond.Amount)} BTC";
		LockedFor = bond.LockedFor is { } blocks ? Blocks(blocks) : MarketFormat.None;
		UnlockTime = bond.BlocksLeft is { } left ? $"{Blocks(left)} left" : MarketFormat.None;
		Rank = $"{MarketFormat.Whole(standing.Rank)} of {MarketFormat.Whole(standing.Ranked)}";
		Percentile = $"Top {standing.TopPercent}%";
		Status = bond.Status;
		CanRedeem = bond.CanRedeem;
		RedeemCommand = redeemCommand;
	}

	public MarketBondInfo Bond { get; }

	public string Amount { get; }

	public string LockedFor { get; }

	public string UnlockTime { get; }

	public string Rank { get; }

	public string Percentile { get; }

	public string? Status { get; }

	public bool HasStatus => Status is not null;

	public bool CanRedeem { get; }

	public ICommand RedeemCommand { get; }

	private static string Blocks(long blocks) => $"{MarketFormat.Whole(blocks)} {(blocks == 1 ? "block" : "blocks")}";
}
