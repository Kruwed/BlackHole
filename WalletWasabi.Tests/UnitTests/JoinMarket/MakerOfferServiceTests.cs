using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NNostr.Client;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Discoverability;
using WalletWasabi.JoinMarket;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The wallet has one offer on the market at most. It is made by the user, posted to the Nostr relays, posted again
/// before the relays' readers take it for gone, and taken back.
/// </summary>
public class MakerOfferServiceTests
{
	private static readonly FeeRate MinFeeRate = new(2m);
	private static readonly Money Bid = Money.Satoshis(100);
	private static readonly Money Ask = Money.Satoshis(150);

	private readonly KeyManager _keyManager = ServiceFactory.CreateKeyManager();
	private readonly List<SmartCoin> _wallet = [];
	private readonly FakePublisher _publisher = new();
	private readonly EventBus _eventBus = new();
	private readonly List<MakerOfferState?> _changes = [];
	private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

	public MakerOfferServiceTests()
	{
		_eventBus.Subscribe<MakerOfferChanged>(e => _changes.Add(e.Offer));
	}

	[Fact]
	public async Task NothingIsPostedUntilAnOfferIsMadeAsync()
	{
		using var service = CreateService();
		AddCoin(1m);

		Assert.Null(service.Current);

		await service.KeepOfferAsync(CancellationToken.None);
		_now += TimeSpan.FromHours(1);
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.Empty(_publisher.Events);
		Assert.Empty(_changes);
	}

	[Fact]
	public async Task OfferTellsTheTermsAndWhatItsCoinsHoldAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		AddCoin(5m);

		var answers = await service.PostAsync([first.Outpoint, second.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.All(answers, answer => Assert.True(answer.Accepted));

		var expected = new MakerOffer(MinFeeRate, Bid, Ask, Money.Coins(0.5m));
		var state = service.Current;

		Assert.NotNull(state);
		Assert.Equal(expected, state.Offer);
		Assert.Equal([first.Outpoint, second.Outpoint], state.Coins);
		Assert.Equal(_now, state.LastPosted);
		Assert.Null(state.Problem);
		Assert.Equal([state], _changes);

		// What the relays got: a signed event of the kind, for the network of the wallet, that tells this offer.
		var nostrEvent = Assert.Single(_publisher.Events);

		Assert.True(nostrEvent.Verify());
		Assert.Equal(MakerOfferEvent.Kind, nostrEvent.Kind);
		Assert.Equal(_now, nostrEvent.CreatedAt);
		Assert.True(MakerOfferEvent.TryParse(nostrEvent, Network.RegTest, out var told));
		Assert.Equal(expected, told);

		// The coins are not told: neither where they are nor what each of them holds.
		Assert.DoesNotContain(first.Outpoint.Hash.ToString(), nostrEvent.Content);
		Assert.DoesNotContain("30000000", nostrEvent.Content);
		Assert.DoesNotContain("20000000", nostrEvent.Content);
	}

	[Fact]
	public async Task OfferHasOneOrTwoCoinsOfTheWalletAsync()
	{
		using var service = CreateService();
		var coins = new[] { AddCoin(0.1m), AddCoin(0.2m), AddCoin(0.3m) };
		var foreign = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(_keyManager), 1m);

		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync([], MinFeeRate, Bid, Ask, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(coins.Select(coin => coin.Outpoint), MinFeeRate, Bid, Ask, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync([coins[0].Outpoint, foreign.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync([coins[0].Outpoint], MinFeeRate, bid: Ask, ask: Bid, CancellationToken.None));

		Assert.Null(service.Current);
		Assert.Empty(_publisher.Events);

		// The same coin named twice is one coin.
		await service.PostAsync([coins[0].Outpoint, coins[0].Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.Equal(Money.Coins(0.1m), service.Current?.Offer.Liquidity);
		Assert.Equal([coins[0].Outpoint], service.Current?.Coins);
	}

	[Fact]
	public async Task OfferThatNoRelayTookIsNotMadeAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		_publisher.Accepts = [false, false];

		var answers = await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.All(answers, answer => Assert.False(answer.Accepted));
		Assert.Null(service.Current);
		Assert.Empty(_changes);

		// Nothing is tried again behind the back of the user.
		_now += TimeSpan.FromHours(1);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Single(_publisher.Events);
	}

	[Fact]
	public async Task OneRelayThatTakesTheOfferIsEnoughAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		_publisher.Accepts = [false, true];

		var answers = await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.Equal([false, true], answers.Select(answer => answer.Accepted));
		Assert.NotNull(service.Current);
	}

	/// <summary>
	/// The kind is replaceable: a relay keeps the newest event of a key. So an offer is changed by posting another one
	/// with the same key, and the new one has to be the newer one even when it is made within the same second.
	/// </summary>
	[Fact]
	public async Task NewOfferTakesThePlaceOfTheOneBeforeAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);

		await service.PostAsync([first.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		await service.PostAsync([second.Outpoint], new FeeRate(7m), Money.Satoshis(10), Money.Satoshis(20), CancellationToken.None);
		await service.PostAsync([first.Outpoint, second.Outpoint], new FeeRate(7m), Money.Satoshis(10), Money.Satoshis(20), CancellationToken.None);

		Assert.Equal(new MakerOffer(new FeeRate(7m), Money.Satoshis(10), Money.Satoshis(20), Money.Coins(0.5m)), service.Current?.Offer);
		Assert.Equal(3, _publisher.Events.Count);
		Assert.Single(_publisher.Events.Select(e => e.PublicKey).Distinct());

		var times = _publisher.Events.Select(e => e.CreatedAt!.Value.ToUnixTimeSeconds()).ToArray();
		Assert.Equal([1_800_000_000, 1_800_000_001, 1_800_000_002], times);
	}

	[Fact]
	public async Task OfferStaysWhenNoRelayTakesTheOneThatWasToReplaceItAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);

		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		var standing = service.Current;

		_publisher.Accepts = [false, false];
		var answers = await service.PostAsync([coin.Outpoint], new FeeRate(9m), Bid, Ask, CancellationToken.None);

		Assert.All(answers, answer => Assert.False(answer.Accepted));
		Assert.Equal(standing, service.Current);

		// The next posting is by the same key, so that it replaces the offer that the relays have.
		_publisher.Accepts = [true, true];
		await service.PostAsync([coin.Outpoint], new FeeRate(9m), Bid, Ask, CancellationToken.None);

		Assert.Equal(new FeeRate(9m), service.Current?.Offer.MinFeeRate);
		Assert.Single(_publisher.Events.Select(e => e.PublicKey).Distinct());
	}

	/// <summary>
	/// An offer is taken back by an event of the same key that tells nothing and is newer, so the relays have nothing
	/// to tell about that key any more.
	/// </summary>
	[Fact]
	public async Task OfferIsTakenBackAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);

		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		var answers = await service.WithdrawAsync(CancellationToken.None);

		Assert.All(answers, answer => Assert.True(answer.Accepted));
		Assert.Null(service.Current);
		Assert.Null(_changes[^1]);

		var offer = _publisher.Events[0];
		var withdrawal = _publisher.Events[1];

		Assert.True(withdrawal.Verify());
		Assert.Equal(offer.PublicKey, withdrawal.PublicKey);
		Assert.Equal(MakerOfferEvent.Kind, withdrawal.Kind);
		Assert.True(withdrawal.CreatedAt > offer.CreatedAt);
		Assert.Equal("", withdrawal.Content);
		Assert.False(MakerOfferEvent.TryParse(withdrawal, Network.RegTest, out _));

		// Nothing is posted afterwards.
		_now += TimeSpan.FromHours(1);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Equal(2, _publisher.Events.Count);

		// There is nothing to take back twice.
		Assert.Empty(await service.WithdrawAsync(CancellationToken.None));
		Assert.Equal(2, _publisher.Events.Count);
	}

	[Fact]
	public async Task OfferIsGoneForTheWalletAlsoWhenNoRelayCouldBeToldAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);

		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		_publisher.Accepts = [false, false];
		await service.WithdrawAsync(CancellationToken.None);

		Assert.Null(service.Current);

		// It is not posted again, so whoever reads the relays takes it for gone when it is old enough.
		_now += TimeSpan.FromHours(1);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Equal(2, _publisher.Events.Count);
	}

	/// <summary>The relays cannot tell that two offers come from the same wallet: every offer has a key of its own.</summary>
	[Fact]
	public async Task EveryOfferHasAKeyOfItsOwnAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);

		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		await service.WithdrawAsync(CancellationToken.None);
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.Equal(_publisher.Events[0].PublicKey, _publisher.Events[1].PublicKey);
		Assert.NotEqual(_publisher.Events[0].PublicKey, _publisher.Events[2].PublicKey);

		// An offer that reached no relay leaves nothing of its key behind either.
		await service.WithdrawAsync(CancellationToken.None);
		_publisher.Accepts = [false, false];
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		_publisher.Accepts = [true, true];
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.NotEqual(_publisher.Events[^2].PublicKey, _publisher.Events[^1].PublicKey);
	}

	/// <summary>
	/// Whoever reads the relays takes an event of the kind for gone when it is 15 minutes old. A standing offer is
	/// posted again every 10 minutes.
	/// </summary>
	[Fact]
	public async Task StandingOfferIsPostedAgainBeforeItCountsAsGoneAsync()
	{
		Assert.True(MakerOfferService.RefreshPeriod < MakerOfferEvent.MaxAge);

		using var service = CreateService();
		var coin = AddCoin(1m);
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		var posted = _now;

		_now += MakerOfferService.RefreshPeriod - TimeSpan.FromSeconds(1);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Single(_publisher.Events);

		_now += TimeSpan.FromSeconds(1);
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.Equal(2, _publisher.Events.Count);
		Assert.Equal(posted + MakerOfferService.RefreshPeriod, _publisher.Events[1].CreatedAt);
		Assert.Equal(_publisher.Events[0].PublicKey, _publisher.Events[1].PublicKey);
		Assert.Equal(_publisher.Events[0].Content, _publisher.Events[1].Content);
		Assert.Equal(_now, service.Current?.LastPosted);

		// And so on, for as long as the offer stands.
		for (var i = 0; i < 5; i++)
		{
			_now += MakerOfferService.RefreshPeriod;
			await service.KeepOfferAsync(CancellationToken.None);
		}

		Assert.Equal(7, _publisher.Events.Count);
		Assert.All(_publisher.Events, e => Assert.True(MakerOfferEvent.TryParse(e, Network.RegTest, out _)));
	}

	[Fact]
	public async Task OfferThatReachedNoRelayIsTriedAgainSoonerAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		var posted = _now;

		_publisher.Accepts = [false, false];
		_now += MakerOfferService.RefreshPeriod;
		await service.KeepOfferAsync(CancellationToken.None);

		// The offer stands, and it tells what is wrong.
		Assert.NotNull(service.Current);
		Assert.Equal(posted, service.Current.LastPosted);
		Assert.Equal("No Nostr relay took it: one.example: no; two.example: no", service.Current.Problem);
		Assert.Equal(2, _publisher.Events.Count);

		_now += MakerOfferService.RetryPeriod - TimeSpan.FromSeconds(1);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Equal(2, _publisher.Events.Count);

		_publisher.Accepts = [true, false];
		_now += TimeSpan.FromSeconds(1);
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.Equal(3, _publisher.Events.Count);
		Assert.Null(service.Current.Problem);
		Assert.Equal(_now, service.Current.LastPosted);
	}

	/// <summary>A coin that was spent is not on offer any more. The offer goes on with the coin that is left.</summary>
	[Fact]
	public async Task OfferFollowsTheCoinsOfTheWalletAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		await service.PostAsync([first.Outpoint, second.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		// Nothing happened to the coins: nothing is posted.
		_now += TimeSpan.FromSeconds(30);
		await service.KeepOfferAsync(CancellationToken.None);
		Assert.Single(_publisher.Events);

		_wallet.Remove(first);
		_now += TimeSpan.FromSeconds(30);
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.Equal(2, _publisher.Events.Count);
		Assert.Equal([second.Outpoint], service.Current?.Coins);
		Assert.Equal(Money.Coins(0.2m), service.Current?.Offer.Liquidity);
		Assert.True(MakerOfferEvent.TryParse(_publisher.Events[1], Network.RegTest, out var told));
		Assert.Equal(new MakerOffer(MinFeeRate, Bid, Ask, Money.Coins(0.2m)), told);
		Assert.Equal(_publisher.Events[0].PublicKey, _publisher.Events[1].PublicKey);
	}

	[Fact]
	public async Task OfferWhoseCoinsWereAllSpentIsTakenBackAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		await service.PostAsync([first.Outpoint, second.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		_wallet.Clear();
		_now += TimeSpan.FromSeconds(30);
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.Null(service.Current);
		Assert.Null(_changes[^1]);
		Assert.Equal(2, _publisher.Events.Count);
		Assert.Equal("", _publisher.Events[1].Content);
		Assert.Equal(_publisher.Events[0].PublicKey, _publisher.Events[1].PublicKey);
	}

	/// <summary>
	/// When the application is closed, the wallet goes before the services do. That its coins cannot be asked for then
	/// does not mean that they were spent.
	/// </summary>
	[Fact]
	public async Task OfferIsLeftAloneWhileThereIsNoWalletToAskAsync()
	{
		IEnumerable<SmartCoin>? wallet = _wallet;
		using var service = new MakerOfferService(Network.RegTest, _publisher, () => wallet, _eventBus, () => _now);
		var coin = AddCoin(1m);
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		wallet = null;
		_now += MakerOfferService.RefreshPeriod;
		await service.KeepOfferAsync(CancellationToken.None);

		Assert.NotNull(service.Current);
		Assert.Single(_publisher.Events);

		// Without a wallet there are no coins to offer.
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None));
	}

	[Fact]
	public async Task OfferDoesNotOutliveTheApplicationAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);

		await service.StartAsync(CancellationToken.None);
		await service.PostAsync([coin.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);
		await service.StopAsync(CancellationToken.None);

		Assert.Null(service.Current);
		Assert.Equal(2, _publisher.Events.Count);
		Assert.Equal("", _publisher.Events[1].Content);
	}

	/// <summary>The whole way, with relays that are asked like the ones out there: one event per key is all that a relay ever has.</summary>
	[Fact]
	public async Task RelayHasOneEventOfAnOfferFromFirstToLastAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var otherRelay = new TestNostrRelay();
		var publisher = new NostrRelayPublisher([relay.Uri, otherRelay.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20));
		using var service = new MakerOfferService(Network.RegTest, publisher, () => _wallet, _eventBus, () => _now);

		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);

		// Posted.
		var answers = await service.PostAsync([first.Outpoint, second.Outpoint], MinFeeRate, Bid, Ask, CancellationToken.None);

		Assert.Equal([true, true], answers.Select(answer => answer.Accepted));
		AssertRelayTells(relay, new MakerOffer(MinFeeRate, Bid, Ask, Money.Coins(0.5m)));
		AssertRelayTells(otherRelay, new MakerOffer(MinFeeRate, Bid, Ask, Money.Coins(0.5m)));

		// Changed, within the same second.
		await service.PostAsync([first.Outpoint], new FeeRate(3m), Money.Satoshis(7), Money.Satoshis(9), CancellationToken.None);
		AssertRelayTells(relay, new MakerOffer(new FeeRate(3m), Money.Satoshis(7), Money.Satoshis(9), Money.Coins(0.3m)));

		// Posted again when it is due.
		_now += MakerOfferService.RefreshPeriod;
		await service.KeepOfferAsync(CancellationToken.None);
		AssertRelayTells(relay, new MakerOffer(new FeeRate(3m), Money.Satoshis(7), Money.Satoshis(9), Money.Coins(0.3m)));
		Assert.Equal(_now, relay.Stored.Single().CreatedAt);

		// Taken back: what the relay has of the key tells no offer any more.
		await service.WithdrawAsync(CancellationToken.None);

		Assert.False(MakerOfferEvent.TryParse(Assert.Single(relay.Stored), Network.RegTest, out _));
		Assert.False(MakerOfferEvent.TryParse(Assert.Single(otherRelay.Stored), Network.RegTest, out _));
		Assert.Equal(4, relay.Received.Count);
		Assert.All(relay.Received, e => Assert.True(e.Verify()));
	}

	private static void AssertRelayTells(TestNostrRelay relay, MakerOffer expected)
	{
		var stored = Assert.Single(relay.Stored);

		Assert.True(MakerOfferEvent.TryParse(stored, Network.RegTest, out var offer));
		Assert.Equal(expected, offer);
	}

	private MakerOfferService CreateService() =>
		new(Network.RegTest, _publisher, () => _wallet, _eventBus, () => _now);

	private SmartCoin AddCoin(decimal amountBtc)
	{
		var coin = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(_keyManager), amountBtc);

		_wallet.Add(coin);
		return coin;
	}

	/// <summary>Two relays that take or refuse what they are sent, as the test says.</summary>
	private sealed class FakePublisher : INostrPublisher
	{
		public IReadOnlyList<Uri> Relays { get; } = [new Uri("wss://one.example"), new Uri("wss://two.example")];

		public bool[] Accepts { get; set; } = [true, true];

		public List<NostrEvent> Events { get; } = [];

		public Task<IReadOnlyList<RelayAnswer>> PublishAsync(NostrEvent nostrEvent, CancellationToken cancellationToken)
		{
			Events.Add(nostrEvent);

			return Task.FromResult<IReadOnlyList<RelayAnswer>>(
				[.. Relays.Select((relay, i) => new RelayAnswer(relay, Accepts[i], Accepts[i] ? "" : "no"))]);
		}
	}
}
