using System.Collections.Generic;
using System.Globalization;
using System.Reactive.Linq;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.JoinMarket;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>A row of the list of market makers: who the maker is, and what it offers.</summary>
/// <param name="Problem">Why the offer may not be on the market, or null if nothing is known against it.</param>
public record MarketMakerRow(string Maker, string FeeRate, string Spread, string Bid, string Ask, string Liquidity, string Bond, string? Problem)
{
	public bool HasProblem => Problem is not null;
}

/// <summary>
/// The tab of the JoinMarket-style coinjoins. The wallet can be a market maker: it has fidelity bonds, and it posts an
/// offer of its coins to the Nostr relays. Both are opened from here, and this tab is the only place that opens them.
/// The list of the market makers shows the offer of this wallet and nobody else's: the wallet does not look for makers yet.
/// </summary>
[AppLifetime]
public partial class JoinMarketTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "JoinMarket";

	/// <summary>What the list calls the maker that this wallet is.</summary>
	public const string OwnMakerName = "This wallet";

	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isWalletLoaded;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<MarketMakerRow> _marketMakers = [];

	public JoinMarketTabViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		// The bonds are read from the transactions of the wallet, so they wait for the wallet to know them all.
		walletModel.WhenAnyValue(x => x.IsLoaded)
			.BindTo(this, x => x.IsWalletLoaded);

		// The fidelity bonds: coins that are locked until the chain reaches a block height.
		BondCommand = ReactiveCommand.Create(
			() => UiContext.Navigate().To().Bonds(walletModel, wallet),
			this.WhenAnyValue(x => x.IsWalletLoaded));

		// The offer: coins that a taker may put into its coinjoin. It is made of the coins of the wallet, so it waits for them as well.
		OfferCommand = ReactiveCommand.Create(
			() => UiContext.Navigate().To().MakerOffer(walletModel),
			this.WhenAnyValue(x => x.IsWalletLoaded));

		walletModel.MakerOffer.Changed
			.StartWith(walletModel.MakerOffer.Current)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(offer => MarketMakers = offer is null ? [] : [CreateRow(offer)]);
	}

	/// <summary>
	/// The columns of the list of market makers. The first one names the maker, the others tell what it offers.
	/// </summary>
	public static IReadOnlyList<string> Columns { get; } = ["Market Maker", "Fee Rate", "Spread", "Bid", "Ask", "Liquidity", "Bond"];

	/// <summary>Opens the fidelity bonds of the wallet.</summary>
	public ICommand BondCommand { get; }

	/// <summary>Opens the offer of the wallet.</summary>
	public ICommand OfferCommand { get; }

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
