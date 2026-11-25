using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The relay with the filters that its user set, as a client meets it: whose events it takes, and whose events make
/// way when it is full.
/// </summary>
public sealed class NostrRelayBondFilterTests : IDisposable
{
	private const long Now = TestRelayEvent.Now;
	private const long Coin = 100_000_000;
	private const string Full = "error: the relay is full, and the events of keys with more of a fidelity bond behind them come first";
	private const string NoSuchBond = "restricted: the chain has no fidelity bond that is locked and is the one that the event shows";

	private readonly TestBondNode _node = new();
	private readonly List<ECPrivKey> _keys = [];

	public void Dispose()
	{
		_node.Dispose();

		foreach (var key in _keys)
		{
			key.Dispose();
		}
	}

	private static DateTimeOffset Clock() => DateTimeOffset.FromUnixTimeSeconds(Now);

	private ECPrivKey NewKey()
	{
		var key = TestRelayEvent.NewKey();
		_keys.Add(key);
		return key;
	}

	private NostrRelayServer Start(RelaySenderRules senders, RelayEventSet? events = null, RelayLimits? limits = null, string? folder = null, Func<DateTimeOffset>? now = null)
	{
		var server = new NostrRelayServer(
			events ?? new BlackHoleEventSet(Network.RegTest),
			folder,
			now: now ?? Clock,
			limits: limits,
			senders: senders,
			checkBond: _node.CheckAsync,
			getTipHeight: () => _node.TipHeight);

		RelayTestClient.Open(server);
		return server;
	}

	/// <summary>The offer that the application posts for a maker, with the proof of the bond of the wallet in it if there is one.</summary>
	private static async Task<string> OfferAsync(ECPrivKey key, TestBond? bond = null, long createdAt = Now)
	{
		var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 150_000_000, Points: 3, MatchedInputs: 3, ExtraInputs: 0, Bond: bond?.Prove(key));
		var signed = await MakerOfferEvent.Create(offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(createdAt)).ComputeIdAndSignAsync(key);

		return JsonSerializer.Serialize(signed);
	}

	private static async Task<string> WithdrawalAsync(ECPrivKey key, long createdAt = Now + 1) =>
		JsonSerializer.Serialize(await MakerOfferEvent.CreateWithdrawal(Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(createdAt)).ComputeIdAndSignAsync(key));

	private static string Announcement(ECPrivKey key) =>
		TestRelayEvent.CreateJson(key, 15750, "A coordinator", [["type", "wabisabi"], ["network", "regtest"], ["endpoint", "http://coordinator.example/"]]);

	private static string GetId(string eventJson)
	{
		using var json = JsonDocument.Parse(eventJson);
		return json.RootElement.GetProperty("id").GetString()!;
	}

	/// <summary>
	/// With the bond filter, the relay takes the offers of makers that prove a bond of the minimum or more, and what
	/// the keys of the whitelist send. Everybody else is told what the relay asks for.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayTakesEventsOfKeysWithABondAndOfTheWhitelistAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var maker = NewKey();
		var smallMaker = NewKey();
		var stranger = NewKey();
		var coordinator = NewKey();
		var otherCoordinator = NewKey();

		await using var server = Start(new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: [TestRelayEvent.GetPublicKey(coordinator)]));
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		const string NeedsACoin = "restricted: this relay takes events from keys with a fidelity bond of 1 BTC or more behind them";

		// An offer without a bond.
		Assert.Equal((false, NeedsACoin), await client.PublishAsync(await OfferAsync(stranger), cancellationToken));

		// An offer with a bond of 3 BTC, and the event that takes it back, which shows no bond.
		var offer = await OfferAsync(maker, _node.Lock(3 * Coin));
		var withdrawal = await WithdrawalAsync(maker);

		Assert.Equal((true, ""), await client.PublishAsync(offer, cancellationToken));
		Assert.Equal([GetId(offer)], await client.QueryAsync("{}", cancellationToken));
		Assert.Equal((true, ""), await client.PublishAsync(withdrawal, cancellationToken));

		// An offer with a bond below the minimum, and one with a bond that the chain does not have.
		Assert.Equal(
			(false, "restricted: the fidelity bond holds 0.5 BTC, and this relay takes events from keys with 1 BTC or more behind them"),
			await client.PublishAsync(await OfferAsync(smallMaker, _node.Lock(Coin / 2)), cancellationToken));
		Assert.Equal((false, NoSuchBond), await client.PublishAsync(await OfferAsync(stranger, _node.Invent(50 * Coin)), cancellationToken));

		// The announcement of a coordinator shows no bond: the coordinator of the whitelist is taken, another is not.
		var announcement = Announcement(coordinator);

		Assert.Equal((true, ""), await client.PublishAsync(announcement, cancellationToken));
		Assert.Equal((false, NeedsACoin), await client.PublishAsync(Announcement(otherCoordinator), cancellationToken));

		Assert.Equal(new[] { GetId(withdrawal), GetId(announcement) }.Order(), (await client.QueryAsync("{}", cancellationToken)).Order());
		Assert.Equal(2, server.EventCount);

		// The relay tells what it asks for when a client asks it about itself.
		var information = await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {server.OnionHost}\r\nAccept: application/nostr+json\r\n\r\n", cancellationToken);
		Assert.Contains(
			"It takes them from keys that have shown a fidelity bond of 1 BTC or more, and from one key that it has on a list. It keeps an event for 24 hours.",
			information,
			StringComparison.Ordinal);
	}

	/// <summary>
	/// A wallet with a bond of 3 BTC and three offers has 1 BTC behind each, so a relay that asks for 1 BTC takes all
	/// three. With a fourth offer the bond stands behind the three that showed it last.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task OffersOfOneWalletShareItsBondAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var bond = _node.Lock(3 * Coin);
		var keys = Enumerable.Range(0, 4).Select(_ => NewKey()).ToArray();
		long time = Now;

		await using var server = Start(
			new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: []),
			now: () => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time)));
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var offers = new List<string>();

		foreach (var key in keys[..3])
		{
			offers.Add(await OfferAsync(key, bond, createdAt: time));
			Assert.Equal((true, ""), await client.PublishAsync(offers[^1], cancellationToken));
			Volatile.Write(ref time, time + 60);
		}

		Assert.Equal(offers.Select(GetId).Order(), (await client.QueryAsync("{}", cancellationToken)).Order());
		Assert.All(keys[..3], key => Assert.Equal(Coin, server.Store.GetSender(TestRelayEvent.GetPublicKey(key))!.Share));

		// The fourth offer takes the place of the first: the bond holds three shares of 1 BTC, and no more.
		offers.Add(await OfferAsync(keys[3], bond, createdAt: time));
		Assert.Equal((true, ""), await client.PublishAsync(offers[^1], cancellationToken));

		Assert.Equal(offers[1..].Select(GetId).Order(), (await client.QueryAsync("{}", cancellationToken)).Order());
		Assert.Null(server.Store.GetSender(TestRelayEvent.GetPublicKey(keys[0])));

		// A relay that asks for 1.5 BTC takes two offers of this wallet at a time.
		await using var strict = Start(new RelaySenderRules(IsBondRequired: true, MinBond: Coin * 3 / 2, Whitelist: []));
		using var strictClient = await RelayTestClient.ConnectAsync(strict, cancellationToken);

		foreach (var offer in offers[..3])
		{
			Assert.Equal((true, ""), await strictClient.PublishAsync(offer, cancellationToken));
		}

		Assert.Equal(2, (await strictClient.QueryAsync("{}", cancellationToken)).Length);
	}

	/// <summary>
	/// When the room for events is used up, the events of the keys with the smallest bonds make way first, then the
	/// larger ones, and the events of the whitelist last. An event with less behind it than everything that is kept
	/// is not taken.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task EventsOfTheSmallestBondsMakeWayFirstAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var plain = NewKey();
		var small = NewKey();
		var large = NewKey();
		var middle = NewKey();
		var listed = NewKey();
		var late = NewKey();
		var better = NewKey();
		var best = NewKey();

		await using var server = Start(
			new RelaySenderRules(IsBondRequired: false, MinBond: 0, Whitelist: [TestRelayEvent.GetPublicKey(listed)]),
			limits: RelayLimits.ForBlackHoleEvents with { MaxStoredEvents = 3, VisitorBondLookupBurst = 20 });
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var ofPlain = await OfferAsync(plain);
		var ofSmall = await OfferAsync(small, _node.Lock(Coin / 100));
		var ofLarge = await OfferAsync(large, _node.Lock(5 * Coin));
		var ofMiddle = await OfferAsync(middle, _node.Lock(2 * Coin));
		var ofListed = await OfferAsync(listed);
		var ofBetter = await OfferAsync(better, _node.Lock(3 * Coin));
		var ofBest = await OfferAsync(best, _node.Lock(9 * Coin));

		async Task<string[]> KeptAsync() =>
			[.. (await client.QueryAsync("{}", cancellationToken)).Order()];

		string[] Ids(params string[] events) => [.. events.Select(GetId).Order()];

		// The relay takes events from anybody, and has room for three.
		Assert.Equal((true, ""), await client.PublishAsync(ofLarge, cancellationToken));
		Assert.Equal((true, ""), await client.PublishAsync(ofSmall, cancellationToken));
		Assert.Equal((true, ""), await client.PublishAsync(ofPlain, cancellationToken));

		// The newest of the three is the one without a bond, and it is the one that makes way.
		Assert.Equal((true, ""), await client.PublishAsync(ofMiddle, cancellationToken));
		Assert.Equal(Ids(ofLarge, ofSmall, ofMiddle), await KeptAsync());

		// For a key of the whitelist the smallest bond makes way.
		Assert.Equal((true, ""), await client.PublishAsync(ofListed, cancellationToken));
		Assert.Equal(Ids(ofLarge, ofMiddle, ofListed), await KeptAsync());

		// Nothing makes way for an offer without a bond, or with a smaller bond than everything that is kept.
		Assert.Equal((false, Full), await client.PublishAsync(await OfferAsync(late), cancellationToken));
		Assert.Equal((false, Full), await client.PublishAsync(await OfferAsync(late, _node.Lock(Coin)), cancellationToken));
		Assert.Equal(Ids(ofLarge, ofMiddle, ofListed), await KeptAsync());

		// Larger bonds push the smaller ones out, lowest first. The offer of the whitelist stays.
		Assert.Equal((true, ""), await client.PublishAsync(ofBetter, cancellationToken));
		Assert.Equal(Ids(ofLarge, ofBetter, ofListed), await KeptAsync());

		Assert.Equal((true, ""), await client.PublishAsync(ofBest, cancellationToken));
		Assert.Equal(Ids(ofLarge, ofBest, ofListed), await KeptAsync());

		// An offer with a bond that the chain does not have is one without a bond. Its key is told about the bond.
		Assert.Equal((false, NoSuchBond), await client.PublishAsync(await OfferAsync(NewKey(), _node.Invent(50 * Coin)), cancellationToken));
		Assert.Equal(Ids(ofLarge, ofBest, ofListed), await KeptAsync());
	}

	/// <summary>
	/// A relay for a list of kinds of the user's own takes the bond in a tag. It keeps its book of bonds in the file
	/// with the events, so a key need not show its bond again after a restart, and it brings what it kept in line
	/// with the filters that it is started with.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayOfAListOfKindsTakesTheBondInATagAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var folder = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay");
		var author = NewKey();
		var stranger = NewKey();
		var bond = _node.Lock(Coin / 2);

		Assert.True(KindList.TryParse("1", out var kinds, out _));
		var events = new CustomEventSet(kinds);
		var rules = new RelaySenderRules(IsBondRequired: true, MinBond: Coin / 10, Whitelist: []);

		var first = TestRelayEvent.CreateJson(author, 1, "a note with the proof of a bond", [bond.Tag(author)]);
		var second = TestRelayEvent.CreateJson(author, 1, "a note without it");
		var third = TestRelayEvent.CreateJson(author, 1, "a note after the restart");

		await using (var server = Start(rules, events, folder: folder))
		{
			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Equal(
				(false, "restricted: this relay takes events from keys with a fidelity bond of 0.1 BTC or more behind them"),
				await client.PublishAsync(TestRelayEvent.CreateJson(stranger, 1, "a note of a stranger"), cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(first, cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(second, cancellationToken));

			var information = await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {server.OnionHost}\r\nAccept: application/nostr+json\r\n\r\n", cancellationToken);
			Assert.Contains("It takes them from keys that have shown a fidelity bond of 0.1 BTC or more. It keeps an event for 24 hours.", information, StringComparison.Ordinal);
		}

		Assert.Equal(1, _node.Lookups);

		// After a restart the relay knows the key and its bond, and asks the node about nothing.
		await using (var server = Start(rules, events, folder: folder))
		{
			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Equal((true, ""), await client.PublishAsync(third, cancellationToken));
			Assert.Equal(new[] { GetId(first), GetId(second), GetId(third) }.Order(), (await client.QueryAsync("{}", cancellationToken)).Order());
		}

		Assert.Equal(1, _node.Lookups);

		// Started with a minimum above the bond, the relay drops what the key sent, and takes no more of it.
		await using (var server = Start(rules with { MinBond = Coin }, events, folder: folder))
		{
			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Empty(await client.QueryAsync("{}", cancellationToken));
			Assert.Equal(
				(false, "restricted: this relay takes events from keys with a fidelity bond of 1 BTC or more behind them"),
				await client.PublishAsync(TestRelayEvent.CreateJson(author, 1, "a note for a relay that asks for more"), cancellationToken));
		}

		// With the key on the whitelist, the relay takes its events again.
		await using (var server = Start(rules with { MinBond = Coin, Whitelist = [TestRelayEvent.GetPublicKey(author)] }, events, folder: folder))
		{
			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Equal((true, ""), await client.PublishAsync(third, cancellationToken));
		}
	}

	/// <summary>Whoever shows bonds that are none is slowed down: the node is asked about so many bonds for one visitor, and no more.</summary>
	[Fact(Timeout = 120000)]
	public async Task VisitorCannotMakeTheNodeLookUpBondAfterBondAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var honest = NewKey();

		await using var server = Start(
			new RelaySenderRules(IsBondRequired: true, MinBond: Coin, Whitelist: []),
			limits: RelayLimits.ForBlackHoleEvents with { VisitorBondLookupBurst = 2, VisitorBondLookupsPerSecond = 0 });
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);
		using var otherClient = await RelayTestClient.ConnectAsync(server, cancellationToken);

		Assert.Equal((false, RelaySenders.NoSuchBondMessage), await client.PublishAsync(await OfferAsync(NewKey(), _node.Invent(Coin)), cancellationToken));
		Assert.Equal((false, RelaySenders.NoSuchBondMessage), await client.PublishAsync(await OfferAsync(NewKey(), _node.Invent(Coin)), cancellationToken));
		Assert.Equal((false, RelaySenders.TooManyLookupsMessage), await client.PublishAsync(await OfferAsync(NewKey(), _node.Invent(Coin)), cancellationToken));
		Assert.Equal(2, _node.Lookups);

		// Another visitor is served.
		Assert.Equal((true, ""), await otherClient.PublishAsync(await OfferAsync(honest, _node.Lock(2 * Coin)), cancellationToken));
		Assert.Equal(3, _node.Lookups);
	}
}
