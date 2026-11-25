using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin.Secp256k1;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The store of a relay: what it keeps, what takes the place of what, what it hands out for a filter, and what
/// happens when the room for events is used up.
/// </summary>
public class RelayEventStoreTests
{
	private const long Now = TestRelayEvent.Now;

	private static readonly RelayLimits Limits = RelayLimits.ForCustomEvents;

	private static RelayFilter Filter(string json)
	{
		using var document = JsonDocument.Parse(json);

		return RelayFilter.TryParse(document.RootElement, Limits, out var filter, out var problem)
			? filter
			: throw new InvalidOperationException(problem);
	}

	private static string[] QueryIds(RelayEventStore store, string filter = "{}", long now = Now, int pageSize = 1_000) =>
		[.. store.Query(Filter(filter), now, pageSize).Select(stored => stored.Id)];

	/// <summary>An event that is kept is handed out again as it came, and the store has it once.</summary>
	[Fact]
	public void EventIsKeptOnce()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);
		var note = TestRelayEvent.Create(key, 1, "text", [["t", "tag"]]);

		Assert.Equal(StoreOutcome.Stored, store.Add(note, expiresAt: null, Now));
		Assert.Equal(StoreOutcome.Duplicate, store.Add(note, expiresAt: null, Now));

		var stored = Assert.Single(store.Query(Filter("{}"), Now, 10));

		Assert.Equal(new StoredEvent(note.CreatedAt, note.Id, note.Json), stored);
		Assert.Equal(1, store.Count);
		Assert.Equal(note.Size, store.Bytes);
	}

	/// <summary>Of a replaceable kind the newest event of a key stays. An older one that comes later is not kept.</summary>
	[Fact]
	public void NewestEventOfAReplaceableKindStays()
	{
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		var first = TestRelayEvent.Create(key, 15750, "first", createdAt: Now - 20);
		var second = TestRelayEvent.Create(key, 15750, "second", createdAt: Now - 10);
		var ofOtherKey = TestRelayEvent.Create(otherKey, 15750, "other key", createdAt: Now - 30);
		var ofOtherKind = TestRelayEvent.Create(key, 10002, "other kind", createdAt: Now - 30);

		Assert.Equal(StoreOutcome.Stored, store.Add(first, null, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(ofOtherKey, null, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(ofOtherKind, null, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(second, null, Now));

		Assert.Equal([second.Id, ofOtherKey.Id, ofOtherKind.Id], QueryIds(store).Order(), QueryIdsOrder([second, ofOtherKey, ofOtherKind]));
		Assert.Equal(StoreOutcome.Superseded, store.Add(first, null, Now));
		Assert.Equal(3, store.Count);
		Assert.Equal(second.Size + ofOtherKey.Size + ofOtherKind.Size, store.Bytes);

		// Of two events of the same second the one with the lower id stays, whichever comes first.
		var twins = new[] { "a", "b", "c", "d" }.Select(content => TestRelayEvent.Create(key, 0, content, createdAt: Now)).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();

		Assert.Equal(StoreOutcome.Stored, store.Add(twins[2], null, Now));
		Assert.Equal(StoreOutcome.Superseded, store.Add(twins[3], null, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(twins[0], null, Now));
		Assert.Equal(StoreOutcome.Superseded, store.Add(twins[1], null, Now));
		Assert.Equal([twins[0].Id], QueryIds(store, """{"kinds":[0]}"""));

		static System.Collections.Generic.IEqualityComparer<System.Collections.Generic.IEnumerable<string>> QueryIdsOrder(RelayEvent[] expected) =>
			System.Collections.Generic.EqualityComparer<System.Collections.Generic.IEnumerable<string>>.Create((_, actual) => actual!.Order().SequenceEqual(expected.Select(x => x.Id).Order()));
	}

	/// <summary>Of an addressable kind the newest event of a key and of a name stays: events with other names are other events.</summary>
	[Fact]
	public void NewestEventOfANameStays()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		var article = TestRelayEvent.Create(key, 30023, "first draft", [["d", "article"]], createdAt: Now - 20);
		var revised = TestRelayEvent.Create(key, 30023, "revised", [["d", "article"]], createdAt: Now - 10);
		var otherArticle = TestRelayEvent.Create(key, 30023, "another one", [["d", "other"]], createdAt: Now - 30);
		var unnamed = TestRelayEvent.Create(key, 30023, "no name", createdAt: Now - 30);
		var unnamedToo = TestRelayEvent.Create(key, 30023, "empty name", [["d", ""]], createdAt: Now - 5);

		foreach (var relayEvent in new[] { article, otherArticle, unnamed, revised, unnamedToo })
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(relayEvent, null, Now));
		}

		// No name and an empty name are the same name.
		Assert.Equal([unnamedToo.Id, revised.Id, otherArticle.Id], QueryIds(store));
		Assert.Equal(StoreOutcome.Superseded, store.Add(article, null, Now));

		// An event of a kind that is not addressable is not replaced by its name.
		var note = TestRelayEvent.Create(key, 1, "one", [["d", "article"]], createdAt: Now - 2);
		var otherNote = TestRelayEvent.Create(key, 1, "two", [["d", "article"]], createdAt: Now - 1);

		Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(otherNote, null, Now));
		Assert.Equal(5, store.Count);
	}

	/// <summary>The store hands out what a filter asks for: by id, by key, by kind, by time and by tag.</summary>
	[Fact]
	public void StoreHandsOutWhatAFilterAsksFor()
	{
		using var alice = TestRelayEvent.NewKey();
		using var bob = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		var idOfSomething = new string('e', 64);
		var note = TestRelayEvent.Create(alice, 1, "note", [["t", "nostr"], ["e", idOfSomething, "wss://relay.example"]], createdAt: 100);
		var reply = TestRelayEvent.Create(bob, 1, "reply", [["e", note.Id], ["p", TestRelayEvent.GetPublicKey(alice)], ["t", "nostr"], ["t", "relay"]], createdAt: 200);
		var reaction = TestRelayEvent.Create(bob, 7, "+", [["e", note.Id], ["T", "upper"], ["long", "name"]], createdAt: 300);
		var article = TestRelayEvent.Create(alice, 30023, "article", [["d", "name"], ["t", "relay"]], createdAt: 400);

		foreach (var relayEvent in new[] { note, reply, reaction, article })
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(relayEvent, null, Now));
		}

		var a = TestRelayEvent.GetPublicKey(alice);
		var b = TestRelayEvent.GetPublicKey(bob);

		// The newest comes first.
		Assert.Equal([article.Id, reaction.Id, reply.Id, note.Id], QueryIds(store));
		Assert.Equal([reply.Id, note.Id], QueryIds(store, $$"""{"ids":["{{note.Id}}","{{reply.Id}}","{{idOfSomething}}"]}"""));
		Assert.Equal([article.Id, note.Id], QueryIds(store, $$"""{"authors":["{{a}}"]}"""));
		Assert.Equal([reaction.Id, reply.Id], QueryIds(store, $$"""{"authors":["{{b}}"]}"""));
		Assert.Equal([reaction.Id, reply.Id, note.Id], QueryIds(store, """{"kinds":[1,7]}"""));
		Assert.Equal([reply.Id], QueryIds(store, $$"""{"authors":["{{b}}"],"kinds":[1]}"""));

		// The times count with their ends.
		Assert.Equal([reaction.Id, reply.Id], QueryIds(store, """{"since":200,"until":300}"""));
		Assert.Equal([article.Id], QueryIds(store, """{"since":301}"""));
		Assert.Equal([note.Id], QueryIds(store, """{"until":199}"""));

		// A tag is found by its name and by what follows the name. Names differ by their case.
		Assert.Equal([reaction.Id, reply.Id], QueryIds(store, $$"""{"#e":["{{note.Id}}"]}"""));
		Assert.Equal([note.Id], QueryIds(store, $$"""{"#e":["{{idOfSomething}}"]}"""));
		Assert.Empty(QueryIds(store, """{"#e":["wss://relay.example"]}"""));
		Assert.Equal([article.Id, reply.Id, note.Id], QueryIds(store, """{"#t":["nostr","relay"]}"""));
		Assert.Equal([reaction.Id], QueryIds(store, """{"#T":["upper"]}"""));
		Assert.Empty(QueryIds(store, """{"#t":["upper"]}"""));

		// Every tag that is asked for has to be there.
		Assert.Equal([reply.Id], QueryIds(store, $$"""{"#e":["{{note.Id}}"],"#t":["nostr"]}"""));
		Assert.Equal([reply.Id], QueryIds(store, $$"""{"#p":["{{a}}"]}"""));

		// Nothing meets a filter that nothing can meet.
		Assert.Empty(QueryIds(store, """{"kinds":[]}"""));
		Assert.Empty(QueryIds(store, """{"limit":0}"""));
		Assert.Empty(QueryIds(store, """{"kinds":[2]}"""));

		// What the store hands out for a filter is what the filter says of each event by itself.
		foreach (var json in new[] { "{}", """{"kinds":[1]}""", """{"#t":["relay"]}""", $$"""{"authors":["{{a}}"],"since":150}""" })
		{
			var filter = Filter(json);
			Assert.Equal(
				new[] { article, reaction, reply, note }.Where(filter.Matches).Select(x => x.Id),
				QueryIds(store, json));
		}
	}

	/// <summary>The stored events are handed out page by page, the newest first, and none of them twice.</summary>
	[Fact]
	public void StoredEventsComeInPages()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		// Several events of the same second among them.
		var events = Enumerable.Range(0, 25).Select(i => TestRelayEvent.Create(key, 1, $"note {i}", createdAt: 1_000 + i / 4)).ToArray();

		foreach (var relayEvent in events)
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(relayEvent, null, Now));
		}

		var expected = events.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Id).ToArray();
		var filter = Filter("""{"kinds":[1]}""");
		var paged = new System.Collections.Generic.List<string>();
		StoredEvent? last = null;

		while (true)
		{
			var page = store.Query(filter, Now, pageSize: 7, last);
			paged.AddRange(page.Select(stored => stored.Id));

			if (page.Count < 7)
			{
				break;
			}

			last = page[^1];
		}

		Assert.Equal(expected, paged);
		Assert.Equal(expected[..3], QueryIds(store, pageSize: 3));
	}

	/// <summary>An event that has expired is not handed out any more, and is removed when the store is told to forget what expired.</summary>
	[Fact]
	public void ExpiredEventsAreForgotten()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, RelayLimits.ForBlackHoleEvents);

		var soon = TestRelayEvent.Create(key, 1, "soon");
		var later = TestRelayEvent.Create(key, 1, "later");
		var never = TestRelayEvent.Create(key, 1, "never");

		Assert.Equal(StoreOutcome.Stored, store.Add(soon, Now + 10, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(later, Now + 20, Now));
		Assert.Equal(StoreOutcome.Stored, store.Add(never, null, Now));

		Assert.Equal(3, QueryIds(store, now: Now + 9).Length);
		Assert.Equal(new[] { later.Id, never.Id }.Order(), QueryIds(store, now: Now + 10).Order());
		Assert.Equal(3, store.Count);

		Assert.Equal(1, store.DeleteExpired(Now + 10));
		Assert.Equal(2, store.Count);
		Assert.Equal(later.Size + never.Size, store.Bytes);

		Assert.Equal(1, store.DeleteExpired(Now + 1_000_000));
		Assert.Equal(0, store.DeleteExpired(Now + 1_000_000));
		Assert.Equal([never.Id], QueryIds(store, now: Now + 1_000_000));
	}

	/// <summary>
	/// When the room is used up, one event makes way for a new one: the one that the store has had the longest. What
	/// has expired makes way before that, and an event that takes the place of one that is there needs no room.
	/// </summary>
	[Fact]
	public void EventThatWasKeptTheLongestMakesWayInAFullStore()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredEvents = 3 });

		// The time of an event is what its maker says, and says nothing about when the store got it.
		var notes = Enumerable.Range(0, 6).Select(i => TestRelayEvent.Create(key, 1, $"note {i}", createdAt: 5_000 - i)).ToArray();

		Assert.Equal(StoreOutcome.Stored, store.Add(notes[0], null, now: 100));
		Assert.Equal(StoreOutcome.Stored, store.Add(notes[1], null, now: 101));
		Assert.Equal(StoreOutcome.Stored, store.Add(notes[2], null, now: 102));
		Assert.Equal(StoreOutcome.Stored, store.Add(notes[3], null, now: 103));

		Assert.Equal(new[] { notes[1].Id, notes[2].Id, notes[3].Id }.Order(), QueryIds(store).Order());
		Assert.Equal(3, store.Count);
		Assert.Equal(notes[1..4].Sum(x => (long)x.Size), store.Bytes);

		// One for one, however many came before.
		Assert.Equal(StoreOutcome.Stored, store.Add(notes[4], null, now: 104));
		Assert.Equal(new[] { notes[2].Id, notes[3].Id, notes[4].Id }.Order(), QueryIds(store).Order());

		// What has expired goes first, also if it came last.
		var profile = TestRelayEvent.Create(key, 0, "{}");
		Assert.Equal(StoreOutcome.Stored, store.Add(TestRelayEvent.Create(key, 1, "short-lived"), expiresAt: 200, now: 105));
		Assert.Equal(StoreOutcome.Stored, store.Add(profile, null, now: 200));
		Assert.Equal(new[] { notes[3].Id, notes[4].Id, profile.Id }.Order(), QueryIds(store, now: 200).Order());

		// A newer event of a replaceable kind takes the place of the one before, and nothing else makes way for it.
		var newerProfile = TestRelayEvent.Create(key, 0, "{\"name\":\"x\"}", createdAt: TestRelayEvent.Now + 1);
		Assert.Equal(StoreOutcome.Stored, store.Add(newerProfile, null, now: 201));
		Assert.Equal(new[] { notes[3].Id, notes[4].Id, newerProfile.Id }.Order(), QueryIds(store, now: 201).Order());
		Assert.Equal(3, store.Count);
	}

	/// <summary>The room for events has a limit in bytes as well: as many events make way as it takes.</summary>
	[Fact]
	public void EventsMakeWayForTheBytesOfANewOne()
	{
		using var key = TestRelayEvent.NewKey();
		var small = Enumerable.Range(0, 4).Select(i => TestRelayEvent.Create(key, 1, new string((char)('a' + i), 300))).ToArray();
		var large = TestRelayEvent.Create(key, 1, new string('z', 700));

		using var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredBytes = small.Sum(x => (long)x.Size) + 10 });

		for (var i = 0; i < small.Length; i++)
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(small[i], null, now: 100 + i));
		}

		Assert.Equal(StoreOutcome.Stored, store.Add(large, null, now: 200));

		// Two of the small ones were as many bytes as the large one needed.
		Assert.Equal(new[] { small[2].Id, small[3].Id, large.Id }.Order(), QueryIds(store).Order());
		Assert.Equal(small[2].Size + small[3].Size + large.Size, store.Bytes);
	}

	/// <summary>
	/// The events of the keys with the least behind them make way first, however new they are. An event with less
	/// behind it than everything in a full store is not kept, and the events of the keys of the list go last.
	/// </summary>
	[Fact]
	public void EventsWithTheLeastBehindThemMakeWayFirst()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredEvents = 4 });

		RelayEvent Note(string text) => TestRelayEvent.Create(key, 1, text);

		var large = Note("of a key with a large bond");
		var small = Note("of a key with a small bond");
		var none = Note("of a key without a bond");
		var listed = Note("of a key of the list");

		Assert.Equal(StoreOutcome.Stored, store.Add(listed, null, now: 100, RelaySenderRules.WhitelistRank));
		Assert.Equal(StoreOutcome.Stored, store.Add(large, null, now: 101, rank: 5_000_000));
		Assert.Equal(StoreOutcome.Stored, store.Add(none, null, now: 102));
		Assert.Equal(StoreOutcome.Stored, store.Add(small, null, now: 103, rank: 1_000_000));

		// The newest of them all is the one without a bond, and it is the one that goes.
		var middle = Note("of a key with a bond between the two");
		Assert.Equal(StoreOutcome.Stored, store.Add(middle, null, now: 104, rank: 2_000_000));
		Assert.Equal(new[] { listed.Id, large.Id, small.Id, middle.Id }.Order(), QueryIds(store).Order());

		// Nothing makes way for an event with less behind it than everything that is kept.
		Assert.Equal(StoreOutcome.Full, store.Add(Note("another without a bond"), null, now: 105));
		Assert.Equal(StoreOutcome.Full, store.Add(Note("of a key with a tiny bond"), null, now: 105, rank: 999_999));
		Assert.Equal(4, store.Count);

		// With as much behind it as the least of them, a new event takes the place of the one that was kept the longest.
		var alsoSmall = Note("of another key with a small bond");
		Assert.Equal(StoreOutcome.Stored, store.Add(alsoSmall, null, now: 106, rank: 1_000_000));
		Assert.Equal(new[] { listed.Id, large.Id, middle.Id, alsoSmall.Id }.Order(), QueryIds(store).Order());

		// The largest bonds push out everything but the list, lowest first, and then each other.
		var huge = Enumerable.Range(0, 4).Select(i => Note($"of a key with a huge bond {i}")).ToArray();

		Assert.Equal(StoreOutcome.Stored, store.Add(huge[0], null, now: 107, rank: 9_000_000));
		Assert.Equal(new[] { listed.Id, large.Id, middle.Id, huge[0].Id }.Order(), QueryIds(store).Order());

		Assert.Equal(StoreOutcome.Stored, store.Add(huge[1], null, now: 108, rank: 9_000_000));
		Assert.Equal(StoreOutcome.Stored, store.Add(huge[2], null, now: 109, rank: 9_000_000));
		Assert.Equal(new[] { listed.Id, huge[0].Id, huge[1].Id, huge[2].Id }.Order(), QueryIds(store).Order());

		Assert.Equal(StoreOutcome.Stored, store.Add(huge[3], null, now: 110, rank: 9_000_000));
		Assert.Equal(new[] { listed.Id, huge[1].Id, huge[2].Id, huge[3].Id }.Order(), QueryIds(store).Order());

		// Only an event of a key of the list makes one of its kind go.
		var alsoListed = Note("of another key of the list");
		Assert.Equal(StoreOutcome.Stored, store.Add(alsoListed, null, now: 111, RelaySenderRules.WhitelistRank));
		Assert.Contains(listed.Id, QueryIds(store));
		Assert.DoesNotContain(huge[1].Id, QueryIds(store));
	}

	/// <summary>
	/// The store keeps the book of the fidelity bonds. The keys that have shown the same bond share what it holds in
	/// equal parts, and their events stand for that share. Where a key needs a share of a certain size, a bond stands
	/// behind as many keys as it holds such shares: the ones that showed it last.
	/// </summary>
	[Fact]
	public void KeysShareTheBondThatTheyShow()
	{
		using var alice = TestRelayEvent.NewKey();
		using var bob = TestRelayEvent.NewKey();
		using var carol = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredEvents = 2 });

		var a = TestRelayEvent.GetPublicKey(alice);
		var b = TestRelayEvent.GetPublicKey(bob);
		var c = TestRelayEvent.GetPublicKey(carol);

		Assert.Null(store.GetSender(a));

		// The first key has the bond to itself.
		Assert.Equal(3_000_000, store.Join(a, "bond1", amount: 3_000_000, unlockHeight: 900_000, requiredShare: 1_000_000, now: 100));
		Assert.Equal(new SenderBond("bond1", 3_000_000, 900_000, Share: 3_000_000), store.GetSender(a));

		var ofAlice = TestRelayEvent.Create(alice, 1, "of the first key");
		Assert.Equal(StoreOutcome.Stored, store.Add(ofAlice, null, now: 100, rank: 3_000_000));

		// With a second key each has half, and the event that is there stands for the half from now on.
		Assert.Equal(1_500_000, store.Join(b, "bond1", 3_000_000, 900_000, requiredShare: 1_000_000, now: 101));
		Assert.Equal(1_500_000, store.GetSender(a)!.Share);

		var ofBob = TestRelayEvent.Create(bob, 1, "of the second key");
		Assert.Equal(StoreOutcome.Stored, store.Add(ofBob, null, now: 101, rank: 1_500_000));
		Assert.Equal(StoreOutcome.Full, store.Add(TestRelayEvent.Create(carol, 1, "below the two halves"), null, now: 102, rank: 1_499_999));
		Assert.Equal(StoreOutcome.Stored, store.Add(TestRelayEvent.Create(carol, 1, "level with the two halves"), null, now: 102, rank: 1_500_000));
		Assert.DoesNotContain(ofAlice.Id, QueryIds(store));

		// A third key has a third. Now the bond holds no further share of the size that a key needs.
		Assert.Equal(1_000_000, store.Join(c, "bond1", 3_000_000, 900_000, requiredShare: 1_000_000, now: 103));
		Assert.Equal(1_000_000, store.GetSender(a)!.Share);
		Assert.Contains(ofBob.Id, QueryIds(store));

		// For a fourth key, the one that the store heard of the longest ago is struck, and its events go with it. The
		// first key was heard of again since, so that is the second.
		store.Touch(a, now: 104);

		using var dave = TestRelayEvent.NewKey();
		var d = TestRelayEvent.GetPublicKey(dave);

		Assert.Equal(1_000_000, store.Join(d, "bond1", 3_000_000, 900_000, requiredShare: 1_000_000, now: 105));
		Assert.Null(store.GetSender(b));
		Assert.DoesNotContain(ofBob.Id, QueryIds(store));
		Assert.Equal(1, store.Count);
		Assert.Equal(1_000_000, store.GetSender(a)!.Share);
		Assert.Equal(1_000_000, store.GetSender(c)!.Share);

		// A key that shows the bond again is the key that it was.
		Assert.Equal(1_000_000, store.Join(a, "bond1", 3_000_000, 900_000, requiredShare: 1_000_000, now: 106));
		Assert.NotNull(store.GetSender(c));
		Assert.NotNull(store.GetSender(d));

		// A bond that holds less than one key needs stands behind none.
		using var erin = TestRelayEvent.NewKey();
		Assert.Null(store.Join(TestRelayEvent.GetPublicKey(erin), "tiny", 999_999, 900_000, requiredShare: 1_000_000, now: 107));
		Assert.Null(store.GetSender(TestRelayEvent.GetPublicKey(erin)));

		// A key that shows another bond leaves the first, and the keys that stay have more of it each.
		Assert.Equal(700_000, store.Join(c, "bond2", 700_000, 950_000, requiredShare: 0, now: 108));
		Assert.Equal(new SenderBond("bond2", 700_000, 950_000, Share: 700_000), store.GetSender(c));
		Assert.Equal(1_500_000, store.GetSender(a)!.Share);
		Assert.Equal(1_500_000, store.GetSender(d)!.Share);

		// Where a key needs no bond, a bond is shared by as many keys as show it.
		foreach (var i in Enumerable.Range(0, 5))
		{
			using var another = TestRelayEvent.NewKey();
			Assert.Equal(700_000 / (i + 2), store.Join(TestRelayEvent.GetPublicKey(another), "bond2", 700_000, 950_000, requiredShare: 0, now: 109));
		}

		Assert.Equal(700_000 / 6, store.GetSender(c)!.Share);
	}

	/// <summary>
	/// A bond stands behind a key for as long as the relay keeps events of the key, and for as long as it is locked.
	/// After that the key is struck from the book, and the keys that shared the bond have more of it each.
	/// </summary>
	[Fact]
	public void BondNoLongerStandsBehindAKeyOnceItLapsed()
	{
		using var alice = TestRelayEvent.NewKey();
		using var bob = TestRelayEvent.NewKey();
		using var carol = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		var a = TestRelayEvent.GetPublicKey(alice);
		var b = TestRelayEvent.GetPublicKey(bob);
		var c = TestRelayEvent.GetPublicKey(carol);
		const long Day = 86_400;

		store.Join(a, "shared", 2_000_000, unlockHeight: 900_000, requiredShare: 0, now: 1_000);
		store.Join(b, "shared", 2_000_000, unlockHeight: 900_000, requiredShare: 0, now: 2_000);
		store.Join(c, "soon", 500_000, unlockHeight: 800_010, requiredShare: 0, now: 2_000);

		var ofAlice = TestRelayEvent.Create(alice, 1, "of the first key");
		var ofBob = TestRelayEvent.Create(bob, 1, "of the second key");
		var ofCarol = TestRelayEvent.Create(carol, 1, "of the third key");

		store.Add(ofAlice, null, now: 1_000, rank: 1_000_000);
		store.Add(ofBob, null, now: 2_000, rank: 1_000_000);
		store.Add(ofCarol, null, now: 2_000, rank: 500_000);

		// Nothing has lapsed yet: no key was silent for a day, and no bond has unlocked.
		Assert.Equal(0, store.ForgetSenders(now: 1_000 + Day - 1, Day, tipHeight: 800_009, dropEvents: false));
		Assert.Equal(0, store.ForgetSenders(now: 1_000 + Day - 1, Day, tipHeight: null, dropEvents: false));

		// The relay heard of the second key later, so it stays, with the whole bond.
		store.Touch(b, now: 50_000);
		Assert.Equal(1, store.ForgetSenders(now: 1_000 + Day, Day, tipHeight: 800_009, dropEvents: false));
		Assert.Null(store.GetSender(a));
		Assert.Equal(2_000_000, store.GetSender(b)!.Share);

		// The event of the key that was struck stays where no bond is needed, with nothing behind it.
		Assert.Equal(3, QueryIds(store).Length);

		// A bond that has unlocked can be spent. Where a key needs a bond, the events of its key go with it.
		Assert.Equal(1, store.ForgetSenders(now: 60_000, Day, tipHeight: 800_010, dropEvents: true));
		Assert.Null(store.GetSender(c));
		Assert.Equal(new[] { ofAlice.Id, ofBob.Id }.Order(), QueryIds(store).Order());
		Assert.Equal(ofAlice.Size + ofBob.Size, store.Bytes);
	}

	/// <summary>
	/// A relay that starts with other rules than it had brings what it kept in line with them: where a key needs a
	/// bond now, the events of keys without enough behind them are dropped, a bond stands behind as many keys as it
	/// can carry, and the keys of the list need none.
	/// </summary>
	[Fact]
	public async Task StoreFollowsTheRulesThatTheRelayStartsWithAsync()
	{
		var filePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "Events.sqlite");
		var keys = Enumerable.Range(0, 6).Select(_ => TestRelayEvent.NewKey()).ToArray();

		try
		{
			var names = keys.Select(TestRelayEvent.GetPublicKey).ToArray();
			var events = keys.Select((key, i) => TestRelayEvent.Create(key, 1, $"of key {i}")).ToArray();

			using (var store = RelayEventStore.Open(filePath, Limits))
			{
				// Three keys share a bond of 3,000,000, one has a bond of 400,000, and two have none.
				store.Join(names[0], "shared", 3_000_000, 900_000, requiredShare: 0, now: 100);
				store.Join(names[1], "shared", 3_000_000, 900_000, requiredShare: 0, now: 200);
				store.Join(names[2], "shared", 3_000_000, 900_000, requiredShare: 0, now: 300);
				store.Join(names[3], "small", 400_000, 900_000, requiredShare: 0, now: 100);

				for (var i = 0; i < events.Length; i++)
				{
					store.Add(events[i], null, now: 100 + i, rank: store.GetSender(names[i])?.Share ?? 0);
				}

				// Anybody's events are taken: nothing changes.
				store.Reconcile(RelaySenderRules.Anyone);
				Assert.Equal(6, store.Count);
			}

			// A bond of 1,200,000 or more for each: the shared bond carries two keys, the ones that were heard of last.
			var rules = new RelaySenderRules(IsBondRequired: true, MinBond: 1_200_000, Whitelist: [names[5]]);

			using (var store = RelayEventStore.Open(filePath, Limits))
			{
				store.Reconcile(rules);

				Assert.Null(store.GetSender(names[0]));
				Assert.Equal(1_500_000, store.GetSender(names[1])!.Share);
				Assert.Equal(1_500_000, store.GetSender(names[2])!.Share);
				Assert.Null(store.GetSender(names[3]));
				Assert.Equal(new[] { events[1].Id, events[2].Id, events[5].Id }.Order(), QueryIds(store).Order());
				Assert.Equal(events[1].Size + events[2].Size + events[5].Size, store.Bytes);
			}

			// The events of the key of the list are the last to go, and the two halves of the bond are level.
			using (var store = RelayEventStore.Open(filePath, Limits with { MaxStoredEvents = 3 }))
			{
				store.Reconcile(rules);

				Assert.Equal(StoreOutcome.Full, store.Add(TestRelayEvent.Create(keys[0], 1, "with less behind it"), null, now: 500, rank: 1_499_999));
				Assert.Equal(StoreOutcome.Stored, store.Add(TestRelayEvent.Create(keys[0], 1, "with as much behind it"), null, now: 500, rank: 1_500_000));
				Assert.DoesNotContain(events[1].Id, QueryIds(store));
				Assert.Contains(events[5].Id, QueryIds(store));
			}

			// Without the list, the key that was on it is a key without a bond.
			using (var store = RelayEventStore.Open(filePath, Limits))
			{
				store.Reconcile(rules with { Whitelist = [] });

				Assert.DoesNotContain(events[5].Id, QueryIds(store));
			}
		}
		finally
		{
			foreach (var key in keys)
			{
				key.Dispose();
			}
		}
	}

	/// <summary>
	/// A file that an earlier version of the relay wrote has no place for what stands behind an event. It is emptied
	/// and used: what it had was a day old at most by the rules of now.
	/// </summary>
	[Fact]
	public async Task FileOfAnEarlierVersionIsEmptiedAndUsedAsync()
	{
		using var key = TestRelayEvent.NewKey();
		var filePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "Events.sqlite");

		using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={filePath};Pooling=False"))
		{
			connection.Open();

			using var command = connection.CreateCommand();
			command.CommandText =
				"""
				CREATE TABLE event (id TEXT NOT NULL PRIMARY KEY, pubkey TEXT NOT NULL, created_at INTEGER NOT NULL, kind INTEGER NOT NULL, address TEXT, expires_at INTEGER, size INTEGER NOT NULL, json TEXT NOT NULL) WITHOUT ROWID;
				CREATE TABLE tag (name TEXT NOT NULL, value TEXT NOT NULL, event_id TEXT NOT NULL REFERENCES event (id) ON DELETE CASCADE, PRIMARY KEY (name, value, event_id)) WITHOUT ROWID;
				INSERT INTO event VALUES ('old', 'key', 1, 1, NULL, NULL, 2, '{}');
				""";
			command.ExecuteNonQuery();
		}

		var note = TestRelayEvent.Create(key, 1, "of the version of now", [["t", "tag"]]);

		using (var store = RelayEventStore.Open(filePath, Limits))
		{
			Assert.Equal(0, store.Count);
			Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now, rank: 5));
		}

		// A file of this version is kept as it is.
		using (var store = RelayEventStore.Open(filePath, Limits))
		{
			Assert.Equal([note.Id], QueryIds(store, """{"#t":["tag"]}"""));
		}
	}

	/// <summary>A store in a file has its events again when it is opened again. A store in memory starts empty every time.</summary>
	[Fact]
	public async Task EventsInAFileAreStillThereAfterTheStoreWasClosedAsync()
	{
		using var key = TestRelayEvent.NewKey();
		var filePath = Path.Combine(await Common.GetEmptyWorkDirAsync(), "NostrRelay", "Events.sqlite");
		var note = TestRelayEvent.Create(key, 1, "kept", [["t", "tag"]]);
		var profile = TestRelayEvent.Create(key, 0, "{}");

		using (var store = RelayEventStore.Open(filePath, Limits))
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(profile, null, Now));
		}

		Assert.True(File.Exists(filePath));

		using (var store = RelayEventStore.Open(filePath, Limits))
		{
			Assert.Equal(2, store.Count);
			Assert.Equal(note.Size + profile.Size, store.Bytes);
			Assert.Equal([note.Id], QueryIds(store, """{"#t":["tag"]}"""));
			Assert.Equal(StoreOutcome.Duplicate, store.Add(note, null, Now));
		}

		// With a list of kinds, the store hands out those kinds only. What it keeps of other kinds stays in the file.
		Assert.True(KindList.TryParse("0", out var onlyProfiles, out _));

		using (var store = RelayEventStore.Open(filePath, Limits, servedKinds: onlyProfiles))
		{
			Assert.Equal([profile.Id], QueryIds(store));
			Assert.Empty(QueryIds(store, $$"""{"ids":["{{note.Id}}"]}"""));
			Assert.Equal(2, store.Count);
		}

		using (var store = RelayEventStore.Open(filePath, Limits, servedKinds: KindList.Empty))
		{
			Assert.Empty(QueryIds(store));
		}

		using (var store = RelayEventStore.Open(filePath, Limits))
		{
			Assert.Equal(2, QueryIds(store).Length);
		}

		using (var inMemory = RelayEventStore.Open(filePath: null, Limits))
		{
			Assert.Equal(0, inMemory.Count);
		}
	}

	/// <summary>A tag with a very long value is kept with its event, and cannot be asked for.</summary>
	[Fact]
	public void TagWithAVeryLongValueIsNotFound()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits);

		var atTheLimit = new string('x', RelayEventStore.MaxIndexedTagValueLength);
		var beyond = new string('y', RelayEventStore.MaxIndexedTagValueLength + 1);
		var note = TestRelayEvent.Create(key, 1, "text", [["t", atTheLimit], ["r", beyond], ["t", atTheLimit]]);

		Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
		Assert.Equal([note.Id], QueryIds(store, $$"""{"#t":["{{atTheLimit}}"]}"""));
		Assert.Empty(QueryIds(store, $$"""{"#r":["{{beyond}}"]}"""));
		Assert.Contains(beyond, Assert.Single(store.Query(Filter("{}"), Now, 1)).Json, StringComparison.Ordinal);
	}

	[Fact]
	public void StoreThatWasClosedIsNotUsed()
	{
		using var key = TestRelayEvent.NewKey();
		var store = RelayEventStore.Open(filePath: null, Limits);

		store.Dispose();
		store.Dispose();

		Assert.Throws<ObjectDisposedException>(() => store.Add(TestRelayEvent.Create(key, 1, "text"), null, Now));
		Assert.Throws<ObjectDisposedException>(() => store.Query(Filter("{}"), Now, 1));
		Assert.Throws<ObjectDisposedException>(() => store.DeleteExpired(Now));
	}
}
