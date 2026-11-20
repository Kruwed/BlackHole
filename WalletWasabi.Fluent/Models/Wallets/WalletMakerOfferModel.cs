using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Discoverability;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.JoinMarket;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>
/// The offer of the wallet as a market maker of JoinMarket-style coinjoins: up to two coins that a taker may use, and the
/// prices of the anonymity points. The offer is on the Nostr relays for as long as it stands.
/// </summary>
[AppLifetime]
public class WalletMakerOfferModel
{
	private readonly IServices _services;
	private readonly MakerOfferService? _service;

	public WalletMakerOfferModel(IServices services)
	{
		_services = services;
		_service = services.GetHostedService<MakerOfferService>();

		Changed = services.EventBus.AsObservable<MakerOfferChanged>().Select(e => e.Offer);
	}

	/// <summary>The offer that the wallet has on the market, every time it changes. Null when the offer is gone.</summary>
	public IObservable<MakerOfferState?> Changed { get; }

	/// <summary>The offer that the wallet has on the market, if it has one.</summary>
	public MakerOfferState? Current => _service?.Current;

	/// <summary>The relays that an offer is posted to.</summary>
	public IReadOnlyList<Uri> Relays => _service?.Relays ?? [];

	/// <summary>Whether the relays are reached through Tor.</summary>
	public bool IsTorUsed => _services.GetUseTor() != TorMode.Disabled;

	/// <summary>Posts an offer of the given coins. It takes the place of the offer that the wallet has on the market, if there is one.</summary>
	/// <returns>What every relay made of the offer. The offer stands if one of them took it.</returns>
	public Task<IReadOnlyList<RelayAnswer>> PostAsync(IEnumerable<SmartCoin> coins, FeeRate minFeeRate, Money bid, Money ask, CancellationToken cancellationToken) =>
		_service is { } service
			? Task.Run(() => service.PostAsync(coins.Select(coin => coin.Outpoint).ToArray(), minFeeRate, bid, ask, cancellationToken), cancellationToken)
			: Task.FromResult<IReadOnlyList<RelayAnswer>>([]);

	/// <summary>Takes the offer of the wallet off the market.</summary>
	public Task WithdrawAsync(CancellationToken cancellationToken) =>
		_service is { } service
			? Task.Run(() => service.WithdrawAsync(cancellationToken), cancellationToken)
			: Task.CompletedTask;
}
