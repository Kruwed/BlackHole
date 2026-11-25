using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Discoverability;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The Nostr relay of the application, as a client meets it: what it answers to, what it takes and hands out, and
/// what it lets nobody do.
/// </summary>
public class NostrRelayServerTests
{
	private const long Now = TestRelayEvent.Now;

	private static readonly string[][] OfferTags = [["type", "BlackHoleJoinMarket"], ["network", "regtest"]];

	private static DateTimeOffset Clock() => DateTimeOffset.FromUnixTimeSeconds(Now);

	/// <summary>A relay for the events of Black Hole on regtest, which answers to the address that tests reach it at.</summary>
	private static NostrRelayServer StartPreset(RelayLimits? limits = null, Func<DateTimeOffset>? now = null)
	{
		var server = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest), now: now ?? Clock, limits: limits);
		RelayTestClient.Open(server);
		return server;
	}

	private static string Offer(ECPrivKey key, string content = "{}", long createdAt = Now) =>
		TestRelayEvent.CreateJson(key, 15750, content, OfferTags, createdAt);

	private static string GetId(string eventJson)
	{
		using var json = JsonDocument.Parse(eventJson);
		return json.RootElement.GetProperty("id").GetString()!;
	}

	/// <summary>The relay listens on the address of this computer that only this computer reaches.</summary>
	[Fact]
	public async Task RelayListensOnThisComputerOnlyAsync()
	{
		await using var server = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest));

		Assert.Equal(IPAddress.Loopback, server.EndPoint.Address);
		Assert.NotEqual(0, server.EndPoint.Port);
		Assert.Null(server.OnionHost);
	}

	/// <summary>
	/// The relay answers to its onion address and to nothing else: not before it has one, and not to whoever asks for
	/// another host, like a page in a browser of this computer that tries the ports of the computer.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayAnswersToItsOnionAddressOnlyAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var server = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest));
		var port = server.EndPoint.Port;
		var onionHost = new string('a', 55) + "d.onion";

		string Upgrade(string host) =>
			$"GET / HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";

		// Before there is an onion address the relay answers to nobody.
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade($"127.0.0.1:{port}"), cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade(onionHost), cancellationToken), StringComparison.Ordinal);

		server.OnionHost = onionHost;

		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade($"127.0.0.1:{port}"), cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade($"localhost:{port}"), cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade("attacker.example"), cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, "GET / HTTP/1.1\r\nAccept: application/nostr+json\r\n\r\n", cancellationToken), StringComparison.Ordinal);

		// Tor hands a visitor over with the request as the visitor made it: for the onion address.
		Assert.StartsWith(
			"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n",
			await ReadHeadAsync(server, Upgrade(onionHost), cancellationToken),
			StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 101 ", await ReadHeadAsync(server, Upgrade(onionHost.ToUpperInvariant() + ":80"), cancellationToken), StringComparison.Ordinal);

		// Without an address again, the relay is silent again.
		server.OnionHost = null;
		Assert.StartsWith("HTTP/1.1 421 ", await RelayTestClient.RequestAsync(server, Upgrade(onionHost), cancellationToken), StringComparison.Ordinal);

		static async Task<string> ReadHeadAsync(NostrRelayServer server, string request, CancellationToken cancellationToken)
		{
			using var client = new TcpClient();
			await client.ConnectAsync(server.EndPoint, cancellationToken);

			var stream = client.GetStream();
			await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), cancellationToken);

			var buffer = new byte[1024];
			var length = 0;

			while (!System.Text.Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
			{
				var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
				Assert.NotEqual(0, read);
				length += read;
			}

			return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
		}
	}

	/// <summary>A client that asks the relay about itself is told its name, what it takes, which parts of the protocol it speaks, and its limits.</summary>
	[Fact(Timeout = 120000)]
	public async Task RelayTellsAboutItselfAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var server = StartPreset();
		var host = server.OnionHost;

		var answer = await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\nAccept: application/nostr+json\r\n\r\n", cancellationToken);
		var (head, body) = Split(answer);

		Assert.StartsWith("HTTP/1.1 200 OK\r\n", head, StringComparison.Ordinal);
		Assert.Contains("Content-Type: application/nostr+json\r\n", head, StringComparison.Ordinal);
		Assert.Contains("Access-Control-Allow-Origin: *\r\n", head, StringComparison.Ordinal);
		Assert.Contains($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n", head, StringComparison.Ordinal);

		using var information = JsonDocument.Parse(body);
		var root = information.RootElement;

		Assert.Equal("Black Hole relay", root.GetProperty("name").GetString());
		Assert.Equal(
			"A relay inside a Black Hole wallet. It takes the offers of makers and the announcements of coordinators for regtest (kind 15750). It takes them from anybody. It keeps an event for 24 hours.",
			root.GetProperty("description").GetString());
		Assert.Equal([1, 11], root.GetProperty("supported_nips").EnumerateArray().Select(nip => nip.GetInt32()));

		var limitation = root.GetProperty("limitation");

		Assert.Equal(16 * 1024, limitation.GetProperty("max_message_length").GetInt32());
		Assert.Equal(8, limitation.GetProperty("max_subscriptions").GetInt32());
		Assert.Equal(2_000, limitation.GetProperty("max_limit").GetInt32());
		Assert.Equal(64, limitation.GetProperty("max_subid_length").GetInt32());
		Assert.Equal(16, limitation.GetProperty("max_event_tags").GetInt32());
		Assert.Equal(2_000, limitation.GetProperty("max_content_length").GetInt32());
		Assert.Equal(5 * 60, limitation.GetProperty("created_at_upper_limit").GetInt32());

		// For how long the relay keeps what it takes: a day.
		Assert.Equal(24 * 60 * 60, Assert.Single(root.GetProperty("retention").EnumerateArray()).GetProperty("time").GetInt32());
		Assert.True(limitation.GetProperty("restricted_writes").GetBoolean());
		Assert.False(limitation.GetProperty("auth_required").GetBoolean());

		// The relay tells nothing of the computer that it runs on.
		Assert.DoesNotContain("Server:", head, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(["auth_required", "created_at_upper_limit", "max_content_length", "max_event_tags", "max_limit", "max_message_length", "max_subid_length", "max_subscriptions", "payment_required", "restricted_writes"], limitation.EnumerateObject().Select(x => x.Name).Order());
		Assert.Equal(["description", "limitation", "name", "retention", "supported_nips"], root.EnumerateObject().Select(x => x.Name).Order());

		// A browser asks first whether a page may read the answer.
		var preflight = await RelayTestClient.RequestAsync(server, $"OPTIONS / HTTP/1.1\r\nHost: {host}\r\nOrigin: https://client.example\r\n\r\n", cancellationToken);
		Assert.StartsWith("HTTP/1.1 204 ", preflight, StringComparison.Ordinal);
		Assert.Contains("Access-Control-Allow-Origin: *\r\n", preflight, StringComparison.Ordinal);

		// Whoever just looks is told what this is.
		var plain = await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\n\r\n", cancellationToken);
		Assert.StartsWith("HTTP/1.1 200 OK\r\n", plain, StringComparison.Ordinal);
		Assert.EndsWith("This is a Nostr relay. It is spoken to with a Nostr client.\n", plain, StringComparison.Ordinal);

		static (string Head, string Body) Split(string answer)
		{
			var end = answer.IndexOf("\r\n\r\n", StringComparison.Ordinal);
			return (answer[..(end + 2)], answer[(end + 4)..]);
		}
	}

	/// <summary>What is no request that the relay knows gets a short answer, and the connection is over.</summary>
	[Fact(Timeout = 120000)]
	public async Task WhatIsNoRequestOfARelayIsTurnedAwayAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var server = StartPreset(RelayLimits.ForBlackHoleEvents with { MaxRequestHeadBytes = 512, HandshakeTimeout = TimeSpan.FromSeconds(3) });
		var host = server.OnionHost;

		Assert.StartsWith("HTTP/1.1 405 ", await RelayTestClient.RequestAsync(server, $"POST / HTTP/1.1\r\nHost: {host}\r\n\r\n", cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 400 ", await RelayTestClient.RequestAsync(server, "nonsense\r\n\r\n", cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 400 ", await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.0\r\nHost: {host}\r\n\r\n", cancellationToken), StringComparison.Ordinal);
		Assert.StartsWith("HTTP/1.1 400 ", await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\nno colon\r\n\r\n", cancellationToken), StringComparison.Ordinal);

		// A request that is larger than a request may be.
		Assert.StartsWith("HTTP/1.1 400 ", await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\nX-Padding: {new string('x', 600)}\r\n\r\n", cancellationToken), StringComparison.Ordinal);

		// A WebSocket of another version than there is, or without its key.
		Assert.StartsWith(
			"HTTP/1.1 426 ",
			await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 8\r\n\r\n", cancellationToken),
			StringComparison.Ordinal);
		Assert.StartsWith(
			"HTTP/1.1 426 ",
			await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: short\r\nSec-WebSocket-Version: 13\r\n\r\n", cancellationToken),
			StringComparison.Ordinal);

		// Whoever connects and does not say what it wants is not waited for.
		using var silent = new TcpClient();
		await silent.ConnectAsync(server.EndPoint, cancellationToken);
		var buffer = new byte[16];
		var read = 0;

		try
		{
			read = await silent.GetStream().ReadAsync(buffer, cancellationToken);
		}
		catch (IOException)
		{
		}

		Assert.Equal(0, read);
	}

	/// <summary>A client sends an event, the relay keeps it, and whoever asks for it gets it.</summary>
	[Fact(Timeout = 120000)]
	public async Task EventIsTakenAndHandedOutAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		await using var server = StartPreset();

		using var maker = await RelayTestClient.ConnectAsync(server, cancellationToken);
		var offer = Offer(key, "{\"minFeeRate\":1.5}");

		Assert.Equal((true, ""), await maker.PublishAsync(offer, cancellationToken));
		Assert.Equal((true, "duplicate: already have this event"), await maker.PublishAsync(offer, cancellationToken));
		Assert.Equal(1, server.EventCount);

		using var taker = await RelayTestClient.ConnectAsync(server, cancellationToken);
		await taker.SendAsync($"[\"REQ\",\"book\",{{\"kinds\":[15750],\"since\":{Now - 900}}}]", cancellationToken);

		using (var message = JsonDocument.Parse((await taker.ReceiveAsync(cancellationToken))!))
		{
			Assert.Equal("EVENT", message.RootElement[0].GetString());
			Assert.Equal("book", message.RootElement[1].GetString());
			Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(offer), System.Text.Json.Nodes.JsonNode.Parse(message.RootElement[2].GetRawText())));
		}

		Assert.Equal("[\"EOSE\",\"book\"]", await taker.ReceiveAsync(cancellationToken));
		Assert.Equal(2, server.ConnectionCount);
	}

	/// <summary>The relay refuses what is no event, what is not signed by its key, and what is no event of Black Hole, and says why.</summary>
	[Fact(Timeout = 120000)]
	public async Task EventThatIsNotTakenIsRefusedWithTheReasonAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		await using var server = StartPreset();
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var note = TestRelayEvent.CreateJson(key, 1, "a note");
		Assert.Equal((false, "blocked: this relay takes only the offers and the coordinator announcements of Black Hole (kind 15750)"), await client.PublishAsync(note, cancellationToken));

		var forOtherNetwork = TestRelayEvent.CreateJson(key, 15750, "{}", [["type", "BlackHoleJoinMarket"], ["network", "mainnet"]]);
		Assert.Equal((false, "blocked: this relay takes events for regtest only"), await client.PublishAsync(forOtherNetwork, cancellationToken));

		Assert.Equal((false, "invalid: the event is from further in the future than clocks differ"), await client.PublishAsync(Offer(key, createdAt: Now + 301), cancellationToken));

		var changed = System.Text.Json.Nodes.JsonNode.Parse(Offer(key))!.AsObject();
		changed["content"] = "{\"sell\":1}";
		Assert.Equal((false, "invalid: the id is not the hash of the event"), await client.PublishAsync(changed.ToJsonString(), cancellationToken));

		var unsigned = System.Text.Json.Nodes.JsonNode.Parse(Offer(key))!.AsObject();
		unsigned["sig"] = new string('0', 128);
		Assert.Equal((false, "invalid: the signature is not the one of the key"), await client.PublishAsync(unsigned.ToJsonString(), cancellationToken));

		Assert.Equal((false, "invalid: the content is longer than 2000 characters"), await client.PublishAsync(Offer(key, new string('x', 2_001)), cancellationToken));

		Assert.Equal(0, server.EventCount);

		// Without an id there is nothing to answer about, so the relay says what it has to say to whoever reads along.
		await client.SendAsync("[\"EVENT\",{\"kind\":1}]", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: the event has no id of 64 characters of lowercase hex\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("[\"EVENT\"]", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: EVENT brings one event\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("not json", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: the message is not JSON\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("{\"EVENT\":1}", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: a message is a list that begins with its name\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("[\"AUTH\",\"challenge\"]", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"unsupported: this relay knows the messages EVENT, REQ and CLOSE\"]", await client.ReceiveAsync(cancellationToken));

		// After all that the connection is still good for an event that is one.
		Assert.Equal((true, ""), await client.PublishAsync(Offer(key), cancellationToken));
	}

	/// <summary>
	/// The application's own client talks to the relay: it finds it reachable, posts an offer to it, and reads the
	/// offer back the way the order book reads.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task ApplicationPostsToTheRelayAndReadsFromItAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		await using var server = StartPreset();

		var publisher = new NostrRelayPublisher([RelayTestClient.GetUri(server)], torSocksEndPoint: null, timeout: TimeSpan.FromSeconds(20));

		var reachability = Assert.Single(await publisher.CheckAsync(cancellationToken));
		Assert.True(reachability.IsReachable, reachability.Problem);

		var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 150_000_000, Points: 3, MatchedInputs: 3, ExtraInputs: 0);
		var signed = await MakerOfferEvent.Create(offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(Now)).ComputeIdAndSignAsync(key);

		var answer = Assert.Single(await publisher.PublishAsync(signed, cancellationToken));
		Assert.True(answer.Accepted, answer.Message);

		var read = Assert.Single(await publisher.ReadAsync(MakerOfferEvent.Kind, DateTimeOffset.FromUnixTimeSeconds(Now) - MakerOfferEvent.MaxAge, cancellationToken));
		Assert.True(read.IsAnswered, read.Problem);

		var readEvent = Assert.Single(read.Events);
		Assert.Equal(signed.Id, readEvent.Id);
		Assert.True(readEvent.Verify());
		Assert.True(MakerOfferEvent.TryParse(readEvent, Network.RegTest, out var readOffer));
		Assert.Equal(offer, readOffer);

		// The offer is taken back with an empty event, which takes its place on the relay.
		var withdrawal = await MakerOfferEvent.CreateWithdrawal(Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(Now + 1)).ComputeIdAndSignAsync(key);
		Assert.True(Assert.Single(await publisher.PublishAsync(withdrawal, cancellationToken)).Accepted);

		read = Assert.Single(await publisher.ReadAsync(MakerOfferEvent.Kind, DateTimeOffset.FromUnixTimeSeconds(Now) - MakerOfferEvent.MaxAge, cancellationToken));
		Assert.Equal(withdrawal.Id, Assert.Single(read.Events).Id);
		Assert.Equal(1, server.EventCount);
	}

	/// <summary>Whoever subscribed gets the events that come in afterwards, until it closes the subscription.</summary>
	[Fact(Timeout = 120000)]
	public async Task SubscriberGetsTheEventsThatComeInAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();
		await using var server = StartPreset();

		using var maker = await RelayTestClient.ConnectAsync(server, cancellationToken);
		using var listener = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var author = TestRelayEvent.GetPublicKey(key);

		Assert.Empty(await listener.SubscribeAsync("all", "{\"kinds\":[15750]}", cancellationToken));
		Assert.Empty(await listener.SubscribeAsync("one key", $"{{\"authors\":[\"{author}\"]}},{{\"authors\":[\"{author}\"],\"kinds\":[15750]}}", cancellationToken));
		Assert.Empty(await listener.SubscribeAsync("none", "{\"kinds\":[1]}", cancellationToken));

		var offer = Offer(key);
		Assert.True((await maker.PublishAsync(offer, cancellationToken)).IsAccepted);

		// Once for every subscription that asks for it, however many of its filters do.
		var received = new[] { await listener.ReceiveAsync(cancellationToken), await listener.ReceiveAsync(cancellationToken) };
		Assert.Equal(
			new[] { $"[\"EVENT\",\"all\",{Normalize(offer)}]", $"[\"EVENT\",\"one key\",{Normalize(offer)}]" }.Order(),
			received.Select(message => Normalize(message!)).Order());

		// The maker gets what it sent itself if it asked for it.
		Assert.Equal([GetId(offer)], await maker.SubscribeAsync("mine", "{}", cancellationToken));

		await listener.SendAsync("[\"CLOSE\",\"all\"]", cancellationToken);

		// The same name again puts other filters in the place of the ones before.
		Assert.Equal([GetId(offer)], await listener.SubscribeAsync("one key", "{\"kinds\":[15750],\"limit\":5}", cancellationToken));

		var otherOffer = Offer(otherKey);
		Assert.True((await maker.PublishAsync(otherOffer, cancellationToken)).IsAccepted);

		Assert.Equal(Normalize($"[\"EVENT\",\"one key\",{otherOffer}]"), Normalize((await listener.ReceiveAsync(cancellationToken))!));
		Assert.Equal(Normalize($"[\"EVENT\",\"mine\",{otherOffer}]"), Normalize((await maker.ReceiveAsync(cancellationToken))!));

		// Nothing else is on its way to the listener: what it asks next is what it is answered next.
		Assert.Equal([GetId(otherOffer), GetId(offer)], (await listener.SubscribeAsync("check", "{}", cancellationToken)).Order(StringComparer.Ordinal).ToArray() is var ids ? Sort(ids, GetId(otherOffer), GetId(offer)) : []);

		static string Normalize(string json) => System.Text.Json.Nodes.JsonNode.Parse(json)!.ToJsonString();

		static string[] Sort(string[] ids, string first, string second) => ids.Length == 2 && ids.Contains(first) && ids.Contains(second) ? [first, second] : ids;
	}

	/// <summary>Of an offer that is posted again, the relay keeps the newest and hands out that one only.</summary>
	[Fact(Timeout = 120000)]
	public async Task NewestOfferOfAKeyTakesThePlaceOfTheOneBeforeAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		await using var server = StartPreset();
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var first = Offer(key, "{\"sell\":150}", createdAt: Now - 60);
		var second = Offer(key, "{\"sell\":140}", createdAt: Now);

		Assert.Equal((true, ""), await client.PublishAsync(first, cancellationToken));
		Assert.Equal((true, ""), await client.PublishAsync(second, cancellationToken));
		Assert.Equal((false, "replaced: have newer event"), await client.PublishAsync(first, cancellationToken));

		Assert.Equal([GetId(second)], await client.SubscribeAsync("book", "{\"kinds\":[15750]}", cancellationToken));
		Assert.Equal(1, server.EventCount);
	}

	/// <summary>The relay keeps an event for a day from when it took it, whatever time the event has, and drops it then.</summary>
	[Fact(Timeout = 120000)]
	public async Task EventIsKeptForADayAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		using var coordinatorKey = TestRelayEvent.NewKey();
		using var lateKey = TestRelayEvent.NewKey();
		long time = Now;

		await using var server = StartPreset(now: () => DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref time)));
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		// An offer that was made an hour ago, and the announcement of a coordinator.
		var offer = Offer(key, createdAt: Now - 3_600);
		var announcement = TestRelayEvent.CreateJson(coordinatorKey, 15750, "A coordinator", [["type", "wabisabi"], ["network", "regtest"], ["endpoint", "http://coordinator.example/"]]);

		Assert.True((await client.PublishAsync(offer, cancellationToken)).IsAccepted);
		Assert.True((await client.PublishAsync(announcement, cancellationToken)).IsAccepted);

		// Twelve hours later another offer comes.
		Volatile.Write(ref time, Now + 12 * 3_600);
		var lateOffer = Offer(lateKey, createdAt: Now + 12 * 3_600);
		Assert.True((await client.PublishAsync(lateOffer, cancellationToken)).IsAccepted);

		Volatile.Write(ref time, Now + 24 * 3_600 - 1);
		Assert.Equal(3, (await client.SubscribeAsync("a", "{}", cancellationToken)).Length);

		// A day after the relay took them, the first two are gone: also the offer, which was an hour old when it came.
		Volatile.Write(ref time, Now + 24 * 3_600);
		Assert.Equal([GetId(lateOffer)], await client.SubscribeAsync("b", "{}", cancellationToken));

		Volatile.Write(ref time, Now + 36 * 3_600);
		Assert.Empty(await client.SubscribeAsync("c", "{}", cancellationToken));
	}

	/// <summary>A filter is answered with the newest stored events, up to its limit and up to the limit of the relay.</summary>
	[Fact(Timeout = 120000)]
	public async Task StoredEventsComeNewestFirstUpToTheLimitAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var limits = RelayLimits.ForBlackHoleEvents with { MaxLimit = 20, EventBurst = 100, MessageBurst = 200 };
		await using var server = StartPreset(limits);
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var keys = Enumerable.Range(0, 30).Select(_ => TestRelayEvent.NewKey()).ToArray();

		try
		{
			var offers = keys.Select((key, i) => Offer(key, createdAt: Now - 60 + i)).ToArray();

			foreach (var offer in offers)
			{
				Assert.True((await client.PublishAsync(offer, cancellationToken)).IsAccepted);
			}

			var newestFirst = offers.Reverse().Select(GetId).ToArray();

			Assert.Equal(newestFirst[..10], await client.SubscribeAsync("ten", "{\"limit\":10}", cancellationToken));
			Assert.Equal(newestFirst[..20], await client.SubscribeAsync("all", "{}", cancellationToken));
			Assert.Equal(newestFirst[..20], await client.SubscribeAsync("many", "{\"limit\":1000}", cancellationToken));
			Assert.Empty(await client.SubscribeAsync("none", "{\"limit\":0}", cancellationToken));

			// An event that two filters ask for comes once.
			Assert.Equal(newestFirst[..5], await client.SubscribeAsync("twice", "{\"limit\":5},{\"limit\":3}", cancellationToken));
		}
		finally
		{
			foreach (var key in keys)
			{
				key.Dispose();
			}
		}
	}

	/// <summary>What a client may not ask for is refused with the reason, and the connection stays.</summary>
	[Fact(Timeout = 120000)]
	public async Task SubscriptionBeyondTheLimitsIsRefusedAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var limits = RelayLimits.ForBlackHoleEvents with { MaxSubscriptions = 2, MaxFilters = 2, MaxSubscriptionIdLength = 8 };
		await using var server = StartPreset(limits);
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		await client.SendAsync("[\"REQ\",\"a\",{},{},{}]", cancellationToken);
		Assert.Equal("[\"CLOSED\",\"a\",\"invalid: a subscription has 2 filters at most\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("[\"REQ\",\"a\",{\"search\":\"x\"}]", cancellationToken);
		Assert.Equal("[\"CLOSED\",\"a\",\"unsupported: the filter contains unknown elements\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("[\"REQ\",\"a\",{\"kinds\":\"all\"}]", cancellationToken);
		Assert.StartsWith("[\"CLOSED\",\"a\",\"invalid: the kinds of a filter", await client.ReceiveAsync(cancellationToken), StringComparison.Ordinal);

		await client.SendAsync("[\"REQ\",\"a\"]", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: REQ names a subscription with up to 8 characters, and one filter at least\"]", await client.ReceiveAsync(cancellationToken));

		await client.SendAsync("[\"REQ\",\"too long a name\",{}]", cancellationToken);
		Assert.StartsWith("[\"NOTICE\",\"invalid: REQ names a subscription", await client.ReceiveAsync(cancellationToken), StringComparison.Ordinal);

		await client.SendAsync("[\"REQ\",\"\",{}]", cancellationToken);
		Assert.StartsWith("[\"NOTICE\",\"invalid: REQ names a subscription", await client.ReceiveAsync(cancellationToken), StringComparison.Ordinal);

		await client.SendAsync("[\"REQ\",5,{}]", cancellationToken);
		Assert.StartsWith("[\"NOTICE\",\"invalid: REQ names a subscription", await client.ReceiveAsync(cancellationToken), StringComparison.Ordinal);

		await client.SendAsync("[\"CLOSE\"]", cancellationToken);
		Assert.Equal("[\"NOTICE\",\"invalid: CLOSE names one subscription\"]", await client.ReceiveAsync(cancellationToken));

		// None of these opened a subscription, so there is room for as many as there may be.
		Assert.Empty(await client.SubscribeAsync("a", "{}", cancellationToken));
		Assert.Empty(await client.SubscribeAsync("b", "{}", cancellationToken));

		await client.SendAsync("[\"REQ\",\"c\",{}]", cancellationToken);
		Assert.Equal("[\"CLOSED\",\"c\",\"rate-limited: a connection has 2 subscriptions at most\"]", await client.ReceiveAsync(cancellationToken));

		// One that is open can be asked anew, and one that was closed makes room.
		Assert.Empty(await client.SubscribeAsync("a", "{\"kinds\":[15750]}", cancellationToken));
		await client.SendAsync("[\"CLOSE\",\"b\"]", cancellationToken);
		Assert.Empty(await client.SubscribeAsync("c", "{}", cancellationToken));
	}

	/// <summary>A message that is larger than a message may be ends the connection, and so does one that is no text.</summary>
	[Fact(Timeout = 120000)]
	public async Task MessageThatIsTooLargeEndsTheConnectionAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var server = StartPreset(RelayLimits.ForBlackHoleEvents with { MaxMessageBytes = 1_024 });

		using (var client = await RelayTestClient.ConnectAsync(server, cancellationToken))
		{
			// A message at the limit is read.
			await client.SendAsync("[\"REQ\",\"a\",{}" + new string(' ', 1_024 - 14) + "]", cancellationToken);
			Assert.Equal("[\"EOSE\",\"a\"]", await client.ReceiveAsync(cancellationToken));

			await client.SendAsync("[\"REQ\",\"b\",{}" + new string(' ', 1_024 - 13) + "]", cancellationToken);
			Assert.Equal("[\"NOTICE\",\"error: a message has 1024 bytes at most\"]", await client.ReceiveAsync(cancellationToken));
			Assert.Null(await client.ReceiveAsync(cancellationToken));
			Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
		}

		using (var client = await RelayTestClient.ConnectAsync(server, cancellationToken))
		{
			await client.SendBinaryAsync([1, 2, 3], cancellationToken);
			Assert.Null(await client.ReceiveAsync(cancellationToken));
			Assert.Equal(WebSocketCloseStatus.InvalidMessageType, client.CloseStatus);
		}
	}

	/// <summary>
	/// A client is held to a pace. Beyond it its events and its subscriptions are refused, and a client that goes on
	/// regardless is disconnected.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task ClientIsHeldToAPaceAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var limits = RelayLimits.ForBlackHoleEvents with { MessageBurst = 6, MessagesPerSecond = 0.001, EventBurst = 2, EventsPerSecond = 0.001, MaxPaceViolations = 3 };
		await using var server = StartPreset(limits);
		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

		var keys = Enumerable.Range(0, 4).Select(_ => TestRelayEvent.NewKey()).ToArray();

		try
		{
			var offers = keys.Select(key => Offer(key)).ToArray();

			// Events have a pace of their own, as checking and keeping them is the work that the relay does.
			Assert.Equal((true, ""), await client.PublishAsync(offers[0], cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(offers[1], cancellationToken));
			Assert.Equal((false, "rate-limited: slow down"), await client.PublishAsync(offers[2], cancellationToken));
			Assert.Equal(2, server.EventCount);

			// Other messages are still within the pace of messages.
			Assert.Equal(2, (await client.SubscribeAsync("a", "{}", cancellationToken)).Length);
			await client.SendAsync("[\"CLOSE\",\"a\"]", cancellationToken);
			Assert.Equal(2, (await client.SubscribeAsync("b", "{}", cancellationToken)).Length);

			// Six messages were within the pace. From here on nothing is.
			await client.SendAsync("[\"REQ\",\"c\",{}]", cancellationToken);
			Assert.Equal("[\"CLOSED\",\"c\",\"rate-limited: slow down\"]", await client.ReceiveAsync(cancellationToken));
			Assert.Equal((false, "rate-limited: slow down"), await client.PublishAsync(offers[3], cancellationToken));
			await client.SendAsync("[\"REQ\",\"d\",{}]", cancellationToken);
			Assert.Equal("[\"CLOSED\",\"d\",\"rate-limited: slow down\"]", await client.ReceiveAsync(cancellationToken));

			// And once too often.
			await client.SendAsync("[\"REQ\",\"e\",{}]", cancellationToken);
			Assert.Null(await client.ReceiveAsync(cancellationToken));
			Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
			Assert.Equal(2, server.EventCount);
		}
		finally
		{
			foreach (var key in keys)
			{
				key.Dispose();
			}
		}
	}

	/// <summary>When as many clients are connected as may be, the one that was silent the longest makes way for a new one.</summary>
	[Fact(Timeout = 120000)]
	public async Task ClientThatWasSilentTheLongestMakesWayAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await using var server = StartPreset(RelayLimits.ForBlackHoleEvents with { MaxConnections = 3 });

		using var first = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await first.SubscribeAsync("a", "{}", cancellationToken));
		await Task.Delay(50, cancellationToken);

		using var second = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await second.SubscribeAsync("a", "{}", cancellationToken));
		await Task.Delay(50, cancellationToken);

		using var third = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await third.SubscribeAsync("a", "{}", cancellationToken));
		await Task.Delay(50, cancellationToken);

		// The first one speaks again, so the second is the one that was silent the longest.
		Assert.Empty(await first.SubscribeAsync("b", "{}", cancellationToken));

		using var fourth = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await fourth.SubscribeAsync("a", "{}", cancellationToken));

		Assert.Null(await second.ReceiveAsync(cancellationToken));
		Assert.Empty(await first.SubscribeAsync("c", "{}", cancellationToken));
		Assert.Empty(await third.SubscribeAsync("b", "{}", cancellationToken));
		Assert.Equal(3, server.ConnectionCount);
	}

	/// <summary>A client that subscribed and does not read what comes in is disconnected: what it is owed does not pile up.</summary>
	[Fact(Timeout = 120000)]
	public async Task ClientThatDoesNotReadIsDisconnectedAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		await using var server = StartPreset(RelayLimits.ForBlackHoleEvents with { MaxQueuedBytes = 16 });

		using var listener = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await listener.SubscribeAsync("all", "{}", cancellationToken));

		using var maker = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.True((await maker.PublishAsync(Offer(key), cancellationToken)).IsAccepted);

		// One event is more than may wait here, so the listener is gone, and the maker is not.
		Assert.Null(await listener.ReceiveAsync(cancellationToken));
		Assert.Empty(await maker.SubscribeAsync("still here", "{\"limit\":0}", cancellationToken));
	}

	/// <summary>
	/// With a list of the user's own, the relay takes the kinds of the list and keeps them in a file: they are there
	/// again when the relay is started again. An ephemeral event is handed to whoever listens, and not kept.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task KindsOfTheUsersListAreKeptInAFileAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var key = TestRelayEvent.NewKey();
		var folder = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay");

		Assert.True(KindList.TryParse("0, 1, 20000-29999", out var kinds, out _));

		var note = TestRelayEvent.CreateJson(key, 1, "a note that stays", [["t", "kept"]]);
		var profile = TestRelayEvent.CreateJson(key, 0, "{\"name\":\"somebody\"}");
		var ephemeral = TestRelayEvent.CreateJson(key, 20_001, "for whoever listens now");

		await using (var server = new NostrRelayServer(new CustomEventSet(kinds), folder, now: Clock))
		{
			RelayTestClient.Open(server);

			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);
			using var listener = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Empty(await listener.SubscribeAsync("live", "{\"kinds\":[20001]}", cancellationToken));

			Assert.Equal((true, ""), await client.PublishAsync(note, cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(profile, cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(ephemeral, cancellationToken));
			Assert.Equal((false, "blocked: this relay does not take events of kind 15750"), await client.PublishAsync(Offer(key), cancellationToken));

			Assert.Contains(GetId(ephemeral), await listener.ReceiveAsync(cancellationToken), StringComparison.Ordinal);
			Assert.Equal(2, server.EventCount);

			// The relay of a custom list does not say which program runs it.
			var information = await RelayTestClient.RequestAsync(server, $"GET / HTTP/1.1\r\nHost: {server.OnionHost}\r\nAccept: application/nostr+json\r\n\r\n", cancellationToken);
			Assert.Contains("\"name\":\"Nostr relay\"", information, StringComparison.Ordinal);
			Assert.Contains("0, 1, 20000-29999", information, StringComparison.Ordinal);
			Assert.DoesNotContain("Black Hole", information, StringComparison.Ordinal);
		}

		Assert.True(File.Exists(Path.Combine(folder, "Events.sqlite")));

		await using (var server = new NostrRelayServer(new CustomEventSet(kinds), folder, now: Clock))
		{
			RelayTestClient.Open(server);

			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Equal(new[] { GetId(note), GetId(profile) }.Order(), (await client.SubscribeAsync("all", "{}", cancellationToken)).Order());
			Assert.Equal([GetId(note)], await client.SubscribeAsync("tagged", "{\"#t\":[\"kept\"]}", cancellationToken));
		}

		// The events of Black Hole are kept in a file of their own, so that neither relay hands out what the other took.
		var offer = Offer(key);

		await using (var server = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest), folder, now: Clock))
		{
			RelayTestClient.Open(server);

			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Empty(await client.SubscribeAsync("all", "{}", cancellationToken));
			Assert.Equal((true, ""), await client.PublishAsync(offer, cancellationToken));
		}

		Assert.True(File.Exists(Path.Combine(folder, "BlackHoleEvents.sqlite")));

		await using (var server = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest), folder, now: Clock))
		{
			RelayTestClient.Open(server);

			using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);

			Assert.Equal([GetId(offer)], await client.SubscribeAsync("all", "{}", cancellationToken));
		}
	}

	/// <summary>A relay that is stopped disconnects its clients and gives its port back.</summary>
	[Fact(Timeout = 120000)]
	public async Task StoppedRelayIsGoneAsync()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var server = StartPreset();
		var endPoint = server.EndPoint;

		using var client = await RelayTestClient.ConnectAsync(server, cancellationToken);
		Assert.Empty(await client.SubscribeAsync("a", "{}", cancellationToken));

		await server.DisposeAsync();
		await server.DisposeAsync();

		Assert.Null(await client.ReceiveAsync(cancellationToken));

		using var probe = new TcpClient();
		await Assert.ThrowsAnyAsync<SocketException>(async () => await probe.ConnectAsync(endPoint, cancellationToken));

		// The port can be taken again.
		await using var again = new NostrRelayServer(new BlackHoleEventSet(Network.RegTest), port: endPoint.Port);
		Assert.Equal(endPoint.Port, again.EndPoint.Port);
	}
}
