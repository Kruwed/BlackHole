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
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);
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
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

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
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

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
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

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
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

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
		using var store = RelayEventStore.Open(filePath: null, RelayLimits.ForBlackHoleEvents, dropsOldestWhenFull: false);

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
	/// When the room is used up and nothing makes way, a new event is refused, and what is there stays. An event that
	/// takes the place of one that is there needs no new room, and what has expired makes way.
	/// </summary>
	[Fact]
	public void FullStoreKeepsWhatItHas()
	{
		using var store = RelayEventStore.Open(filePath: null, RelayLimits.ForBlackHoleEvents with { MaxStoredEvents = 3 }, dropsOldestWhenFull: false);

		var keys = Enumerable.Range(0, 5).Select(_ => TestRelayEvent.NewKey()).ToArray();

		try
		{
			var offers = keys.Select((key, i) => TestRelayEvent.Create(key, 15750, $"offer {i}", createdAt: Now - 100 + i)).ToArray();

			Assert.Equal(StoreOutcome.Stored, store.Add(offers[0], Now + 100, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(offers[1], Now + 200, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(offers[2], Now + 200, Now));
			Assert.Equal(StoreOutcome.Full, store.Add(offers[3], Now + 200, Now));
			Assert.Equal(3, store.Count);
			Assert.Equal(offers[..3].Sum(x => (long)x.Size), store.Bytes);

			// A maker that is there posts its offer again.
			var again = TestRelayEvent.Create(keys[1], 15750, "offer 1 again", createdAt: Now);
			Assert.Equal(StoreOutcome.Stored, store.Add(again, Now + 300, Now));
			Assert.Equal(3, store.Count);

			// The first offer has expired by now, so there is room for another.
			Assert.Equal(StoreOutcome.Stored, store.Add(offers[3], Now + 300, Now + 100));
			Assert.Equal(StoreOutcome.Full, store.Add(offers[4], Now + 300, Now + 100));
			Assert.Equal(new[] { again.Id, offers[2].Id, offers[3].Id }.Order(), QueryIds(store, now: Now + 100).Order());
			Assert.Equal(3, store.Count);
			Assert.Equal(again.Size + offers[2].Size + offers[3].Size, store.Bytes);
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
	/// When the room is used up and the oldest events make way, the store keeps the newest events, by number and by
	/// bytes: a new event pushes the oldest out, and an event that is older than everything that is kept is not kept.
	/// </summary>
	[Fact]
	public void OldestEventsMakeWayInAFullStore()
	{
		using var key = TestRelayEvent.NewKey();

		using (var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredEvents = 5 }, dropsOldestWhenFull: true))
		{
			var notes = Enumerable.Range(0, 10).Select(i => TestRelayEvent.Create(key, 1, $"note {i}", createdAt: 1_000 + i)).ToArray();

			foreach (var note in notes[2..7])
			{
				Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
			}

			// The oldest is not the first that came: what is older than everything in a full store finds no room.
			Assert.Equal(StoreOutcome.Full, store.Add(notes[1], null, Now));
			Assert.Equal(StoreOutcome.Full, store.Add(notes[0], null, Now));
			Assert.Equal(notes[2..7].Reverse().Select(x => x.Id), QueryIds(store));

			// What is newer pushes the oldest out, one for one.
			Assert.Equal(StoreOutcome.Stored, store.Add(notes[9], null, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(notes[7], null, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(notes[8], null, Now));

			Assert.Equal(5, store.Count);
			Assert.Equal(notes[5..].Reverse().Select(x => x.Id), QueryIds(store));
			Assert.Equal(notes[5..].Sum(x => (long)x.Size), store.Bytes);
		}

		var large = Enumerable.Range(0, 4).Select(i => TestRelayEvent.Create(key, 1, new string((char)('a' + i), 1_000), createdAt: 1_000 + i)).ToArray();

		using (var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredBytes = large[0].Size * 2 + 100 }, dropsOldestWhenFull: true))
		{
			foreach (var note in large)
			{
				Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
			}

			Assert.Equal([large[3].Id, large[2].Id], QueryIds(store));
			Assert.Equal(large[2].Size + large[3].Size, store.Bytes);
		}

		// Of a large store one event in a hundred makes way at a time, and not more than are older than the new one.
		using (var store = RelayEventStore.Open(filePath: null, Limits with { MaxStoredEvents = 300 }, dropsOldestWhenFull: true))
		{
			var notes = Enumerable.Range(0, 302).Select(i => TestRelayEvent.Create(key, 1, $"note {i}", createdAt: 1_000 + i)).ToArray();

			foreach (var note in notes[..300])
			{
				Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
			}

			Assert.Equal(StoreOutcome.Stored, store.Add(notes[300], null, Now));
			Assert.Equal(298, store.Count);
			Assert.Equal(notes[3].Id, QueryIds(store)[^1]);

			// With the store full again, an event that is older than all but one of them pushes that one out, and no more.
			Assert.Equal(StoreOutcome.Stored, store.Add(notes[301], null, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(TestRelayEvent.Create(key, 1, "one more", createdAt: 2_000), null, Now));
			Assert.Equal(300, store.Count);

			var asOldAsTheOldest = TestRelayEvent.Create(key, 1, "as old as the oldest", createdAt: notes[3].CreatedAt);
			Assert.Equal(StoreOutcome.Stored, store.Add(asOldAsTheOldest, null, Now));
			Assert.Equal(300, store.Count);
			Assert.Equal(asOldAsTheOldest.Id, QueryIds(store)[^1]);
			Assert.Equal(notes[4].Id, QueryIds(store)[^2]);

			Assert.Equal(StoreOutcome.Full, store.Add(notes[2], null, Now));
			Assert.Equal(300, store.Count);
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

		using (var store = RelayEventStore.Open(filePath, Limits, dropsOldestWhenFull: true))
		{
			Assert.Equal(StoreOutcome.Stored, store.Add(note, null, Now));
			Assert.Equal(StoreOutcome.Stored, store.Add(profile, null, Now));
		}

		Assert.True(File.Exists(filePath));

		using (var store = RelayEventStore.Open(filePath, Limits, dropsOldestWhenFull: true))
		{
			Assert.Equal(2, store.Count);
			Assert.Equal(note.Size + profile.Size, store.Bytes);
			Assert.Equal([note.Id], QueryIds(store, """{"#t":["tag"]}"""));
			Assert.Equal(StoreOutcome.Duplicate, store.Add(note, null, Now));
		}

		// With a list of kinds, the store hands out those kinds only. What it keeps of other kinds stays in the file.
		Assert.True(KindList.TryParse("0", out var onlyProfiles, out _));

		using (var store = RelayEventStore.Open(filePath, Limits, dropsOldestWhenFull: true, servedKinds: onlyProfiles))
		{
			Assert.Equal([profile.Id], QueryIds(store));
			Assert.Empty(QueryIds(store, $$"""{"ids":["{{note.Id}}"]}"""));
			Assert.Equal(2, store.Count);
		}

		using (var store = RelayEventStore.Open(filePath, Limits, dropsOldestWhenFull: true, servedKinds: KindList.Empty))
		{
			Assert.Empty(QueryIds(store));
		}

		using (var store = RelayEventStore.Open(filePath, Limits, dropsOldestWhenFull: true))
		{
			Assert.Equal(2, QueryIds(store).Length);
		}

		using (var inMemory = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true))
		{
			Assert.Equal(0, inMemory.Count);
		}
	}

	/// <summary>A tag with a very long value is kept with its event, and cannot be asked for.</summary>
	[Fact]
	public void TagWithAVeryLongValueIsNotFound()
	{
		using var key = TestRelayEvent.NewKey();
		using var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

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
		var store = RelayEventStore.Open(filePath: null, Limits, dropsOldestWhenFull: true);

		store.Dispose();
		store.Dispose();

		Assert.Throws<ObjectDisposedException>(() => store.Add(TestRelayEvent.Create(key, 1, "text"), null, Now));
		Assert.Throws<ObjectDisposedException>(() => store.Query(Filter("{}"), Now, 1));
		Assert.Throws<ObjectDisposedException>(() => store.DeleteExpired(Now));
	}
}
