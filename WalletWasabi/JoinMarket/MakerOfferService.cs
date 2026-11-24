using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using Nito.AsyncEx;
using NNostr.Client;
using WalletWasabi.Bases;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket.Planning;
using WalletWasabi.Logging;
using WalletWasabi.Services;

namespace WalletWasabi.JoinMarket;

/// <summary>What the user asks for when an offer is posted.</summary>
/// <param name="Coins">The coins that the offer holds.</param>
/// <param name="Amount">The amount that is offered, in sats. It is not more than the coins hold.</param>
/// <param name="Points">The anonymity points that the offer tells: those of the coins that count.</param>
/// <param name="MatchedInputs">How many of the coins count as matched inputs: the private ones, three at most.</param>
public sealed record OfferRequest(ImmutableArray<OutPoint> Coins, decimal MinFeeRate, decimal BuyRate, decimal SellRate, long Amount, decimal Points, int MatchedInputs)
{
	public int ExtraInputs => Coins.Length - MatchedInputs;
}

/// <summary>A fidelity bond of the wallet with the proof that it stands behind an offer.</summary>
/// <param name="Amount">What the bond holds, in sats.</param>
public sealed record BondBacking(BondProof Proof, long Amount);

/// <summary>An offer that the wallet has on the market.</summary>
/// <param name="Id">The number of the offer among the offers of the wallet since the application started.</param>
/// <param name="PublicKey">The key that signs the offer, in hex. Every offer has its own.</param>
/// <param name="Coins">The coins that the offer holds.</param>
/// <param name="CoinsTotal">What those coins hold together, in sats.</param>
/// <param name="Offer">What the relays are told.</param>
/// <param name="Bond">What the fidelity bond holds that stands behind the offer, in sats. Zero without one.</param>
/// <param name="LastPosted">When a relay took the offer the last time.</param>
/// <param name="Problem">Why the last posting of the offer reached no relay, or null if it reached one.</param>
public sealed record PostedOffer(
	int Id,
	string PublicKey,
	ImmutableArray<OutPoint> Coins,
	long CoinsTotal,
	MakerOffer Offer,
	long Bond,
	DateTimeOffset LastPosted,
	string? Problem)
{
	/// <summary>The name that the order book shows for the offer.</summary>
	public string Alias => MakerAlias.Get(PublicKey);
}

/// <summary>
/// Keeps the offers of the wallet on the Nostr relays: posts them, posts them again before they count as gone, and
/// takes them back. Offers are made by the user: nothing is posted before <see cref="PostAsync"/> is called.
/// </summary>
/// <remarks>
/// An offer lasts until it is taken back, until one of its coins is spent or until the application stops. It is not
/// stored, and every offer is signed with a new key, so that the relays cannot tell from the keys that two offers
/// come from the same wallet. A fidelity bond is another matter: the offers that it stands behind all name it.
/// </remarks>
public class MakerOfferService : PeriodicRunner
{
	/// <summary>How often a standing offer is posted again. Whoever reads the relays takes an offer that is older than <see cref="MakerOfferEvent.MaxAge"/> for gone.</summary>
	public static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(10);

	/// <summary>How long it takes until an offer that reached no relay is posted again.</summary>
	public static readonly TimeSpan RetryPeriod = TimeSpan.FromMinutes(1);

	/// <summary>How often it is checked whether the offers are due and whether their coins are still there.</summary>
	private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(20);

	/// <summary>How long the application waits at its end for the relays to hear that the offers are taken back. Through Tor a new connection takes seconds.</summary>
	private static readonly TimeSpan WithdrawalAtStopTimeout = TimeSpan.FromSeconds(15);

	private readonly Network _network;
	private readonly INostrPublisher _publisher;
	private readonly Func<IEnumerable<SmartCoin>?> _getUnspentCoins;
	private readonly Func<byte[], BondBacking?> _getBond;
	private readonly EventBus _eventBus;
	private readonly Func<DateTimeOffset> _now;
	private readonly AsyncLock _lock = new();
	private readonly List<Standing> _standing = [];

	private volatile PostedOffer[] _offers = [];
	private int _lastId;

	/// <param name="getUnspentCoins">Gives the coins that the wallet has now, or null while there is no wallet to ask.</param>
	/// <param name="getBond">Gives the fidelity bond of the wallet with the proof for the key of an offer, or null if no bond is locked.</param>
	public MakerOfferService(
		Network network,
		INostrPublisher publisher,
		Func<IEnumerable<SmartCoin>?> getUnspentCoins,
		EventBus eventBus,
		Func<byte[], BondBacking?>? getBond = null,
		Func<DateTimeOffset>? now = null)
		: base(CheckPeriod)
	{
		_network = network;
		_publisher = publisher;
		_getUnspentCoins = getUnspentCoins;
		_getBond = getBond ?? (_ => null);
		_eventBus = eventBus;
		_now = now ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>The offers that the wallet has on the market, in the order in which they were posted.</summary>
	public IReadOnlyList<PostedOffer> Offers => _offers;

	/// <summary>The relays that the offers are posted to.</summary>
	public IReadOnlyList<Uri> Relays => _publisher.Relays;

	/// <summary>
	/// Posts an offer of the given coins. It stands next to the offers that the wallet has on the market already,
	/// none of which holds any of these coins.
	/// </summary>
	/// <returns>
	/// The offer, and what every relay made of it. If none of them took it, there is no offer.
	/// </returns>
	/// <exception cref="ArgumentException">These are not the terms of an offer, or not coins that the wallet can offer.</exception>
	public async Task<(PostedOffer? Offer, IReadOnlyList<RelayAnswer> Answers)> PostAsync(OfferRequest request, CancellationToken cancellationToken)
	{
		var outPoints = request.Coins.Distinct().ToImmutableArray();

		if (outPoints.Length is 0 or > MakerOffer.MaxCoinCount)
		{
			throw new ArgumentException($"An offer has one to {MakerOffer.MaxCoinCount} coins.", nameof(request));
		}

		if (MakerOffer.ValidateTerms(request.MinFeeRate, request.BuyRate, request.SellRate) is { } reason)
		{
			throw new ArgumentException(reason, nameof(request));
		}

		if (request.MatchedInputs is < 0 or > MarketRules.MaxMatchedInputsPerMaker || request.MatchedInputs > outPoints.Length)
		{
			throw new ArgumentException($"Up to {MarketRules.MaxMatchedInputsPerMaker} coins of an offer count as matched, and not more than it has.", nameof(request));
		}

		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (FindUnspent(outPoints) is not { } unspentCoins || unspentCoins.Length != outPoints.Length)
			{
				throw new ArgumentException("The wallet does not have all of these coins any more.", nameof(request));
			}

			if (_standing.Any(standing => standing.Coins.Any(outPoints.Contains)))
			{
				throw new ArgumentException("One of these coins is in an offer already.", nameof(request));
			}

			var total = unspentCoins.Sum(coin => coin.Amount.Satoshi);

			if (request.Amount > total)
			{
				throw new ArgumentException("The coins hold less than the amount of the offer.", nameof(request));
			}

			// The offer has a key of its own. Until the offer stands, this is the only place that holds the key.
			Standing? fresh = new(++_lastId, outPoints, total);

			try
			{
				var standing = fresh;
				var bond = _getBond(standing.PublicKeyBytes);
				var offer = new MakerOffer(
					request.MinFeeRate,
					request.BuyRate,
					request.SellRate,
					request.Amount,
					request.Points,
					request.MatchedInputs,
					outPoints.Length - request.MatchedInputs,
					bond?.Proof);

				if (offer.Validate() is { } offerReason)
				{
					throw new ArgumentException(offerReason, nameof(request));
				}

				standing.Offer = offer;
				standing.Bond = bond?.Amount ?? 0;

				var answers = await PublishNoLockAsync(standing, MakerOfferEvent.Create(offer, _network, NextCreatedAt(standing)), cancellationToken).ConfigureAwait(false);

				if (!answers.Any(answer => answer.Accepted))
				{
					// A key that never posted anything has nothing to stand for.
					Logger.LogWarning($"The offer was not posted. {DescribeFailure(answers)}");
					return (null, answers);
				}

				standing.LastPosted = standing.LastAttempt;
				_standing.Add(standing);
				fresh = null;
				Announce();

				Logger.LogInfo($"The offer {standing.Alias} is posted. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
				return (standing.ToPostedOffer(), answers);
			}
			finally
			{
				fresh?.Dispose();
			}
		}
	}

	/// <summary>Takes an offer of the wallet off the market. The wallet does not have the offer afterwards, whether the relays could be told or not.</summary>
	/// <returns>What every relay made of it, or nothing if there was no such offer.</returns>
	public async Task<IReadOnlyList<RelayAnswer>> CancelAsync(int id, CancellationToken cancellationToken)
	{
		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			return _standing.FirstOrDefault(offer => offer.Id == id) is { } standing
				? await WithdrawNoLockAsync(standing, $"The offer {standing.Alias} is taken back.", cancellationToken).ConfigureAwait(false)
				: [];
		}
	}

	/// <summary>Takes every offer of the wallet off the market.</summary>
	public async Task CancelAllAsync(CancellationToken cancellationToken)
	{
		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			foreach (var standing in _standing.ToArray())
			{
				await WithdrawNoLockAsync(standing, $"The offer {standing.Alias} is taken back.", cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>An offer does not outlive the application: the relays are told that the offers are gone, as far as they can be reached in time.</summary>
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		// The loop is stopped first, so that it does not post an offer again behind the back of this.
		await base.StopAsync(cancellationToken).ConfigureAwait(false);

		if (Offers.Count == 0)
		{
			return;
		}

		using var timeoutCts = new CancellationTokenSource(WithdrawalAtStopTimeout);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

		try
		{
			await CancelAllAsync(linkedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Logger.LogInfo($"The relays were not told in time that the offers are gone. They take an offer for gone when it is {MakerOfferEvent.MaxAge.TotalMinutes:0} minutes old.");
		}
	}

	public override void Dispose()
	{
		foreach (var standing in _standing)
		{
			standing.Dispose();
		}

		_standing.Clear();
		base.Dispose();
		GC.SuppressFinalize(this);
	}

	protected override Task ActionAsync(CancellationToken cancel) => KeepOffersAsync(cancel);

	/// <summary>
	/// Looks after the standing offers: an offer whose coin was spent is taken back, and an offer that is due is posted again.
	/// </summary>
	internal async Task KeepOffersAsync(CancellationToken cancel)
	{
		if (Offers.Count == 0)
		{
			return;
		}

		using (await _lock.LockAsync(cancel).ConfigureAwait(false))
		{
			foreach (var standing in _standing.ToArray())
			{
				// While the wallet is being closed there is nobody to ask about the coins. The offers go when the application stops.
				if (FindUnspent(standing.Coins) is not { } unspentCoins)
				{
					return;
				}

				// What an offer tells is what its coins are. With one of them spent, that is not true any more.
				if (unspentCoins.Length != standing.Coins.Length)
				{
					await WithdrawNoLockAsync(standing, $"A coin of the offer {standing.Alias} was spent, so the offer is taken back.", cancel).ConfigureAwait(false);
					continue;
				}

				if (_now() - standing.LastAttempt < (standing.Problem is null ? RefreshPeriod : RetryPeriod))
				{
					continue;
				}

				// The bond is looked at again every time: one may have been locked since, and one may have unlocked.
				var bond = _getBond(standing.PublicKeyBytes);
				standing.Offer = standing.Offer! with { Bond = bond?.Proof };
				standing.Bond = bond?.Amount ?? 0;

				var answers = await PublishNoLockAsync(standing, MakerOfferEvent.Create(standing.Offer, _network, NextCreatedAt(standing)), cancel).ConfigureAwait(false);
				var isPosted = answers.Any(answer => answer.Accepted);
				var problem = isPosted ? null : DescribeFailure(answers);

				// What is wrong is told once, not every time it is tried again.
				if (problem is not null && problem != standing.Problem)
				{
					Logger.LogWarning($"The offer {standing.Alias} could not be posted again. {problem}");
				}
				else if (problem is null && standing.Problem is not null)
				{
					Logger.LogInfo($"The offer {standing.Alias} is on the relays again. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
				}
				else if (problem is null)
				{
					Logger.LogDebug($"The offer {standing.Alias} was posted again. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
				}

				standing.Problem = problem;

				if (isPosted)
				{
					standing.LastPosted = standing.LastAttempt;
				}

				Announce();
			}
		}
	}

	/// <summary>Tells in a sentence why no relay took an event.</summary>
	public static string DescribeFailure(IReadOnlyList<RelayAnswer> answers) =>
		answers.Count == 0
			? "There is no Nostr relay to post it to."
			: "No Nostr relay took it: " + string.Join("; ", answers.Select(answer => $"{answer.Relay.Host}: {(string.IsNullOrWhiteSpace(answer.Message) ? "refused" : answer.Message)}"));

	private static int CountAccepted(IReadOnlyList<RelayAnswer> answers) => answers.Count(answer => answer.Accepted);

	/// <summary>The coins of the wallet among the given ones, in the given order. Null while there is no wallet to ask.</summary>
	private SmartCoin[]? FindUnspent(ImmutableArray<OutPoint> outPoints)
	{
		if (_getUnspentCoins() is not { } walletCoins)
		{
			return null;
		}

		var unspentCoins = walletCoins.Where(coin => outPoints.Contains(coin.Outpoint)).ToDictionary(coin => coin.Outpoint);

		return [.. outPoints.Where(unspentCoins.ContainsKey).Select(outPoint => unspentCoins[outPoint])];
	}

	private async Task<IReadOnlyList<RelayAnswer>> WithdrawNoLockAsync(Standing standing, string message, CancellationToken cancellationToken)
	{
		try
		{
			var answers = await PublishNoLockAsync(standing, MakerOfferEvent.CreateWithdrawal(_network, NextCreatedAt(standing)), cancellationToken).ConfigureAwait(false);

			Logger.LogInfo(answers.Any(answer => answer.Accepted)
				? $"{message} {CountAccepted(answers)} of {answers.Count} Nostr relays were told."
				: $"{message} No Nostr relay could be told, so the relays take it for gone when it is {MakerOfferEvent.MaxAge.TotalMinutes:0} minutes old.");

			return answers;
		}
		finally
		{
			// Whatever the relays heard, the offer is not posted again from here on.
			_standing.Remove(standing);
			standing.Dispose();
			Announce();
		}
	}

	private async Task<IReadOnlyList<RelayAnswer>> PublishNoLockAsync(Standing standing, NostrEvent unsignedEvent, CancellationToken cancellationToken)
	{
		standing.LastAttempt = _now();

		var signedEvent = await unsignedEvent.ComputeIdAndSignAsync(standing.Key).ConfigureAwait(false);

		return await _publisher.PublishAsync(signedEvent, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The time that the next event of an offer carries. A relay keeps the newest event of a key, so every event has to
	/// be newer than the one before it, also when the two are made within the same second.
	/// </summary>
	private DateTimeOffset NextCreatedAt(Standing standing)
	{
		var now = DateTimeOffset.FromUnixTimeSeconds(_now().ToUnixTimeSeconds());

		standing.LastCreatedAt = now > standing.LastCreatedAt ? now : standing.LastCreatedAt.AddSeconds(1);
		return standing.LastCreatedAt;
	}

	private void Announce()
	{
		PostedOffer[] offers = [.. _standing.Select(standing => standing.ToPostedOffer())];

		_offers = offers;
		_eventBus.Publish(new MakerOffersChanged(offers));
	}

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}

	/// <summary>An offer that stands, with the key that signs it.</summary>
	private sealed class Standing : IDisposable
	{
		public Standing(int id, ImmutableArray<OutPoint> coins, long coinsTotal)
		{
			Id = id;
			Coins = coins;
			CoinsTotal = coinsTotal;
			Key = CreateKey();
			PublicKeyBytes = Key.CreateXOnlyPubKey().ToBytes();
		}

		public int Id { get; }
		public ECPrivKey Key { get; }
		public byte[] PublicKeyBytes { get; }
		public string PublicKey => Encoders.Hex.EncodeData(PublicKeyBytes);
		public string Alias => MakerAlias.Get(PublicKey);
		public ImmutableArray<OutPoint> Coins { get; }
		public long CoinsTotal { get; }
		public MakerOffer? Offer { get; set; }
		public long Bond { get; set; }
		public DateTimeOffset LastAttempt { get; set; }
		public DateTimeOffset LastCreatedAt { get; set; }
		public DateTimeOffset LastPosted { get; set; }
		public string? Problem { get; set; }

		public PostedOffer ToPostedOffer() =>
			new(Id, PublicKey, Coins, CoinsTotal, Offer ?? throw new InvalidOperationException("The offer has no terms yet."), Bond, LastPosted, Problem);

		public void Dispose() => Key.Dispose();
	}
}
