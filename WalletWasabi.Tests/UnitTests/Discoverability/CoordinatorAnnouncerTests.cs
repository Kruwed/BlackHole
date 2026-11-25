using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.WabiSabi.Coordinator;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Discoverability;

/// <summary>
/// A coordinator announces itself on Nostr relays. A relay that asks for a fidelity bond filters the announcement
/// like the offer of a maker: the announcement shows the proof of a bond, which a wallet made for the key of the
/// coordinator.
/// </summary>
public sealed class CoordinatorAnnouncerTests : IDisposable
{
	private const long Now = TestRelayEvent.Now;
	private const long Coin = 100_000_000;

	private readonly TestBondNode _node = new();
	private readonly ECPrivKey _key = TestRelayEvent.NewKey();
	private readonly List<ECPrivKey> _otherKeys = [];

	public void Dispose()
	{
		_node.Dispose();
		_key.Dispose();

		foreach (var key in _otherKeys)
		{
			key.Dispose();
		}
	}

	private static DateTimeOffset Clock() => DateTimeOffset.FromUnixTimeSeconds(Now);

	private string PublicKey => TestRelayEvent.GetPublicKey(_key);

	private AnnouncerConfig Config(TestBond? bond = null, params Uri[] relays) =>
		new()
		{
			IsEnabled = true,
			CoordinatorName = "A coordinator",
			CoordinatorUri = "http://coordinator.example/",
			ReadMoreUri = "http://coordinator.example/about",
			AbsoluteMinInputCount = 5,
			Key = _key.ToNIP19(),
			RelayUris = [.. relays.Select(relay => relay.ToString())],
			BondProof = bond?.Prove(_key).ToText() ?? ""
		};

	private NostrRelayServer StartRelay(RelaySenderRules senders, Network? network = null)
	{
		var server = new NostrRelayServer(
			new BlackHoleEventSet(network ?? Network.RegTest),
			now: Clock,
			senders: senders,
			checkBond: _node.CheckAsync,
			getTipHeight: () => _node.TipHeight);

		RelayTestClient.Open(server);
		return server;
	}

	private static CoordinatorAnnouncer Announcer(AnnouncerConfig config, MockRpcClient? rpc = null, long secondsLater = 0) =>
		new(config, Network.RegTest, rpcClient: rpc, now: () => Clock().AddSeconds(secondsLater));

	/// <summary>Announces a coordinator with the given config once, and tells what the relays made of it.</summary>
	/// <param name="secondsLater">How long after the time of the tests the announcement is made. Of two announcements of a key, a relay keeps the newer one.</param>
	private static async Task<IReadOnlyList<RelayAnswer>> AnnounceAsync(AnnouncerConfig config, MockRpcClient? rpc = null, long secondsLater = 0)
	{
		using var announcer = Announcer(config, rpc, secondsLater);
		return await announcer.AnnounceAsync(TestContext.Current.CancellationToken);
	}

	/// <summary>The announcement tells where the coordinator is, for which network it works, and what it is. Without a bond it is what it was before coordinators could show one.</summary>
	[Fact]
	public void AnnouncementTellsTheCoordinator()
	{
		var config = Config();

		Assert.Null(CoordinatorAnnouncer.ReadBond(config, out var problem));
		Assert.Null(problem);

		var announcement = CoordinatorAnnouncer.CreateAnnouncement(config, Network.Main, Clock(), bond: null);

		Assert.Equal(15750, announcement.Kind);
		Assert.Equal("WabiSabi Coinjoin Coordinator", announcement.Content);
		Assert.Equal(Clock(), announcement.CreatedAt);
		Assert.Equal(
			[
				("name", "A coordinator"), ("type", "wabisabi"), ("network", "mainnet"), ("endpoint", "http://coordinator.example/"),
				("absolutemininputcount", "5"), ("readmore", "http://coordinator.example/about")
			],
			announcement.Tags.Select(tag => (tag.TagIdentifier, tag.Data.Single())));

		// The name of the network is the one that the relays of Black Hole know it by.
		Assert.Equal("signet", GetTag(CoordinatorAnnouncer.CreateAnnouncement(config, Bitcoin.Instance.Signet, Clock(), null), "network"));
		Assert.Equal("regtest", GetTag(CoordinatorAnnouncer.CreateAnnouncement(config, Network.RegTest, Clock(), null), "network"));
	}

	/// <summary>
	/// With the proof of a bond in the config of the announcer, the announcement shows the bond in a tag, where a relay
	/// reads it. The proof is the one that a wallet makes for an offer, made for the key of the coordinator.
	/// </summary>
	[Fact]
	public async Task AnnouncementShowsTheBondOfTheConfigAsync()
	{
		var bond = _node.Lock(3 * Coin);
		var config = Config(bond);

		var proof = CoordinatorAnnouncer.ReadBond(config, out var problem);
		Assert.NotNull(proof);
		Assert.Null(problem);
		Assert.Equal(bond.OutPoint, proof.OutPoint);
		Assert.True(proof.IsSignedFor(Convert.FromHexString(PublicKey)));

		var signed = await CoordinatorAnnouncer.CreateAnnouncement(config, Network.RegTest, Clock(), proof).ComputeIdAndSignAsync(_key);

		Assert.Equal(7, signed.Tags.Count);
		Assert.Equal("bond", signed.Tags[^1].TagIdentifier);
		Assert.Equal([bond.OutPoint.Hash.ToString(), "0", bond.UnlockHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), proof.Key, proof.Signature], signed.Tags[^1].Data);

		// A relay of Black Hole reads the announcement, takes it for one of its events, and finds the proof in it.
		using var json = JsonDocument.Parse(JsonSerializer.Serialize(signed));
		Assert.True(RelayEvent.TryParse(json.RootElement, RelayLimits.ForBlackHoleEvents, out var relayEvent, out var parseProblem), parseProblem);
		Assert.True(new BlackHoleEventSet(Network.RegTest).Judge(relayEvent, Now).IsAccepted);
		Assert.True(RelayBondClaim.TryRead(relayEvent, out var read));
		Assert.Equal(proof, read);
	}

	/// <summary>A proof that cannot be read, or that a wallet made for another key, is not shown, and the announcer tells what is wrong with it.</summary>
	[Fact]
	public void ProofThatIsNotForTheCoordinatorIsNotShown()
	{
		var bond = _node.Lock(3 * Coin);
		using var otherKey = TestRelayEvent.NewKey();

		Assert.Null(CoordinatorAnnouncer.ReadBond(Config() with { BondProof = "a proof" }, out var problem));
		Assert.StartsWith("The BondProof of the announcer cannot be read.", problem, StringComparison.Ordinal);

		Assert.Null(CoordinatorAnnouncer.ReadBond(Config() with { BondProof = bond.Prove(otherKey).ToText() }, out problem));
		Assert.Equal($"The BondProof of the announcer is not for the key of this coordinator. The wallet has to make it for {_key.CreateXOnlyPubKey().ToNIP19()}.", problem);

		Assert.Null(CoordinatorAnnouncer.ReadBond(Config() with { BondProof = "  " }, out problem));
		Assert.Null(problem);
	}

	/// <summary>The config file of the coordinator keeps the proof. A file from before coordinators could show a bond has none.</summary>
	[Fact]
	public async Task ConfigFileOfTheCoordinatorKeepsTheProofAsync()
	{
		var filePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "Config.json");
		var bond = _node.Lock(3 * Coin);
		var announcer = Config(bond);

		var config = new WabiSabiConfig(filePath) { AnnouncerConfig = announcer };
		config.ToFile();

		var loaded = WabiSabiConfig.TryLoadFile(filePath);
		Assert.NotNull(loaded);
		Assert.Equal(announcer.BondProof, loaded.AnnouncerConfig.BondProof);
		Assert.NotNull(CoordinatorAnnouncer.ReadBond(loaded.AnnouncerConfig, out _));

		// The file as it was written before, without the proof.
		var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(filePath, TestContext.Current.CancellationToken))!.AsObject();
		Assert.True(json["AnnouncerConfig"]!.AsObject().Remove("BondProof"));
		await File.WriteAllTextAsync(filePath, json.ToJsonString(), TestContext.Current.CancellationToken);

		loaded = WabiSabiConfig.TryLoadFile(filePath);
		Assert.NotNull(loaded);
		Assert.Equal("", loaded.AnnouncerConfig.BondProof);
		Assert.Equal(announcer.Key, loaded.AnnouncerConfig.Key);
	}

	/// <summary>
	/// A relay with the bond filter takes the announcement of a coordinator by the bond that it shows, the way it takes
	/// the offer of a maker: with a bond of the minimum or more it is taken, and otherwise the coordinator is told
	/// what the relay asks for.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayFiltersTheAnnouncementByItsBondAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var relay = StartRelay(new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: []));
		var address = RelayTestClient.GetUri(relay);

		// Without a bond.
		var answers = await AnnounceAsync(Config(null, address));
		Assert.Equal([new RelayAnswer(address, false, "restricted: this relay takes events from keys with a fidelity bond of 1 BTC or more behind them")], answers);
		Assert.Equal(0, relay.EventCount);

		// With a bond that holds less than the relay asks for.
		answers = await AnnounceAsync(Config(_node.Lock(Coin / 2), address));
		Assert.Equal(
			[new RelayAnswer(address, false, "restricted: the fidelity bond holds 0.5 BTC, and this relay takes events from keys with 1 BTC or more behind them")],
			answers);

		// With a bond that the chain does not have.
		answers = await AnnounceAsync(Config(_node.Invent(50 * Coin), address));
		Assert.Equal([new RelayAnswer(address, false, RelaySenders.NoSuchBondMessage)], answers);
		Assert.Equal(0, relay.EventCount);

		// With a bond of 3 BTC.
		answers = await AnnounceAsync(Config(_node.Lock(3 * Coin), address));
		Assert.Equal([new RelayAnswer(address, true, "")], answers);
		Assert.Equal(1, relay.EventCount);
		Assert.Equal(3 * Coin, relay.Store.GetSender(PublicKey)!.Share);

		// What the relay keeps is the announcement, and whoever asks gets it.
		using var client = await RelayTestClient.ConnectAsync(relay, cancellationToken);
		await client.SendAsync("[\"REQ\",\"a\",{\"kinds\":[15750]}]", cancellationToken);
		using var stored = JsonDocument.Parse((await client.ReceiveAsync(cancellationToken))!);
		Assert.Equal(PublicKey, stored.RootElement[2].GetProperty("pubkey").GetString());
		Assert.Equal("wabisabi", stored.RootElement[2].GetProperty("tags").EnumerateArray().Single(tag => tag[0].GetString() == "type")[1].GetString());
	}

	/// <summary>
	/// A coordinator and the offers of a wallet that show the same bond share it evenly, like the offers among
	/// themselves: with a bond of 3 BTC, two offers and the coordinator have 1 BTC behind each.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task CoordinatorSharesTheBondWithTheOffersOfItsWalletAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var bond = _node.Lock(3 * Coin);
		long time = Now;

		await using var relay = new NostrRelayServer(
			new BlackHoleEventSet(Network.RegTest),
			now: () => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time)),
			senders: new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: []),
			checkBond: _node.CheckAsync,
			getTipHeight: () => _node.TipHeight);
		RelayTestClient.Open(relay);

		using var client = await RelayTestClient.ConnectAsync(relay, cancellationToken);
		var makers = new List<string>();

		foreach (var _ in Enumerable.Range(0, 2))
		{
			var key = TestRelayEvent.NewKey();
			_otherKeys.Add(key);
			makers.Add(TestRelayEvent.GetPublicKey(key));

			var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 150_000_000, Points: 3, MatchedInputs: 3, ExtraInputs: 0, Bond: bond.Prove(key));
			var signed = await MakerOfferEvent.Create(offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(time)).ComputeIdAndSignAsync(key);

			Assert.Equal((true, ""), await client.PublishAsync(JsonSerializer.Serialize(signed), cancellationToken));
			Volatile.Write(ref time, time + 60);
		}

		using var announcer = new CoordinatorAnnouncer(
			Config(bond, RelayTestClient.GetUri(relay)),
			Network.RegTest,
			now: () => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time)));

		Assert.True(Assert.Single(await announcer.AnnounceAsync(cancellationToken)).Accepted);
		Assert.All(makers.Append(PublicKey), key => Assert.Equal(Coin, relay.Store.GetSender(key)!.Share));
		Assert.Equal(3, relay.EventCount);

		// The coordinator announces itself again a quarter of an hour later: the relay knows its key, and asks the node about nothing.
		var lookups = _node.Lookups;
		Volatile.Write(ref time, time + 15 * 60);
		Assert.True(Assert.Single(await announcer.AnnounceAsync(cancellationToken)).Accepted);
		Assert.Equal(lookups, _node.Lookups);
		Assert.Equal(3, relay.EventCount);
	}

	/// <summary>A relay without the filter takes the announcement with or without a bond, and one for another network takes neither.</summary>
	[Fact(Timeout = 120000)]
	public async Task RelayWithoutTheFilterTakesTheAnnouncementAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;

		await using var open = StartRelay(RelaySenderRules.Anyone);
		await using var otherNetwork = StartRelay(RelaySenderRules.Anyone, Network.Main);

		var answers = await AnnounceAsync(Config(null, RelayTestClient.GetUri(open), RelayTestClient.GetUri(otherNetwork)));

		Assert.Equal(
			[
				new RelayAnswer(RelayTestClient.GetUri(open), true, ""),
				new RelayAnswer(RelayTestClient.GetUri(otherNetwork), false, "blocked: this relay takes events for mainnet only")
			],
			answers);

		// With a bond, the relay knows what stands behind the announcement when its room is used up.
		Assert.True((await AnnounceAsync(Config(_node.Lock(2 * Coin), RelayTestClient.GetUri(open)), secondsLater: 60)).Single().Accepted);
		Assert.Equal(2 * Coin, open.Store.GetSender(PublicKey)!.Share);
	}

	/// <summary>
	/// A relay at an onion address is reached through Tor. A coordinator that runs no Tor says so about that relay,
	/// sends nothing to it, and announces itself on the others.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayAtAnOnionAddressTakesTorAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var onion = new Uri("ws://blackholerelayexampleblackholerelayexampleblackholerelay.onion/");

		await using var relay = StartRelay(RelaySenderRules.Anyone);

		var answers = await AnnounceAsync(Config(null, onion, RelayTestClient.GetUri(relay)));

		Assert.Equal(
			new[] { new RelayAnswer(RelayTestClient.GetUri(relay), true, ""), new RelayAnswer(onion, false, CoordinatorAnnouncer.NoTorMessage) },
			answers);
	}

	/// <summary>
	/// The announcer asks the node of the coordinator about the bond, to tell in the log whether the chain has it. A
	/// node that is away is not in the way of the announcement.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task NodeOfTheCoordinatorIsAskedAboutTheBondAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var bond = _node.Lock(3 * Coin);
		var rpc = new MockRpcClient();
		var questions = 0;
		var isReachable = true;

		rpc.OnGetBlockCountAsync = () =>
		{
			questions++;
			return isReachable ? Task.FromResult((int)TestBondNode.Tip) : throw new HttpRequestException("Connection refused");
		};
		rpc.OnGetTxOutAsync = (txid, index, _) => new OutPoint(txid, index) == bond.OutPoint
			? new GetTxOutResponse { Confirmations = 6, TxOut = new TxOut(Money.Satoshis(bond.Amount), BondScript.GetScriptPubKey(BondScript.CreateLeaf(bond.Key.PubKey, bond.UnlockHeight))) }
			: null;

		await using var relay = StartRelay(new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: []));
		using var announcer = Announcer(Config(bond, RelayTestClient.GetUri(relay)), rpc);

		Assert.True((await announcer.AnnounceAsync(cancellationToken)).Single().Accepted);
		Assert.Equal(1, questions);

		// The second announcement of the same second is the event that the relay has already.
		isReachable = false;
		Assert.Equal("duplicate: already have this event", (await announcer.AnnounceAsync(cancellationToken)).Single().Message);
		Assert.Equal(2, questions);

		// Without a bond in the config there is nothing to ask the node about. The relay knows the key of the
		// coordinator by now, and takes its announcement without the proof.
		Assert.True((await AnnounceAsync(Config(null, RelayTestClient.GetUri(relay)), rpc, secondsLater: 60)).Single().Accepted);
		Assert.Equal(2, questions);
	}

	private static string GetTag(NostrEvent nostrEvent, string name) =>
		nostrEvent.Tags.Single(tag => tag.TagIdentifier == name).Data.Single();
}
