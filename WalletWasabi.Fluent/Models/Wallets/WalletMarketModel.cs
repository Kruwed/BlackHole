using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.JoinMarket;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>
/// The market for anonymity points as the wallet takes part in it: the offers that it has on the Nostr relays as a
/// maker, and the offers of the other makers, which are the order book of a taker.
/// </summary>
[AppLifetime]
public class WalletMarketModel
{
	private readonly IServices _services;
	private readonly MakerOfferService? _offers;
	private readonly OrderBookService _book;

	public WalletMarketModel(IServices services)
	{
		_services = services;
		_offers = services.GetHostedService<MakerOfferService>();
		_book = services.GetOrderBook();

		OffersChanged = services.EventBus.AsObservable<MakerOffersChanged>().Select(e => e.Offers);
		BookChanged = services.EventBus.AsObservable<OrderBookChanged>().Select(e => e.Book);
	}

	/// <summary>The offers that the wallet has on the market, every time one is posted, posted again or taken back.</summary>
	public IObservable<IReadOnlyList<PostedOffer>> OffersChanged { get; }

	/// <summary>The offers that the wallet has on the market.</summary>
	public IReadOnlyList<PostedOffer> Offers => _offers?.Offers ?? [];

	/// <summary>The offers of the other makers, every time the relays were asked.</summary>
	public IObservable<OrderBookSnapshot> BookChanged { get; }

	/// <summary>What the relays told about the offers of the other makers the last time that they were asked.</summary>
	public OrderBookSnapshot Book => _book.Current;

	/// <summary>The relays that offers are posted to and read from.</summary>
	public IReadOnlyList<Uri> Relays => _book.Relays;

	/// <summary>Whether the relays are reached through Tor.</summary>
	public bool IsTorUsed => _services.GetUseTor() != TorMode.Disabled;

	/// <summary>Posts an offer. It stands next to the offers that the wallet has on the market already.</summary>
	/// <returns>The offer, and what every relay made of it. If none of them took it, there is no offer.</returns>
	public Task<(PostedOffer? Offer, IReadOnlyList<RelayAnswer> Answers)> PostAsync(OfferRequest request, CancellationToken cancellationToken) =>
		_offers is { } offers
			? Task.Run(() => offers.PostAsync(request, cancellationToken), cancellationToken)
			: Task.FromResult<(PostedOffer?, IReadOnlyList<RelayAnswer>)>((null, []));

	/// <summary>Takes an offer of the wallet off the market.</summary>
	public Task CancelAsync(int id, CancellationToken cancellationToken) =>
		_offers is { } offers
			? Task.Run(() => offers.CancelAsync(id, cancellationToken), cancellationToken)
			: Task.CompletedTask;

	/// <summary>Asks the relays for the offers of the other makers.</summary>
	public Task<OrderBookSnapshot> RefreshBookAsync(CancellationToken cancellationToken) =>
		Task.Run(() => _book.RefreshAsync(cancellationToken), cancellationToken);
}
