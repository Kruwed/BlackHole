using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.DataEncoders;
using NBitcoin.RPC;
using NNostr.Client;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The offers of the wallet as a maker. Each one is made by the user, has coins and a key of its own, is posted to the
/// Nostr relays, posted again before the relays' readers take it for gone, and taken back.
/// </summary>
public class MakerOfferServiceTests
{
	private const decimal MinFeeRate = 2m;
	private const decimal BuyRate = 100m;
	private const decimal SellRate = 150m;

	private readonly KeyManager _keyManager = ServiceFactory.CreateKeyManager();
	private readonly List<SmartCoin> _wallet = [];
	private readonly FakePublisher _publisher = new();
	private readonly EventBus _eventBus = new();
	private readonly List<IReadOnlyList<PostedOffer>> _changes = [];
	private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
	private bool _isWalletOpen = true;

	public MakerOfferServiceTests()
	{
		_eventBus.Subscribe<MakerOffersChanged>(e => _changes.Add(e.Offers));
	}

	[Fact]
	public async Task NothingIsPostedUntilAnOfferIsMadeAsync()
	{
		using var service = CreateService();
		AddCoin(1m);

		Assert.Empty(service.Offers);

		await service.KeepOffersAsync(CancellationToken.None);
		_now += TimeSpan.FromHours(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Empty(_publisher.Events);
		Assert.Empty(_changes);
	}

	[Fact]
	public async Task OfferTellsItsTermsItsAmountItsPointsAndHowManyCoinsItHasAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		var third = AddCoin(0.1m);
		AddCoin(5m);

		var (posted, answers) = await service.PostAsync(
			new OfferRequest([first.Outpoint, second.Outpoint, third.Outpoint], MinFeeRate, BuyRate, SellRate, Amount: 40_000_000, Points: 1.5m, MatchedInputs: 2),
			CancellationToken.None);

		Assert.All(answers, answer => Assert.True(answer.Accepted));

		var expected = new MakerOffer(MinFeeRate, BuyRate, SellRate, 40_000_000, 1.5m, MatchedInputs: 2, ExtraInputs: 1);

		Assert.NotNull(posted);
		Assert.Equal(1, posted.Id);
		Assert.Equal(expected, posted.Offer);
		Assert.Equal([first.Outpoint, second.Outpoint, third.Outpoint], posted.Coins);
		Assert.Equal(60_000_000, posted.CoinsTotal);
		Assert.Equal(0, posted.Bond);
		Assert.Equal(_now, posted.LastPosted);
		Assert.Null(posted.Problem);
		Assert.Equal(MakerAlias.Get(posted.PublicKey), posted.Alias);

		Assert.Equal([posted], service.Offers);
		Assert.Equal([posted], Assert.Single(_changes));

		// What the relays were sent: the offer, signed by its key, with nothing in it that names a coin.
		var sent = Assert.Single(_publisher.Events);
		Assert.True(sent.Verify());
		Assert.Equal(posted.PublicKey, sent.PublicKey);
		Assert.Equal(_now, sent.CreatedAt);
		Assert.True(MakerOfferEvent.TryParse(sent, Network.RegTest, out var told));
		Assert.Equal(expected, told);
		Assert.DoesNotContain(first.Outpoint.Hash.ToString(), sent.Content);
	}

	[Fact]
	public async Task OfferHasOneToTwentyCoinsOfTheWalletAsync()
	{
		using var service = CreateService();
		var coins = Enumerable.Range(0, 21).Select(_ => AddCoin(0.01m)).ToArray();
		using var foreignKey = new Key();
		var foreign = BitcoinFactory.CreateSmartCoin(BitcoinFactory.CreateHdPubKey(ServiceFactory.CreateKeyManager()), 1m);

		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([]), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request(coins), CancellationToken.None));

		// A coin that the wallet does not have.
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([coins[0], foreign]), CancellationToken.None));

		Assert.Empty(_publisher.Events);
		Assert.Empty(service.Offers);

		var (posted, _) = await service.PostAsync(Request(coins[..20], matchedInputs: 3), CancellationToken.None);

		Assert.NotNull(posted);
		Assert.Equal(20, posted.Coins.Length);
		Assert.Equal(new MakerOffer(MinFeeRate, BuyRate, SellRate, 20_000_000, 1m, MatchedInputs: 3, ExtraInputs: 17), posted.Offer);
	}

	[Fact]
	public async Task CoinThatIsNamedTwiceIsOneCoinAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(0.5m);

		var (posted, _) = await service.PostAsync(
			new OfferRequest([coin.Outpoint, coin.Outpoint], MinFeeRate, BuyRate, SellRate, 50_000_000, 1m, MatchedInputs: 1),
			CancellationToken.None);

		Assert.NotNull(posted);
		Assert.Equal([coin.Outpoint], posted.Coins);
		Assert.Equal(1, posted.Offer.CoinCount);
	}

	[Fact]
	public async Task AmountOfAnOfferIsWhatItsCoinsHoldAtMostAndMoreThanDustAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(0.5m);

		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([coin], amount: 50_000_001), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([coin], amount: 329), CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([coin], amount: 0), CancellationToken.None));

		Assert.Empty(_publisher.Events);

		var (posted, _) = await service.PostAsync(Request([coin], amount: 50_000_000), CancellationToken.None);
		Assert.NotNull(posted);
	}

	[Fact]
	public async Task TermsThatMakeNoOfferAreRefusedAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(0.5m);
		var request = Request([coin]);

		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { SellRate = BuyRate - 1 }, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { MinFeeRate = 0m }, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { Points = -1m }, CancellationToken.None));

		// Three coins of an offer count as matched at most, and not more than it has.
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { MatchedInputs = 2 }, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { MatchedInputs = 4 }, CancellationToken.None));
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(request with { MatchedInputs = -1 }, CancellationToken.None));

		Assert.Empty(_publisher.Events);
		Assert.Empty(service.Offers);
		Assert.Empty(_changes);
	}

	/// <summary>An offer without anonymity points has none to sell: it is posted with a sell rate of 0, whatever was asked for.</summary>
	[Fact]
	public async Task OfferWithoutPointsIsPostedWithASellRateOfZeroAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(0.5m);

		var (posted, _) = await service.PostAsync(Request([coin]) with { Points = 0m }, CancellationToken.None);

		Assert.NotNull(posted);
		Assert.Equal(0m, posted.Offer.Points);
		Assert.Equal(0m, posted.Offer.SellRate);
		Assert.Equal(BuyRate, posted.Offer.BuyRate);

		var sent = Assert.Single(_publisher.Events);
		Assert.Contains("\"sell\":0,", sent.Content);
		Assert.Contains("\"buy\":100,", sent.Content);
		Assert.True(MakerOfferEvent.TryParse(sent, Network.RegTest, out var told));
		Assert.Equal(posted.Offer, told);

		// With points the offer sells at what was asked for.
		var (other, _) = await service.PostAsync(Request([AddCoin(0.2m)]) with { Points = 0.5m }, CancellationToken.None);
		Assert.Equal(SellRate, other!.Offer.SellRate);
	}

	[Fact]
	public async Task SeveralOffersStandNextToEachOtherEachWithAKeyOfItsOwnAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);

		var (one, _) = await service.PostAsync(Request([first]), CancellationToken.None);
		var (two, _) = await service.PostAsync(Request([second]) with { MinFeeRate = 5m }, CancellationToken.None);

		Assert.NotNull(one);
		Assert.NotNull(two);
		Assert.Equal([1, 2], service.Offers.Select(offer => offer.Id));
		Assert.NotEqual(one.PublicKey, two.PublicKey);
		Assert.Equal([one.PublicKey, two.PublicKey], _publisher.Events.Select(e => e.PublicKey));
		Assert.Equal(2, _changes.Count);
		Assert.Equal(2, _changes[^1].Count);

		// What the relays have of the first is not replaced by the second: both are in what they tell.
		Assert.True(MakerOfferEvent.TryParse(_publisher.Events[0], Network.RegTest, out var firstTold));
		Assert.True(MakerOfferEvent.TryParse(_publisher.Events[1], Network.RegTest, out var secondTold));
		Assert.Equal(MinFeeRate, firstTold.MinFeeRate);
		Assert.Equal(5m, secondTold.MinFeeRate);
	}

	[Fact]
	public async Task CoinIsInOneOfferAtATimeAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);

		var (posted, _) = await service.PostAsync(Request([first]), CancellationToken.None);
		Assert.NotNull(posted);

		var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([second, first]), CancellationToken.None));
		Assert.StartsWith("One of these coins is in an offer already.", exception.Message);
		Assert.Single(_publisher.Events);

		// Once its offer is taken back the coin is free again.
		await service.CancelAsync(posted.Id, CancellationToken.None);
		var (again, _) = await service.PostAsync(Request([second, first]), CancellationToken.None);

		Assert.NotNull(again);
		Assert.Equal(2, again.Id);
	}

	[Fact]
	public async Task OfferThatNoRelayTookIsNotMadeAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		_publisher.Accepts = [false, false];

		var (posted, answers) = await service.PostAsync(Request([coin]), CancellationToken.None);

		Assert.Null(posted);
		Assert.Equal([false, false], answers.Select(answer => answer.Accepted));
		Assert.Empty(service.Offers);
		Assert.Empty(_changes);
		Assert.Equal("No Nostr relay took it: one.example: no; two.example: no", MakerOfferService.DescribeFailure(answers));

		// Nothing holds the coin.
		_publisher.Accepts = [true, true];
		(posted, _) = await service.PostAsync(Request([coin]), CancellationToken.None);
		Assert.NotNull(posted);
	}

	[Fact]
	public async Task OneRelayThatTakesTheOfferIsEnoughAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		_publisher.Accepts = [false, true];

		var (posted, answers) = await service.PostAsync(Request([coin]), CancellationToken.None);

		Assert.NotNull(posted);
		Assert.Equal([false, true], answers.Select(answer => answer.Accepted));
		Assert.Single(service.Offers);
	}

	[Fact]
	public void FailureWithoutRelaysSaysThatThereIsNone()
	{
		Assert.Equal("There is no Nostr relay to post it to.", MakerOfferService.DescribeFailure([]));
	}

	/// <summary>An offer is taken back by an event of its key that tells nothing, which takes the place of the offer on the relays.</summary>
	[Fact]
	public async Task OfferIsTakenBackAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		var (one, _) = await service.PostAsync(Request([first]), CancellationToken.None);
		var (two, _) = await service.PostAsync(Request([second]), CancellationToken.None);

		var answers = await service.CancelAsync(one!.Id, CancellationToken.None);

		Assert.Equal([true, true], answers.Select(answer => answer.Accepted));
		Assert.Equal([two!], service.Offers);
		Assert.Equal([two!], _changes[^1]);

		var withdrawal = _publisher.Events[^1];
		Assert.True(withdrawal.Verify());
		Assert.Equal(one.PublicKey, withdrawal.PublicKey);
		Assert.Equal("", withdrawal.Content);
		Assert.True(MakerOfferEvent.IsAboutAnOffer(withdrawal, Network.RegTest));

		// It replaces the offer, so it is not older than the offer, even within the same second.
		Assert.True(withdrawal.CreatedAt > _publisher.Events[0].CreatedAt);
	}

	[Fact]
	public async Task OfferThatIsNotThereCannotBeTakenBackAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		await service.PostAsync(Request([coin]), CancellationToken.None);

		Assert.Empty(await service.CancelAsync(7, CancellationToken.None));
		Assert.Single(service.Offers);
		Assert.Single(_publisher.Events);
	}

	[Fact]
	public async Task OfferIsGoneForTheWalletAlsoWhenNoRelayCouldBeToldAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		var (posted, _) = await service.PostAsync(Request([coin]), CancellationToken.None);
		_publisher.Accepts = [false, false];

		var answers = await service.CancelAsync(posted!.Id, CancellationToken.None);

		Assert.Equal([false, false], answers.Select(answer => answer.Accepted));
		Assert.Empty(service.Offers);

		// It is not posted again.
		_publisher.Accepts = [true, true];
		_now += TimeSpan.FromHours(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Equal(2, _publisher.Events.Count);
	}

	[Fact]
	public async Task EveryOfferIsTakenBackAtOnceAsync()
	{
		using var service = CreateService();
		await service.PostAsync(Request([AddCoin(0.3m)]), CancellationToken.None);
		await service.PostAsync(Request([AddCoin(0.2m)]), CancellationToken.None);

		await service.CancelAllAsync(CancellationToken.None);

		Assert.Empty(service.Offers);
		Assert.Equal(4, _publisher.Events.Count);
		Assert.All(_publisher.Events.Skip(2), e => Assert.Equal("", e.Content));
		Assert.Equal(_publisher.Events.Take(2).Select(e => e.PublicKey), _publisher.Events.Skip(2).Select(e => e.PublicKey));
	}

	/// <summary>A reader takes an offer for gone when it is a quarter of an hour old. The offer is posted again every ten minutes.</summary>
	[Fact]
	public async Task StandingOfferIsPostedAgainBeforeItCountsAsGoneAsync()
	{
		using var service = CreateService();
		var (posted, _) = await service.PostAsync(Request([AddCoin(1m)]), CancellationToken.None);
		var postedAt = _now;

		Assert.True(MakerOfferService.RefreshPeriod < MakerOfferEvent.MaxAge);

		_now += MakerOfferService.RefreshPeriod - TimeSpan.FromSeconds(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Single(_publisher.Events);

		_now += TimeSpan.FromSeconds(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Equal(2, _publisher.Events.Count);

		var again = _publisher.Events[1];
		Assert.Equal(posted!.PublicKey, again.PublicKey);
		Assert.Equal(_now, again.CreatedAt);
		Assert.True(MakerOfferEvent.TryParse(again, Network.RegTest, out var told));
		Assert.Equal(posted.Offer, told);

		var standing = Assert.Single(service.Offers);
		Assert.Equal(_now, standing.LastPosted);
		Assert.NotEqual(postedAt, standing.LastPosted);
		Assert.Null(standing.Problem);
	}

	[Fact]
	public async Task OfferThatReachedNoRelayIsTriedAgainSoonerAsync()
	{
		using var service = CreateService();
		await service.PostAsync(Request([AddCoin(1m)]), CancellationToken.None);
		var postedAt = _now;

		_publisher.Accepts = [false, false];
		_now += MakerOfferService.RefreshPeriod;
		await service.KeepOffersAsync(CancellationToken.None);

		// The offer stands, and tells what is wrong with it.
		var standing = Assert.Single(service.Offers);
		Assert.Equal("No Nostr relay took it: one.example: no; two.example: no", standing.Problem);
		Assert.Equal(postedAt, standing.LastPosted);

		_now += MakerOfferService.RetryPeriod - TimeSpan.FromSeconds(1);
		await service.KeepOffersAsync(CancellationToken.None);
		Assert.Equal(2, _publisher.Events.Count);

		_publisher.Accepts = [true, false];
		_now += TimeSpan.FromSeconds(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Equal(3, _publisher.Events.Count);
		standing = Assert.Single(service.Offers);
		Assert.Null(standing.Problem);
		Assert.Equal(_now, standing.LastPosted);
	}

	/// <summary>What an offer tells is what its coins are. When one of them is spent, the offer is not true any more, and goes.</summary>
	[Fact]
	public async Task OfferIsTakenBackWhenOneOfItsCoinsIsSpentAsync()
	{
		using var service = CreateService();
		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);
		var third = AddCoin(0.1m);
		var (pair, _) = await service.PostAsync(Request([first, second]), CancellationToken.None);
		var (single, _) = await service.PostAsync(Request([third]), CancellationToken.None);

		_wallet.Remove(second);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Equal([single!], service.Offers);
		Assert.Equal(3, _publisher.Events.Count);
		Assert.Equal(pair!.PublicKey, _publisher.Events[2].PublicKey);
		Assert.Equal("", _publisher.Events[2].Content);

		// The coin that is left can be offered again.
		var (again, _) = await service.PostAsync(Request([first]), CancellationToken.None);
		Assert.NotNull(again);
	}

	[Fact]
	public async Task OffersAreLeftAloneWhileThereIsNoWalletToAskAsync()
	{
		using var service = CreateService();
		var coin = AddCoin(1m);
		await service.PostAsync(Request([coin]), CancellationToken.None);

		_isWalletOpen = false;
		_now += TimeSpan.FromHours(1);
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.Single(service.Offers);
		Assert.Single(_publisher.Events);

		// Without a wallet there are no coins to offer.
		await Assert.ThrowsAsync<ArgumentException>(() => service.PostAsync(Request([AddCoin(1m)]), CancellationToken.None));
	}

	[Fact]
	public async Task OffersDoNotOutliveTheApplicationAsync()
	{
		using var service = CreateService();

		await service.StartAsync(CancellationToken.None);
		await service.PostAsync(Request([AddCoin(1m)]), CancellationToken.None);
		await service.PostAsync(Request([AddCoin(2m)]), CancellationToken.None);
		await service.StopAsync(CancellationToken.None);

		Assert.Empty(service.Offers);
		Assert.Equal(4, _publisher.Events.Count);
		Assert.All(_publisher.Events.Skip(2), e => Assert.Equal("", e.Content));
	}

	/// <summary>
	/// The fidelity bond of the wallet stands behind every offer: each one carries a proof that the key of the bond
	/// signed for the key of that offer. The bond is looked at again every time an offer is posted.
	/// </summary>
	[Fact]
	public async Task BondOfTheWalletStandsBehindEveryOfferAsync()
	{
		using var bondKey = new Key();
		var bondOutPoint = new OutPoint(RandomUtils.GetUInt256(), 0);
		var hasBond = true;

		using var service = new MakerOfferService(
			Network.RegTest,
			_publisher,
			() => _wallet,
			_eventBus,
			offerKey => hasBond ? new BondBacking(BondProof.Create(bondKey, bondOutPoint, 900_000, offerKey), 30_000_000) : null,
			() => _now);

		var (one, _) = await service.PostAsync(Request([AddCoin(0.3m)]), CancellationToken.None);
		var (two, _) = await service.PostAsync(Request([AddCoin(0.2m)]), CancellationToken.None);

		Assert.Equal(30_000_000, one!.Bond);
		Assert.Equal(30_000_000, two!.Bond);

		foreach (var (posted, sent) in new[] { one, two }.Zip(_publisher.Events))
		{
			Assert.True(MakerOfferEvent.TryParse(sent, Network.RegTest, out var told));
			Assert.NotNull(told.Bond);
			Assert.Equal(bondOutPoint, told.Bond.OutPoint);
			Assert.Equal(900_000u, told.Bond.UnlockHeight);
			Assert.True(told.Bond.IsSignedFor(Encoders.Hex.DecodeData(posted.PublicKey)));
		}

		// The proof of one offer does not hold for the other.
		Assert.False(one.Offer.Bond!.IsSignedFor(Encoders.Hex.DecodeData(two.PublicKey)));

		// The bond has unlocked, or was paid back: the offers go on without it.
		hasBond = false;
		_now += MakerOfferService.RefreshPeriod;
		await service.KeepOffersAsync(CancellationToken.None);

		Assert.All(service.Offers, offer => Assert.Equal(0, offer.Bond));
		Assert.All(service.Offers, offer => Assert.Null(offer.Offer.Bond));
		Assert.All(_publisher.Events.Skip(2), e => Assert.DoesNotContain("bond", e.Content));
	}

	/// <summary>
	/// The whole way, with relays that are asked like the ones out there. One event per key is all that a relay ever
	/// has, and whoever reads the relays finds the offers with their bonds.
	/// </summary>
	[Fact]
	public async Task OffersReachTheBookOfAnotherWalletThroughTheRelaysAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var otherRelay = new TestNostrRelay();
		var makerRelays = new NostrRelayPublisher([relay.Uri, otherRelay.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20));
		var takerRelays = new NostrRelayPublisher([relay.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20));

		// The maker has a bond that is in a block of the chain that the taker's node has.
		using var bondKey = new Key();
		var bondOutPoint = new OutPoint(RandomUtils.GetUInt256(), 0);
		var bondScript = BondScript.GetScriptPubKey(BondScript.CreateLeaf(bondKey.PubKey, 900_000));
		var node = new MockRpcClient
		{
			OnGetBlockCountAsync = () => Task.FromResult(800_000),
			OnGetTxOutAsync = (txid, index, _) => new OutPoint(txid, index) == bondOutPoint
				? new GetTxOutResponse { Confirmations = 10, TxOut = new TxOut(Money.Coins(0.3m), bondScript) }
				: null
		};

		using var maker = new MakerOfferService(
			Network.RegTest,
			makerRelays,
			() => _wallet,
			_eventBus,
			offerKey => new BondBacking(BondProof.Create(bondKey, bondOutPoint, 900_000, offerKey), 30_000_000),
			() => _now);
		var takerBook = new OrderBookService(Network.RegTest, takerRelays, new BondProofVerifier(node), () => [], new EventBus(), () => _now);

		var first = AddCoin(0.3m);
		var second = AddCoin(0.2m);

		// Posted.
		var (one, answers) = await maker.PostAsync(Request([first]), CancellationToken.None);
		var (two, _) = await maker.PostAsync(Request([second]) with { MinFeeRate = 7m }, CancellationToken.None);

		Assert.Equal([true, true], answers.Select(answer => answer.Accepted));
		Assert.Equal(2, relay.Stored.Count);
		Assert.Equal(2, otherRelay.Stored.Count);

		var book = await takerBook.RefreshAsync(CancellationToken.None);

		Assert.Null(book.Problem);
		Assert.Equal(new[] { one!.PublicKey, two!.PublicKey }.OrderBy(key => key, StringComparer.Ordinal), book.Entries.Select(entry => entry.PublicKey));
		Assert.Equal(7m, book.Entries.Single(entry => entry.PublicKey == two.PublicKey).Offer.MinFeeRate);
		Assert.All(book.Entries, entry => Assert.Equal(30_000_000, entry.Bond));

		// Posted again when it is due: the relay has the newer event in place of the older one.
		_now += MakerOfferService.RefreshPeriod;
		await maker.KeepOffersAsync(CancellationToken.None);

		Assert.Equal(2, relay.Stored.Count);
		Assert.All(relay.Stored, stored => Assert.Equal(_now, stored.CreatedAt));

		// One is taken back: what the relay has of its key tells no offer any more, and the book has the other one.
		await maker.CancelAsync(one.Id, CancellationToken.None);

		Assert.Equal(2, relay.Stored.Count);
		book = await takerBook.RefreshAsync(CancellationToken.None);
		Assert.Equal([two.PublicKey], book.Entries.Select(entry => entry.PublicKey));

		Assert.Equal(5, relay.Received.Count);
		Assert.All(relay.Received, e => Assert.True(e.Verify()));
	}

	private MakerOfferService CreateService() =>
		new(Network.RegTest, _publisher, () => _isWalletOpen ? _wallet : null, _eventBus, getBond: null, () => _now);

	/// <summary>An offer of the given coins at the terms of the tests: all that the coins hold, and one coin that counts as matched.</summary>
	private static OfferRequest Request(SmartCoin[] coins, long? amount = null, int matchedInputs = 1) =>
		new(
			[.. coins.Select(coin => coin.Outpoint)],
			MinFeeRate,
			BuyRate,
			SellRate,
			amount ?? coins.Sum(coin => coin.Amount.Satoshi),
			Points: 1m,
			matchedInputs);

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
