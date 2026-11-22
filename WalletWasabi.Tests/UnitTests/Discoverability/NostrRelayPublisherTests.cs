using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.Discoverability;
using WalletWasabi.JoinMarket;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Discoverability;

/// <summary>
/// Events are sent to Nostr relays one by one, and every relay tells what it made of an event.
/// The relays of these tests run inside the test, on this computer.
/// </summary>
public class NostrRelayPublisherTests
{
	private static readonly MakerOffer Offer = new(new FeeRate(2m), Money.Satoshis(100), Money.Satoshis(150), Money.Coins(1));

	[Fact]
	public async Task EveryRelayTellsWhatItMadeOfTheEventAsync()
	{
		await using var open = new TestNostrRelay();
		await using var picky = new TestNostrRelay { Judge = _ => (false, "blocked: not on the list") };
		var nobody = new Uri($"ws://127.0.0.1:{TestNostrRelay.GetFreePort()}/");

		var publisher = new NostrRelayPublisher([open.Uri, picky.Uri, nobody], torSocksEndPoint: null, TimeSpan.FromSeconds(20));
		var nostrEvent = await CreateEventAsync();

		var answers = await publisher.PublishAsync(nostrEvent, CancellationToken.None);

		Assert.Equal([open.Uri, picky.Uri, nobody], answers.Select(answer => answer.Relay));

		Assert.True(answers[0].Accepted);
		Assert.Equal("", answers[0].Message);

		Assert.False(answers[1].Accepted);
		Assert.Equal("blocked: not on the list", answers[1].Message);

		// A relay that is not there is an answer like the others, with the reason.
		Assert.False(answers[2].Accepted);
		Assert.NotEqual("", answers[2].Message);

		// The relay that took the event has it, as it was signed. The relay that refused it has seen it and does not keep it.
		var stored = Assert.Single(open.Stored);
		Assert.Equal(nostrEvent.Id, stored.Id);
		Assert.True(stored.Verify());
		Assert.Empty(picky.Stored);
		Assert.Single(picky.Received);
	}

	/// <summary>
	/// What a relay says about an event is shown to the user and written into the log. A relay can say anything, so
	/// only a line of it is kept.
	/// </summary>
	[Fact]
	public async Task WhatARelaySaysIsKeptToOneShortLineAsync()
	{
		var chatter = "blocked: no\r\n2026-01-01 00:00:00 INF | Everything is fine.\n" + new string('x', 5_000);
		await using var relay = new TestNostrRelay { Judge = _ => (false, chatter) };
		var publisher = new NostrRelayPublisher([relay.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20));

		var answer = (await publisher.PublishAsync(await CreateEventAsync(), CancellationToken.None)).Single();

		Assert.False(answer.Accepted);
		Assert.StartsWith("blocked: no  2026-01-01 00:00:00 INF | Everything is fine. xxx", answer.Message);
		Assert.Equal(201, answer.Message.Length);
		Assert.EndsWith("…", answer.Message);
		Assert.DoesNotContain(answer.Message, character => char.IsControl(character));
	}

	[Fact]
	public async Task RelayThatDoesNotAnswerIsNotWaitedForForeverAsync()
	{
		await using var silent = new TestNostrRelay { IsSilent = true };
		await using var open = new TestNostrRelay();

		// Long enough for the relay that answers, also on a busy machine.
		var publisher = new NostrRelayPublisher([silent.Uri, open.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(6));

		var answers = await publisher.PublishAsync(await CreateEventAsync(), CancellationToken.None);

		Assert.False(answers[0].Accepted);
		Assert.Equal("The relay did not answer in time.", answers[0].Message);
		Assert.True(answers[1].Accepted);
	}

	[Fact]
	public async Task CallerCanGiveUpAsync()
	{
		await using var silent = new TestNostrRelay { IsSilent = true };
		var publisher = new NostrRelayPublisher([silent.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(30));
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await publisher.PublishAsync(await CreateEventAsync(), cts.Token));
	}

	/// <summary>
	/// With Tor, a relay is reached through the SOCKS port. The proxy is told the name of the relay and finds it itself:
	/// this computer asks nobody where the relay is, and it does not connect to it.
	/// </summary>
	[Fact]
	public async Task WithTorTheRelayIsReachedThroughTheSocksPortByItsNameAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var tor = new TestSocks5Proxy(relay.EndPoint);

		// A name that no name server knows: the event can only arrive if the proxy is asked for it.
		var publisher = new NostrRelayPublisher([new Uri("ws://relay.invalid:7777/")], tor.EndPoint, TimeSpan.FromSeconds(20));
		var nostrEvent = await CreateEventAsync();

		var answers = await publisher.PublishAsync(nostrEvent, CancellationToken.None);

		Assert.True(answers.Single().Accepted, answers.Single().Message);
		Assert.Equal(nostrEvent.Id, Assert.Single(relay.Stored).Id);

		var request = Assert.Single(tor.Requests);
		Assert.True(request.IsHostName);
		Assert.Equal("relay.invalid", request.Host);
		Assert.Equal(7777, request.Port);
	}

	/// <summary>
	/// Tor puts connections with different SOCKS user names on different circuits. The user name is the key that signed
	/// the event, so the connections of two keys cannot be told to come from one place, and those of one key share a circuit.
	/// </summary>
	[Fact]
	public async Task WithTorEveryKeyHasCircuitsOfItsOwnAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var tor = new TestSocks5Proxy(relay.EndPoint);
		var publisher = new NostrRelayPublisher([new Uri("ws://relay.invalid:7777/")], tor.EndPoint, TimeSpan.FromSeconds(20));

		using var firstKey = CreateKey();
		using var secondKey = CreateKey();

		await publisher.PublishAsync(await CreateEventAsync(firstKey, 1_800_000_000), CancellationToken.None);
		await publisher.PublishAsync(await CreateEventAsync(firstKey, 1_800_000_001), CancellationToken.None);
		await publisher.PublishAsync(await CreateEventAsync(secondKey, 1_800_000_000), CancellationToken.None);

		var userNames = tor.Requests.Select(request => request.UserName).ToArray();

		Assert.Equal(3, userNames.Length);
		Assert.All(userNames, userName => Assert.False(string.IsNullOrEmpty(userName)));
		Assert.Equal(userNames[0], userNames[1]);
		Assert.NotEqual(userNames[0], userNames[2]);
		Assert.All(tor.Requests, request => Assert.Equal(request.UserName, request.Password));
	}

	[Fact]
	public async Task WithoutTorTheRelayIsReachedDirectlyAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var tor = new TestSocks5Proxy(relay.EndPoint);
		var publisher = new NostrRelayPublisher([relay.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20));

		var answers = await publisher.PublishAsync(await CreateEventAsync(), CancellationToken.None);

		Assert.True(answers.Single().Accepted);
		Assert.Empty(tor.Requests);
	}

	/// <summary>
	/// Before the application takes the relays that the user names, it finds out which of them answer. It connects to
	/// each of them and sends nothing.
	/// </summary>
	[Fact]
	public async Task CheckTellsWhichRelaysCanBeReachedAsync()
	{
		await using var relay = new TestNostrRelay();
		var nobody = new Uri($"ws://127.0.0.1:{TestNostrRelay.GetFreePort()}/");
		var publisher = new NostrRelayPublisher([relay.Uri, nobody], torSocksEndPoint: null, TimeSpan.FromSeconds(20));

		var reachabilities = await publisher.CheckAsync(CancellationToken.None);

		Assert.Equal([relay.Uri, nobody], reachabilities.Select(x => x.Relay));

		Assert.True(reachabilities[0].IsReachable);
		Assert.Null(reachabilities[0].Problem);

		// A relay that is not there is an answer like the other, with the reason.
		Assert.False(reachabilities[1].IsReachable);
		Assert.False(string.IsNullOrWhiteSpace(reachabilities[1].Problem));
		Assert.DoesNotContain(reachabilities[1].Problem!, character => char.IsControl(character));

		// The relay was connected to once, and it was sent nothing.
		Assert.Equal(1, relay.ConnectionCount);
		Assert.Empty(relay.Received);
		Assert.Empty(relay.Stored);
	}

	[Fact]
	public async Task CheckIsMadeThroughTorOnACircuitOfItsOwnAsync()
	{
		await using var relay = new TestNostrRelay();
		await using var tor = new TestSocks5Proxy(relay.EndPoint);
		var publisher = new NostrRelayPublisher([new Uri("ws://relay.invalid:7777/")], tor.EndPoint, TimeSpan.FromSeconds(20));

		var first = await publisher.CheckAsync(CancellationToken.None);
		var second = await publisher.CheckAsync(CancellationToken.None);

		Assert.True(first.Single().IsReachable, first.Single().Problem);
		Assert.True(second.Single().IsReachable, second.Single().Problem);

		// The proxy is asked for the relay by its name, like for an event.
		Assert.All(tor.Requests, request => Assert.True(request.IsHostName));
		Assert.All(tor.Requests, request => Assert.Equal("relay.invalid", request.Host));

		// Every check has a user name of its own, so Tor keeps it apart from everything else, the offers included.
		var userNames = tor.Requests.Select(request => request.UserName).ToArray();
		Assert.Equal(2, userNames.Length);
		Assert.All(userNames, userName => Assert.False(string.IsNullOrEmpty(userName)));
		Assert.NotEqual(userNames[0], userNames[1]);
	}

	[Fact]
	public async Task CheckDoesNotWaitForeverAndCanBeGivenUpAsync()
	{
		// Something that takes the connection and never says a word, not even what a relay says first.
		using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var mute = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

		var publisher = new NostrRelayPublisher([mute], torSocksEndPoint: null, TimeSpan.FromSeconds(1));
		var reachability = (await publisher.CheckAsync(CancellationToken.None)).Single();

		Assert.False(reachability.IsReachable);
		Assert.Equal("The relay did not answer in time.", reachability.Problem);

		var patient = new NostrRelayPublisher([mute], torSocksEndPoint: null, TimeSpan.FromSeconds(30));
		using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await patient.CheckAsync(cts.Token));
	}

	/// <summary>
	/// The relays of the application can be exchanged while it runs. Whoever posts holds one publisher and posts to
	/// the new relays from then on, without being told.
	/// </summary>
	[Fact]
	public async Task SwitchablePublisherPostsToTheRelaysItWasSwitchedToAsync()
	{
		await using var first = new TestNostrRelay();
		await using var second = new TestNostrRelay();

		var publisher = new SwitchableNostrPublisher(new NostrRelayPublisher([first.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20)));
		INostrPublisher seenByOthers = publisher;

		Assert.Equal([first.Uri], seenByOthers.Relays);
		Assert.True((await seenByOthers.PublishAsync(await CreateEventAsync(), CancellationToken.None)).Single().Accepted);

		publisher.SwitchTo(new NostrRelayPublisher([second.Uri], torSocksEndPoint: null, TimeSpan.FromSeconds(20)));

		Assert.Equal([second.Uri], seenByOthers.Relays);
		var answer = (await seenByOthers.PublishAsync(await CreateEventAsync(), CancellationToken.None)).Single();

		Assert.True(answer.Accepted);
		Assert.Equal(second.Uri, answer.Relay);
		Assert.Single(first.Stored);
		Assert.Single(second.Stored);
	}

	[Fact]
	public void RelaysAreWebSocketAddresses()
	{
		Assert.Equal(
			[new Uri("wss://relay.example"), new Uri("ws://127.0.0.1:7777/"), new Uri("wss://abc.onion/nostr")],
			NostrRelayPublisher.ParseRelays(["wss://relay.example", " ws://127.0.0.1:7777/ ", "", "wss://abc.onion/nostr"]));

		Assert.Empty(NostrRelayPublisher.ParseRelays([]));

		Assert.Throws<FormatException>(() => NostrRelayPublisher.ParseRelays(["https://relay.example"]));
		Assert.Throws<FormatException>(() => NostrRelayPublisher.ParseRelays(["relay.example"]));
		Assert.Throws<FormatException>(() => NostrRelayPublisher.ParseRelays(["wss://relay.example", "nonsense"]));
	}

	[Fact]
	public void RelayThatIsNamedTwiceIsAskedOnce()
	{
		var publisher = new NostrRelayPublisher([new Uri("wss://relay.example"), new Uri("wss://relay.example/"), new Uri("wss://other.example")], torSocksEndPoint: null);

		Assert.Equal([new Uri("wss://relay.example"), new Uri("wss://other.example")], publisher.Relays);
	}

	private static async Task<NostrEvent> CreateEventAsync()
	{
		using var key = CreateKey();
		return await CreateEventAsync(key, 1_800_000_000);
	}

	private static Task<NostrEvent> CreateEventAsync(ECPrivKey key, long createdAt) =>
		MakerOfferEvent.Create(Offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(createdAt)).ComputeIdAndSignAsync(key);

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}
}
