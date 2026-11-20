using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using Nito.AsyncEx;
using NNostr.Client;
using WalletWasabi.Bases;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Discoverability;
using WalletWasabi.Logging;
using WalletWasabi.Services;

namespace WalletWasabi.JoinMarket;

/// <summary>The offer that the wallet has on the market.</summary>
/// <param name="Coins">The coins that are offered.</param>
/// <param name="Offer">What the relays are told.</param>
/// <param name="LastPosted">When a relay took the offer the last time.</param>
/// <param name="Problem">Why the last posting of the offer reached no relay, or null if it reached one.</param>
public record MakerOfferState(ImmutableArray<OutPoint> Coins, MakerOffer Offer, DateTimeOffset LastPosted, string? Problem);

/// <summary>
/// Keeps the offer of the wallet on the Nostr relays: posts it, posts it again before it counts as gone, and takes it
/// back. There is one offer at most, and it is made by the user: nothing is posted before <see cref="PostAsync"/> is called.
/// </summary>
/// <remarks>
/// An offer lasts until it is taken back or the application stops. It is not stored, and every offer is signed with a
/// new key, so that the relays cannot tell that two offers come from the same wallet.
/// </remarks>
public class MakerOfferService : PeriodicRunner
{
	/// <summary>How often a standing offer is posted again. Whoever reads the relays takes an offer that is older than <see cref="MakerOfferEvent.MaxAge"/> for gone.</summary>
	public static readonly TimeSpan RefreshPeriod = TimeSpan.FromMinutes(10);

	/// <summary>How long it takes until an offer that reached no relay is posted again.</summary>
	public static readonly TimeSpan RetryPeriod = TimeSpan.FromMinutes(1);

	/// <summary>How often it is checked whether the offer is due and whether its coins are still there.</summary>
	private static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(20);

	/// <summary>How long the application waits at its end for the relays to hear that the offer is taken back. Through Tor a new connection takes seconds.</summary>
	private static readonly TimeSpan WithdrawalAtStopTimeout = TimeSpan.FromSeconds(15);

	private readonly Network _network;
	private readonly INostrPublisher _publisher;
	private readonly Func<IEnumerable<SmartCoin>?> _getUnspentCoins;
	private readonly EventBus _eventBus;
	private readonly Func<DateTimeOffset> _now;
	private readonly AsyncLock _lock = new();

	private volatile MakerOfferState? _current;
	private ECPrivKey? _key;
	private DateTimeOffset _lastAttempt;
	private DateTimeOffset _lastCreatedAt;

	/// <param name="getUnspentCoins">Gives the coins that the wallet has now, or null while there is no wallet to ask.</param>
	public MakerOfferService(Network network, INostrPublisher publisher, Func<IEnumerable<SmartCoin>?> getUnspentCoins, EventBus eventBus, Func<DateTimeOffset>? now = null)
		: base(CheckPeriod)
	{
		_network = network;
		_publisher = publisher;
		_getUnspentCoins = getUnspentCoins;
		_eventBus = eventBus;
		_now = now ?? (() => DateTimeOffset.UtcNow);
	}

	/// <summary>The offer that the wallet has on the market, if it has one.</summary>
	public MakerOfferState? Current => _current;

	/// <summary>The relays that the offer is posted to.</summary>
	public IReadOnlyList<Uri> Relays => _publisher.Relays;

	/// <summary>
	/// Posts an offer of the given coins. It takes the place of the offer that the wallet has on the market, if there is one.
	/// </summary>
	/// <returns>
	/// What every relay made of the offer. If none of them took it, nothing has changed: there is no new offer, and the
	/// one that was on the market is still there.
	/// </returns>
	/// <exception cref="ArgumentException">These are not the terms of an offer, or not coins that the wallet can offer.</exception>
	public async Task<IReadOnlyList<RelayAnswer>> PostAsync(IEnumerable<OutPoint> coins, FeeRate minFeeRate, Money bid, Money ask, CancellationToken cancellationToken)
	{
		var outPoints = coins.Distinct().ToImmutableArray();

		if (outPoints.Length is 0 or > MakerOffer.MaxCoinCount)
		{
			throw new ArgumentException($"An offer has one to {MakerOffer.MaxCoinCount} coins.", nameof(coins));
		}

		if (MakerOffer.ValidateTerms(minFeeRate, bid, ask) is { } reason)
		{
			throw new ArgumentException(reason);
		}

		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (FindUnspent(outPoints) is not { } unspentCoins || unspentCoins.Length != outPoints.Length)
			{
				throw new ArgumentException("The wallet does not have all of these coins any more.", nameof(coins));
			}

			var offer = new MakerOffer(minFeeRate, bid, ask, unspentCoins.Sum(coin => coin.Amount));
			var answers = await PublishNoLockAsync(MakerOfferEvent.Create(offer, _network, NextCreatedAt()), cancellationToken).ConfigureAwait(false);

			if (answers.Any(answer => answer.Accepted))
			{
				SetCurrent(new MakerOfferState(outPoints, offer, _lastAttempt, Problem: null));
				Logger.LogInfo($"The offer is posted. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
			}
			else
			{
				Logger.LogWarning($"The offer was not posted. {DescribeFailure(answers)}");

				// A key that never posted anything has nothing to stand for.
				if (_current is null)
				{
					ForgetKey();
				}
			}

			return answers;
		}
	}

	/// <summary>Takes the offer of the wallet off the market. The wallet has no offer afterwards, whether the relays could be told or not.</summary>
	/// <returns>What every relay made of it, or nothing if there was no offer.</returns>
	public async Task<IReadOnlyList<RelayAnswer>> WithdrawAsync(CancellationToken cancellationToken)
	{
		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			return await WithdrawNoLockAsync("The offer is taken back.", cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>An offer does not outlive the application: the relays are told that it is gone, as far as they can be reached in time.</summary>
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		// The loop is stopped first, so that it does not post the offer again behind the back of this.
		await base.StopAsync(cancellationToken).ConfigureAwait(false);

		if (_current is null)
		{
			return;
		}

		using var timeoutCts = new CancellationTokenSource(WithdrawalAtStopTimeout);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

		try
		{
			await WithdrawAsync(linkedCts.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Logger.LogInfo($"The relays were not told in time that the offer is gone. They take it for gone when it is {MakerOfferEvent.MaxAge.TotalMinutes:0} minutes old.");
		}
	}

	public override void Dispose()
	{
		ForgetKey();
		base.Dispose();
		GC.SuppressFinalize(this);
	}

	protected override Task ActionAsync(CancellationToken cancel) => KeepOfferAsync(cancel);

	/// <summary>
	/// Looks after the standing offer: it follows the coins of the wallet, and it is posted again when it is due.
	/// </summary>
	internal async Task KeepOfferAsync(CancellationToken cancel)
	{
		if (_current is null)
		{
			return;
		}

		using (await _lock.LockAsync(cancel).ConfigureAwait(false))
		{
			if (_current is not { } current)
			{
				return;
			}

			// While the wallet is being closed there is nobody to ask about the coins. The offer goes when the application stops.
			if (FindUnspent(current.Coins) is not { } unspentCoins)
			{
				return;
			}

			// A coin that was spent is not on offer any more, and an offer without coins is none.
			if (unspentCoins.Length == 0)
			{
				await WithdrawNoLockAsync("The coins of the offer were spent, so the offer is taken back.", cancel).ConfigureAwait(false);
				return;
			}

			var offer = current.Offer with { Liquidity = unspentCoins.Sum(coin => coin.Amount) };
			var isChanged = offer != current.Offer;
			var isDue = _now() - _lastAttempt >= (current.Problem is null ? RefreshPeriod : RetryPeriod);

			if (!isChanged && !isDue)
			{
				return;
			}

			var answers = await PublishNoLockAsync(MakerOfferEvent.Create(offer, _network, NextCreatedAt()), cancel).ConfigureAwait(false);
			var isPosted = answers.Any(answer => answer.Accepted);
			var problem = isPosted ? null : DescribeFailure(answers);

			if (isChanged)
			{
				Logger.LogInfo("A coin of the offer was spent. The offer goes on with what is left.");
			}

			// What is wrong is told once, not every time it is tried again.
			if (problem is not null && problem != current.Problem)
			{
				Logger.LogWarning($"The offer could not be posted again. {problem}");
			}
			else if (problem is null && current.Problem is not null)
			{
				Logger.LogInfo($"The offer is on the relays again. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
			}
			else if (problem is null)
			{
				Logger.LogDebug($"The offer was posted again. {CountAccepted(answers)} of {answers.Count} Nostr relays took it.");
			}

			SetCurrent(new MakerOfferState(
				[.. unspentCoins.Select(coin => coin.Outpoint)],
				offer,
				isPosted ? _lastAttempt : current.LastPosted,
				problem));
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

	private async Task<IReadOnlyList<RelayAnswer>> WithdrawNoLockAsync(string message, CancellationToken cancellationToken)
	{
		if (_current is null)
		{
			return [];
		}

		try
		{
			var answers = await PublishNoLockAsync(MakerOfferEvent.CreateWithdrawal(_network, NextCreatedAt()), cancellationToken).ConfigureAwait(false);

			Logger.LogInfo(answers.Any(answer => answer.Accepted)
				? $"{message} {CountAccepted(answers)} of {answers.Count} Nostr relays were told."
				: $"{message} No Nostr relay could be told, so the relays take it for gone when it is {MakerOfferEvent.MaxAge.TotalMinutes:0} minutes old.");

			return answers;
		}
		finally
		{
			// Whatever the relays heard, the offer is not posted again from here on.
			ForgetKey();
			SetCurrent(null);
		}
	}

	private async Task<IReadOnlyList<RelayAnswer>> PublishNoLockAsync(NostrEvent unsignedEvent, CancellationToken cancellationToken)
	{
		_key ??= CreateKey();
		_lastAttempt = _now();

		var signedEvent = await unsignedEvent.ComputeIdAndSignAsync(_key).ConfigureAwait(false);

		return await _publisher.PublishAsync(signedEvent, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The time that the next event carries. A relay keeps the newest event of a key, so every event has to be newer than
	/// the one before it, also when the two are made within the same second.
	/// </summary>
	private DateTimeOffset NextCreatedAt()
	{
		var now = DateTimeOffset.FromUnixTimeSeconds(_now().ToUnixTimeSeconds());

		_lastCreatedAt = now > _lastCreatedAt ? now : _lastCreatedAt.AddSeconds(1);
		return _lastCreatedAt;
	}

	private void SetCurrent(MakerOfferState? state)
	{
		_current = state;
		_eventBus.Publish(new MakerOfferChanged(state));
	}

	private void ForgetKey()
	{
		_key?.Dispose();
		_key = null;
	}

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}
}
