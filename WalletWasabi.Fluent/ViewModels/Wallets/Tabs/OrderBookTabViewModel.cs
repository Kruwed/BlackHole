using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.JoinMarket;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>A row of the list of market makers: who the maker is, and what it offers.</summary>
/// <param name="Problem">Why the offer may not be on the market, or null if nothing is known against it.</param>
public record MarketMakerRow(string Maker, string FeeRate, string Spread, string Bid, string Ask, string Liquidity, string Bond, string? Problem)
{
	public bool HasProblem => Problem is not null;
}

/// <summary>
/// The tab of the order book: the market makers of the JoinMarket-style coinjoins with what they offer. It shows the
/// offer of this wallet and nobody else's: the wallet does not look for makers yet. The offer of the wallet and its
/// fidelity bonds are made on the Coins tab, out of the coins that are ticked there.
/// </summary>
[AppLifetime]
public partial class OrderBookTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "Order Book";

	/// <summary>What the list calls the maker that this wallet is.</summary>
	public const string OwnMakerName = "This wallet";

	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<MarketMakerRow> _marketMakers = [];

	public OrderBookTabViewModel(UiContext uiContext, IWalletModel walletModel, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		walletModel.MakerOffer.Changed
			.StartWith(walletModel.MakerOffer.Current)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(offer => MarketMakers = offer is null ? [] : [CreateRow(offer)]);
	}

	/// <summary>
	/// The columns of the list of market makers. The first one names the maker, the others tell what it offers.
	/// </summary>
	public static IReadOnlyList<string> Columns { get; } = ["Market Maker", "Fee Rate", "Spread", "Bid", "Ask", "Liquidity", "Bond"];

	public IReadOnlyList<string> MarketMakerColumns => Columns;

	/// <summary>The row of the offer that this wallet has on the market. An offer names no bond yet, so that column is empty.</summary>
	public static MarketMakerRow CreateRow(MakerOfferState state) =>
		new(
			OwnMakerName,
			FormatFeeRate(state.Offer.MinFeeRate),
			FormatPrice(state.Offer.Spread),
			FormatPrice(state.Offer.Bid),
			FormatPrice(state.Offer.Ask),
			$"{state.Offer.Liquidity.ToFormattedString()} BTC",
			Bond: "",
			state.Problem);

	public static string FormatFeeRate(FeeRate feeRate) =>
		$"{feeRate.SatoshiPerByte.ToString("0.###", CultureInfo.InvariantCulture)} sat/vB";

	/// <summary>A price of an anonymity point, in sats.</summary>
	public static string FormatPrice(Money price) =>
		price.Satoshi == 1 ? "1 sat" : $"{price.Satoshi.ToString("#,0", CultureInfo.InvariantCulture)} sats";
}
