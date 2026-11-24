using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.Services;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The order book is what the Nostr relays have of the offers of other makers: the newest event of every key that
/// tells an offer for this network and is not too old, with the fidelity bond that the node could find behind it.
/// </summary>
public class OrderBookServiceTests
{
	private const int Tip = 800_000;
	private static readonly Network Network = Network.RegTest;
	private static readonly MakerOffer Offer = new(MinFeeRate: 2m, BuyRate: 100m, SellRate: 150m, Liquidity: 50_000_000, Points: 2m, MatchedInputs: 2, ExtraInputs: 0);

	private readonly FakeReader _reader = new();
	private readonly MockRpcClient _rpc = new();
	private readonly Dictionary<OutPoint, GetTxOutResponse> _outputs = [];
	private readonly EventBus _eventBus = new();
	private readonly List<OrderBookSnapshot> _changes = [];
	private readonly HashSet<string> _ownKeys = [];
	private readonly OrderBookService _service;
	private DateTimeOffset _now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

	public OrderBookServiceTests()
	{
		_rpc.OnGetBlockCountAsync = () => Task.FromResult(Tip);
		_rpc.OnGetTxOutAsync = (txid, index, _) => _outputs.GetValueOrDefault(new OutPoint(txid, index));
		_eventBus.Subscribe<OrderBookChanged>(e => _changes.Add(e.Book));
		_service = new OrderBookService(Network, _reader, new BondProofVerifier(_rpc), () => _ownKeys, _eventBus, () => _now);
	}

	[Fact]
	public void BookIsEmptyUntilTheRelaysWereAsked()
	{
		Assert.Same(OrderBookSnapshot.Empty, _service.Current);
		Assert.Empty(_service.Current.Entries);
		Assert.Null(_service.Current.ReadAt);
		Assert.Null(_service.Current.Problem);
		Assert.Equal(_reader.Relays, _service.Relays);
	}

	[Fact]
	public async Task OffersThatTheRelaysHaveAreTheBookAsync()
	{
		var first = await PostAsync(Offer, relays: [0, 1]);
		var second = await PostAsync(Offer with { MinFeeRate = 3m }, relays: [0]);
		var third = await PostAsync(Offer with { MinFeeRate = 4m }, relays: [1]);

		var book = await _service.RefreshAsync(CancellationToken.None);

		// Asked for the offers of the last quarter of an hour.
		Assert.Equal([(MakerOfferEvent.Kind, _now - MakerOfferEvent.MaxAge)], _reader.Asked);

		// An offer that two relays have is one offer. The book is in the order of the keys, whatever the relays sent first.
		var expected = new[] { first, second, third }.OrderBy(key => key, System.StringComparer.Ordinal).ToArray();
		Assert.Equal(expected, book.Entries.Select(entry => entry.PublicKey));
		Assert.Equal(3m, book.Entries.Single(entry => entry.PublicKey == second).Offer.MinFeeRate);
		Assert.All(book.Entries, entry => Assert.Equal(_now, entry.CreatedAt));
		Assert.All(book.Entries, entry => Assert.Equal(0, entry.Bond));

		Assert.Equal(_now, book.ReadAt);
		Assert.True(book.IsAnswered);
		Assert.Null(book.Problem);
		Assert.Null(book.BondProblem);
		Assert.Same(book, _service.Current);
		Assert.Equal([book], _changes);
	}

	[Fact]
	public async Task NewestEventOfAKeyIsItsOfferAsync()
	{
		using var key = CreateKey();
		await PostAsync(Offer with { MinFeeRate = 5m }, key, _now.AddMinutes(-5), relays: [0]);
		await PostAsync(Offer with { MinFeeRate = 6m }, key, _now.AddMinutes(-1), relays: [1]);
		await PostAsync(Offer with { MinFeeRate = 7m }, key, _now.AddMinutes(-3), relays: [0, 1]);

		var book = await _service.RefreshAsync(CancellationToken.None);

		var entry = Assert.Single(book.Entries);
		Assert.Equal(6m, entry.Offer.MinFeeRate);
		Assert.Equal(_now.AddMinutes(-1), entry.CreatedAt);
	}

	/// <summary>A relay that did not hear that an offer was taken back still has the offer. The newer word counts.</summary>
	[Fact]
	public async Task OfferThatWasTakenBackIsNotInTheBookAsync()
	{
		using var key = CreateKey();
		await PostAsync(Offer, key, _now.AddMinutes(-5), relays: [0, 1]);
		_reader.Events[1].Add(await MakerOfferEvent.CreateWithdrawal(Network, _now.AddMinutes(-1)).ComputeIdAndSignAsync(key));
		var other = await PostAsync(Offer);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal([other], book.Entries.Select(entry => entry.PublicKey));
	}

	/// <summary>The offers of this wallet are known without the relays, and are not read from them as the offers of somebody else.</summary>
	[Fact]
	public async Task OffersOfThisWalletAreNotReadAsync()
	{
		var own = await PostAsync(Offer);
		var other = await PostAsync(Offer);
		_ownKeys.Add(own);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal([other], book.Entries.Select(entry => entry.PublicKey));
	}

	[Fact]
	public async Task EventsOfAnotherNetworkOrTypeAreNotOffersAsync()
	{
		using var key = CreateKey();
		using var coordinatorKey = CreateKey();

		_reader.Events[0].Add(await MakerOfferEvent.Create(Offer, Network.Main, _now).ComputeIdAndSignAsync(key));

		var coordinator = MakerOfferEvent.Create(Offer, Network, _now);
		coordinator.Tags[0].Data = ["wabisabi"];
		_reader.Events[0].Add(await coordinator.ComputeIdAndSignAsync(coordinatorKey));

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Empty(book.Entries);
		Assert.Null(book.Problem);
	}

	[Theory]
	[InlineData(-14, true)]
	[InlineData(-16, false)]
	[InlineData(4, true)]
	[InlineData(6, false)]
	public async Task OfferCountsForAQuarterOfAnHourAsync(int minutesFromNow, bool isInTheBook)
	{
		await PostAsync(Offer, createdAt: _now.AddMinutes(minutesFromNow));

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal(isInTheBook, book.Entries.Length == 1);
	}

	/// <summary>Anybody can send anything to a relay. What its key did not sign is not the offer of that key.</summary>
	[Fact]
	public async Task EventThatItsKeyDidNotSignIsNotReadAsync()
	{
		using var key = CreateKey();
		using var otherKey = CreateKey();

		// The terms were changed after the signature.
		var changed = await MakerOfferEvent.Create(Offer, Network, _now).ComputeIdAndSignAsync(key);
		changed.Content = changed.Content!.Replace("\"buy\":100", "\"buy\":1");
		_reader.Events[0].Add(changed);

		// Signed, and then given the key of somebody else.
		var stolen = await MakerOfferEvent.Create(Offer, Network, _now).ComputeIdAndSignAsync(key);
		stolen.PublicKey = otherKey.CreateXOnlyPubKey().ToHex();
		_reader.Events[0].Add(stolen);

		// Not signed at all.
		var unsigned = MakerOfferEvent.Create(Offer, Network, _now);
		unsigned.PublicKey = otherKey.CreateXOnlyPubKey().ToHex();
		_reader.Events[0].Add(unsigned);

		var garbled = await MakerOfferEvent.Create(Offer, Network, _now).ComputeIdAndSignAsync(otherKey);
		garbled.Signature = new string('z', 128);
		_reader.Events[1].Add(garbled);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Empty(book.Entries);
	}

	[Fact]
	public async Task EventWithoutAnOfferInItIsNoOfferAsync()
	{
		using var key = CreateKey();
		var nonsense = MakerOfferEvent.Create(Offer, Network, _now);
		nonsense.Content = """{"minFeeRate":"cheap"}""";
		_reader.Events[0].Add(await nonsense.ComputeIdAndSignAsync(key));

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Empty(book.Entries);
	}

	/// <summary>
	/// A maker with a bond that the node finds is listed with what the bond holds. A maker whose proof names nothing
	/// that the node has is listed too, without a bond.
	/// </summary>
	[Fact]
	public async Task BondCountsWhenTheNodeFindsItAsync()
	{
		using var bonded = CreateKey();
		using var boaster = CreateKey();
		var proven = CreateBond(bonded, 30_000_000, isOnTheChain: true);
		var unproven = CreateBond(boaster, 90_000_000, isOnTheChain: false);

		var bondedKey = await PostAsync(Offer with { Bond = proven }, bonded);
		var boasterKey = await PostAsync(Offer with { Bond = unproven }, boaster);
		var plainKey = await PostAsync(Offer);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal(3, book.Entries.Length);
		Assert.Equal(30_000_000, book.Entries.Single(entry => entry.PublicKey == bondedKey).Bond);
		Assert.Equal(0, book.Entries.Single(entry => entry.PublicKey == boasterKey).Bond);
		Assert.Equal(0, book.Entries.Single(entry => entry.PublicKey == plainKey).Bond);
		Assert.Null(book.BondProblem);
	}

	/// <summary>The proof of one maker is of no use in the offer of another.</summary>
	[Fact]
	public async Task BondOfAnotherMakerDoesNotCountAsync()
	{
		using var bonded = CreateKey();
		using var thief = CreateKey();
		var proof = CreateBond(bonded, 30_000_000, isOnTheChain: true);

		var bondedKey = await PostAsync(Offer with { Bond = proof }, bonded);
		var thiefKey = await PostAsync(Offer with { Bond = proof }, thief);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal(30_000_000, book.Entries.Single(entry => entry.PublicKey == bondedKey).Bond);
		Assert.Equal(0, book.Entries.Single(entry => entry.PublicKey == thiefKey).Bond);
	}

	[Fact]
	public async Task WithoutTheNodeNoBondCountsAndTheBookSaysSoAsync()
	{
		using var bonded = CreateKey();
		var bondedKey = await PostAsync(Offer with { Bond = CreateBond(bonded, 30_000_000, isOnTheChain: true) }, bonded);
		_rpc.OnGetBlockCountAsync = () => Task.FromException<int>(new System.Net.Http.HttpRequestException("Connection refused"));

		var book = await _service.RefreshAsync(CancellationToken.None);

		var entry = Assert.Single(book.Entries);
		Assert.Equal(bondedKey, entry.PublicKey);
		Assert.Equal(0, entry.Bond);
		Assert.Equal("The Bitcoin node could not be asked about the fidelity bonds: Connection refused", book.BondProblem);
		Assert.Null(book.Problem);

		// When the node is back the bond counts again.
		_rpc.OnGetBlockCountAsync = () => Task.FromResult(Tip);
		book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal(30_000_000, Assert.Single(book.Entries).Bond);
		Assert.Null(book.BondProblem);
	}

	[Fact]
	public async Task BookWithoutBondsDoesNotAskTheNodeAsync()
	{
		_rpc.OnGetBlockCountAsync = () => Task.FromException<int>(new System.InvalidOperationException("The node was asked."));
		await PostAsync(Offer);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Single(book.Entries);
		Assert.Null(book.BondProblem);
	}

	[Fact]
	public async Task OneRelayThatAnswersIsEnoughAsync()
	{
		var key = await PostAsync(Offer, relays: [1]);
		_reader.Problems[0] = "Connection refused";

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal([key], book.Entries.Select(entry => entry.PublicKey));
		Assert.True(book.IsAnswered);
		Assert.Null(book.Problem);
		Assert.Equal(["Connection refused", null], book.Relays.Select(relay => relay.Problem));
	}

	/// <summary>
	/// Relays that cannot be reached tell nothing, and that is not the same as an empty book: what was read before is
	/// what is known, for as long as it counts.
	/// </summary>
	[Fact]
	public async Task BookStaysWhenNoRelayAnswersUntilItIsTooOldAsync()
	{
		var key = await PostAsync(Offer);
		await _service.RefreshAsync(CancellationToken.None);

		_reader.Problems[0] = "Connection refused";
		_reader.Problems[1] = "The relay did not answer in time.";
		_now += System.TimeSpan.FromMinutes(10);

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Equal([key], book.Entries.Select(entry => entry.PublicKey));
		Assert.False(book.IsAnswered);
		Assert.Equal("No Nostr relay answered: one.example: Connection refused; two.example: The relay did not answer in time.", book.Problem);
		Assert.Equal(_now, book.ReadAt);

		_now += System.TimeSpan.FromMinutes(6);
		book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Empty(book.Entries);
		Assert.NotNull(book.Problem);
		Assert.Equal(3, _changes.Count);
	}

	[Fact]
	public async Task WithoutRelaysThereIsNobodyToAskAsync()
	{
		_reader.Relays = [];

		var book = await _service.RefreshAsync(CancellationToken.None);

		Assert.Empty(book.Entries);
		Assert.Equal("There is no Nostr relay to ask.", book.Problem);
	}

	/// <summary>Posts an offer to the relays of the test as a maker would, and tells the key that signed it.</summary>
	private async Task<string> PostAsync(MakerOffer offer, ECPrivKey? key = null, DateTimeOffset? createdAt = null, int[]? relays = null)
	{
		using var ownKey = key is null ? CreateKey() : null;
		var signedEvent = await MakerOfferEvent.Create(offer, Network, createdAt ?? _now).ComputeIdAndSignAsync(key ?? ownKey!);

		foreach (var relay in relays ?? [0])
		{
			_reader.Events[relay].Add(signedEvent);
		}

		return signedEvent.PublicKey;
	}

	/// <summary>A proof of a bond for the offer that the given key signs, with or without the output that it tells of.</summary>
	private BondProof CreateBond(ECPrivKey offerKey, long amount, bool isOnTheChain)
	{
		using var bondKey = new Key();
		var outPoint = new OutPoint(RandomUtils.GetUInt256(), 0);
		var proof = BondProof.Create(bondKey, outPoint, Tip + 1_000, offerKey.CreateXOnlyPubKey().ToBytes());

		if (isOnTheChain)
		{
			_outputs[outPoint] = new GetTxOutResponse { Confirmations = 3, TxOut = new TxOut(Money.Satoshis(amount), proof.GetScriptPubKey()) };
		}

		return proof;
	}

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}

	/// <summary>Two relays that send what the test gave them, or do not answer.</summary>
	private sealed class FakeReader : INostrReader
	{
		public IReadOnlyList<Uri> Relays { get; set; } = [new Uri("wss://one.example"), new Uri("wss://two.example")];

		public List<NostrEvent>[] Events { get; } = [[], []];

		public string?[] Problems { get; } = [null, null];

		public List<(int Kind, DateTimeOffset Since)> Asked { get; } = [];

		public Task<IReadOnlyList<RelayEvents>> ReadAsync(int kind, DateTimeOffset since, CancellationToken cancellationToken)
		{
			Asked.Add((kind, since));

			return Task.FromResult<IReadOnlyList<RelayEvents>>(
				[.. Relays.Select((relay, i) => new RelayEvents(relay, Problems[i] is null ? [.. Events[i]] : [], Problems[i]))]);
		}
	}
}
