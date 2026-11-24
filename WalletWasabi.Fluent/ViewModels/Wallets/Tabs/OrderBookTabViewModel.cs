using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Market;
using WalletWasabi.JoinMarket;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.Logging;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab of the order book: the makers of the market for anonymity points with what they offer, as the Nostr relays
/// tell it, and the offers of this wallet among them. It is the book to look at. Makers are picked from it on the
/// Taker tab of the Coinjoin dialog, which has the same book with its filters.
/// </summary>
[AppLifetime]
public partial class OrderBookTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "Order Book";

	/// <summary>How often the relays are asked for the book while the tab shows.</summary>
	public static readonly TimeSpan RefreshPeriod = TimeSpan.FromSeconds(60);

	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<object> _rows = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _notice;

	public OrderBookTabViewModel(UiContext uiContext, IWalletModel walletModel, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		var market = walletModel.Market;

		market.BookChanged.ToSignal()
			.Merge(market.OffersChanged.ToSignal())
			.StartWith(System.Reactive.Unit.Default)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ =>
			{
				Rows = CreateRows(market.Book, market.Offers, GetOwnBond(walletModel));
				Notice = DescribeBook(market.Book);
			});

		// The relays are asked while the tab shows, at once and then every minute, and not otherwise.
		this.WhenAnyValue(x => x.IsSelected)
			.Select(isSelected => isSelected
				? Observable.Timer(TimeSpan.Zero, RefreshPeriod)
					.Select(_ => Observable.FromAsync(async cancellationToken =>
					{
						try
						{
							await market.RefreshBookAsync(cancellationToken);
						}
						catch (Exception ex) when (ex is not OperationCanceledException)
						{
							Logger.LogDebug(ex);
						}
					}))
					.Concat()
				: Observable.Empty<System.Reactive.Unit>())
			.Switch()
			.Subscribe();
	}

	/// <summary>The columns of the book. The first one names the maker, the others tell what it offers.</summary>
	public static IReadOnlyList<string> Columns { get; } = ["Maker", "Fee rate", "Spread", "Sell rate", "Buy rate", "Liquidity", "Bond"];

	public IReadOnlyList<string> MarketMakerColumns => Columns;

	/// <summary>
	/// The rows of the book: the makers with a fidelity bond first, then a line that counts the others, then the
	/// others. Within each part the lowest fee rate comes first. The offers of this wallet are among them.
	/// </summary>
	/// <param name="ownBond">What the fidelity bond of this wallet holds, in sats.</param>
	public static IReadOnlyList<object> CreateRows(OrderBookSnapshot book, IReadOnlyList<PostedOffer> ownOffers, long ownBond)
	{
		var aliases = MakerAlias.Disambiguate(book.Entries.Select(entry => entry.PublicKey).Concat(ownOffers.Select(offer => offer.PublicKey)));

		var others = book.Entries.Select((entry, index) => new BookMaker(
			index + 1,
			aliases[entry.PublicKey],
			entry.Offer.MinFeeRate,
			entry.Offer.BuyRate,
			entry.Offer.SellRate,
			entry.Offer.Spread,
			entry.Bond,
			entry.Offer.Liquidity,
			entry.Offer.MatchedInputs,
			entry.Offer.ExtraInputs,
			entry.Offer.Points,
			[]));

		var own = ownOffers.Select(offer => new BookMaker(
			1_000_000_000L + offer.Id,
			aliases[offer.PublicKey],
			offer.Offer.MinFeeRate,
			offer.Offer.BuyRate,
			offer.Offer.SellRate,
			offer.Offer.Spread,
			ownBond,
			offer.Offer.Liquidity,
			offer.Offer.MatchedInputs,
			offer.Offer.ExtraInputs,
			offer.Offer.Points,
			[],
			IsOwn: true));

		var desk = new TakerDesk([.. others], [.. own]);
		var view = desk.GetView(new TakerDeskState());
		var rows = new List<object>();

		rows.AddRange(view.Bonded.Select(row => new BookRowViewModel(row, toggleCommand: null)));

		if (view.Unbonded.Count > 0)
		{
			rows.Add(new BookCaptionViewModel(TakerScreenViewModel.DescribeUnbonded(view.Unbonded.Count)));
			rows.AddRange(view.Unbonded.Select(row => new BookRowViewModel(row, toggleCommand: null)));
		}

		return rows;
	}

	/// <summary>What the tab says about the book besides its rows, if there is anything to say.</summary>
	public static string? DescribeBook(OrderBookSnapshot book) =>
		book.ReadAt is null
			? "Asking your Nostr relays for offers…"
			: book.Problem ?? (book.Entries.Length == 0 ? "Your Nostr relays have no offers of other Makers." : book.BondProblem);

	private static long GetOwnBond(IWalletModel walletModel) =>
		walletModel.Bonds.GetBonds().Bonds
			.Where(bond => bond.State == FidelityBondState.Locked)
			.Select(bond => bond.Bond.Amount.Satoshi)
			.DefaultIfEmpty(0)
			.Max();
}
