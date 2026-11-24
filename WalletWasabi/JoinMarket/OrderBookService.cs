using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Nito.AsyncEx;
using NNostr.Client;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Logging;
using WalletWasabi.Services;

namespace WalletWasabi.JoinMarket;

/// <summary>The offer of another maker as the relays tell it.</summary>
/// <param name="PublicKey">The key that signed the offer, in hex.</param>
/// <param name="Bond">What the fidelity bond behind the offer holds, in sats, as the node confirmed it. Zero without a bond, or with one that was not proven.</param>
/// <param name="CreatedAt">When the maker posted the offer the last time.</param>
public sealed record BookEntry(string PublicKey, MakerOffer Offer, long Bond, DateTimeOffset CreatedAt);

/// <summary>What became of asking one relay for offers.</summary>
/// <param name="Problem">Why the relay could not be asked, or did not answer to the end. Null if it sent all that it has.</param>
public sealed record BookRelay(Uri Relay, string? Problem);

/// <summary>The offers of the other makers at one moment.</summary>
/// <param name="Entries">The offers, one for every key, in the order of their keys.</param>
/// <param name="ReadAt">When the relays were asked. Null as long as they were never asked.</param>
/// <param name="Relays">What became of asking each relay.</param>
/// <param name="BondProblem">Why the bonds could not be checked, if they could not: the offers are there, and none of them counts as bonded.</param>
public sealed record OrderBookSnapshot(ImmutableArray<BookEntry> Entries, DateTimeOffset? ReadAt, ImmutableArray<BookRelay> Relays, string? BondProblem)
{
	public static OrderBookSnapshot Empty { get; } = new([], null, [], null);

	/// <summary>Whether any relay answered. Without one the book is not known, which is not the same as empty.</summary>
	public bool IsAnswered => Relays.Any(relay => relay.Problem is null);

	/// <summary>Why the book is not known, or null if a relay answered or none was asked yet.</summary>
	public string? Problem =>
		ReadAt is null || IsAnswered
			? null
			: Relays.Length == 0
				? "There is no Nostr relay to ask."
				: "No Nostr relay answered: " + string.Join("; ", Relays.Select(relay => $"{relay.Relay.Host}: {relay.Problem}"));
}

/// <summary>
/// Reads the offers of the makers from the Nostr relays: the order book that a taker picks from, and that a maker
/// measures its own offer against.
/// </summary>
/// <remarks>
/// The relays are only asked when somebody asks this service: nothing is read while nobody looks at the book.
/// What a relay sends is not trusted. An event counts if it is signed by the key that it names, of the kind, the
/// type and the network of the offers, and not older than such an event may be. Of several events of a key the
/// newest counts, and an empty one means that the offer was taken back. A fidelity bond counts if the node finds it,
/// see <see cref="BondProofVerifier"/>. The offers of this wallet are left out: the wallet knows them better.
/// </remarks>
public class OrderBookService
{
	/// <summary>How many offers a book holds at most. What the relays send beyond that is left out, the newest offers first in.</summary>
	public const int MaxEntries = 2_000;

	private readonly Network _network;
	private readonly INostrReader _reader;
	private readonly BondProofVerifier? _bondVerifier;
	private readonly Func<IReadOnlyCollection<string>> _getOwnKeys;
	private readonly EventBus _eventBus;
	private readonly Func<DateTimeOffset> _now;
	private readonly AsyncLock _lock = new();

	private volatile OrderBookSnapshot _current = OrderBookSnapshot.Empty;

	/// <param name="bondVerifier">Checks the fidelity bonds of the offers with the node. Without it no offer counts as bonded.</param>
	/// <param name="getOwnKeys">Gives the keys of the offers that this wallet has on the market, in hex.</param>
	public OrderBookService(
		Network network,
		INostrReader reader,
		BondProofVerifier? bondVerifier,
		Func<IReadOnlyCollection<string>> getOwnKeys,
		EventBus eventBus,
		Func<DateTimeOffset>? now = null)
	{
		_network = network;
		_reader = reader;
		_bondVerifier = bondVerifier;
		_getOwnKeys = getOwnKeys;
		_eventBus = eventBus;
		_now = now ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>What the relays told the last time that they were asked.</summary>
	public OrderBookSnapshot Current => _current;

	/// <summary>The relays that are asked.</summary>
	public IReadOnlyList<Uri> Relays => _reader.Relays;

	/// <summary>
	/// Asks the relays for the offers and checks the fidelity bonds that the offers name. If no relay answers, the book
	/// keeps the offers that it had, as far as they are not too old by now.
	/// </summary>
	public async Task<OrderBookSnapshot> RefreshAsync(CancellationToken cancellationToken)
	{
		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			var now = _now();
			var answers = await _reader.ReadAsync(MakerOfferEvent.Kind, now - MakerOfferEvent.MaxAge, cancellationToken).ConfigureAwait(false);
			ImmutableArray<BookRelay> relays = [.. answers.Select(answer => new BookRelay(answer.Relay, answer.Problem))];

			OrderBookSnapshot book;

			if (answers.Any(answer => answer.IsAnswered) || answers.Any(answer => answer.Events.Count > 0))
			{
				var offers = ReadOffers(answers.SelectMany(answer => answer.Events), now);
				var (entries, bondProblem) = await CheckBondsAsync(offers, cancellationToken).ConfigureAwait(false);

				book = new OrderBookSnapshot(entries, now, relays, bondProblem);
			}
			else
			{
				// What was read before is still what is known, until it is too old to count.
				var previous = _current;

				book = new OrderBookSnapshot(
					[.. previous.Entries.Where(entry => entry.CreatedAt >= now - MakerOfferEvent.MaxAge)],
					now,
					relays,
					previous.BondProblem);
			}

			_current = book;
			_eventBus.Publish(new OrderBookChanged(book));

			return book;
		}
	}

	/// <summary>The offers that the events tell: the newest event of every key, as far as it tells an offer, and none of this wallet's own.</summary>
	internal (string PublicKey, MakerOffer Offer, DateTimeOffset CreatedAt)[] ReadOffers(IEnumerable<NostrEvent> events, DateTimeOffset now)
	{
		var ownKeys = _getOwnKeys().ToHashSet();
		var newest = new Dictionary<string, NostrEvent>();

		foreach (var nostrEvent in events)
		{
			if (nostrEvent.PublicKey is not { Length: 64 } publicKey
				|| ownKeys.Contains(publicKey)
				|| !MakerOfferEvent.IsAboutAnOffer(nostrEvent, _network)
				|| !MakerOfferEvent.IsCurrent(nostrEvent, now)
				|| !IsSignedByItsKey(nostrEvent))
			{
				continue;
			}

			// The newer one counts. Of two events of the same second, the one with the lower id, as the relays have it.
			if (!newest.TryGetValue(publicKey, out var current)
				|| nostrEvent.CreatedAt > current.CreatedAt
				|| (nostrEvent.CreatedAt == current.CreatedAt && string.CompareOrdinal(nostrEvent.Id, current.Id) < 0))
			{
				newest[publicKey] = nostrEvent;
			}
		}

		var offers = new List<(string PublicKey, MakerOffer Offer, DateTimeOffset CreatedAt)>();

		foreach (var (publicKey, nostrEvent) in newest)
		{
			// An event without an offer in it takes the offer of its key back.
			if (MakerOfferEvent.TryParse(nostrEvent, _network, out var offer))
			{
				offers.Add((publicKey, offer, nostrEvent.CreatedAt!.Value));
			}
		}

		return [.. offers
			.OrderByDescending(offer => offer.CreatedAt)
			.Take(MaxEntries)
			.OrderBy(offer => offer.PublicKey, StringComparer.Ordinal)];
	}

	private async Task<(ImmutableArray<BookEntry> Entries, string? BondProblem)> CheckBondsAsync(
		(string PublicKey, MakerOffer Offer, DateTimeOffset CreatedAt)[] offers,
		CancellationToken cancellationToken)
	{
		var claims = offers
			.Where(offer => offer.Offer.Bond is not null)
			.Select(offer => (offer.PublicKey, offer.Offer.Bond!))
			.ToArray();

		var bonds = new Dictionary<string, long>();
		string? problem = null;

		if (claims.Length > 0 && _bondVerifier is { } verifier)
		{
			try
			{
				var amounts = await verifier.VerifyAsync(claims, cancellationToken).ConfigureAwait(false);

				for (var i = 0; i < claims.Length; i++)
				{
					bonds[claims[i].PublicKey] = amounts[i];
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				// Without the node there is no telling which bonds are there: none of them counts until it answers.
				problem = $"The Bitcoin node could not be asked about the fidelity bonds: {ex.Message}";
				Logger.LogDebug(problem);
			}
		}

		return ([.. offers.Select(offer => new BookEntry(offer.PublicKey, offer.Offer, bonds.GetValueOrDefault(offer.PublicKey), offer.CreatedAt))], problem);
	}

	/// <summary>Whether the id of the event is the hash of what it says, and the signature is that of the key that it names.</summary>
	private static bool IsSignedByItsKey(NostrEvent nostrEvent)
	{
		try
		{
			return nostrEvent.Id is { Length: 64 } && nostrEvent.Signature is { Length: 128 } && nostrEvent.Verify();
		}
		catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
		{
			// Anybody can send an event with anything in it. What cannot be checked does not count.
			return false;
		}
	}
}
