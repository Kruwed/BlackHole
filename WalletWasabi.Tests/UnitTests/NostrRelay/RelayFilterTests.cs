using System.Linq;
using System.Text.Json;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>What a client can ask a relay for, and which events answer it.</summary>
public class RelayFilterTests
{
	private static readonly RelayLimits Limits = RelayLimits.ForCustomEvents;

	private static readonly string IdA = new('a', 64);
	private static readonly string IdB = new('b', 64);

	private static RelayFilter Parse(string json, RelayLimits? limits = null)
	{
		using var document = JsonDocument.Parse(json);

		return RelayFilter.TryParse(document.RootElement, limits ?? Limits, out var filter, out var problem)
			? filter
			: throw new InvalidOperationException(problem);
	}

	private static string? GetProblem(string json, RelayLimits? limits = null)
	{
		using var document = JsonDocument.Parse(json);

		return RelayFilter.TryParse(document.RootElement, limits ?? Limits, out _, out var problem) ? null : problem;
	}

	/// <summary>A filter names what it asks for, and what it does not name is left open.</summary>
	[Fact]
	public void FilterIsRead()
	{
		var filter = Parse($$"""{"ids":["{{IdA}}"],"authors":["{{IdB}}","{{IdB}}"],"kinds":[1,15750],"#e":["{{IdA}}"],"#t":["nostr","relay"],"since":100,"until":200,"limit":5}""");

		Assert.Equal([IdA], filter.Ids!);
		Assert.Equal([IdB], filter.Authors!);
		Assert.Equal([1, 15750], filter.Kinds!.Order());
		Assert.Equal(["e", "t"], filter.Tags.Keys.Order());
		Assert.Equal(["nostr", "relay"], filter.Tags["t"].Order());
		Assert.Equal(100, filter.Since);
		Assert.Equal(200, filter.Until);
		Assert.Equal(5, filter.Limit);
		Assert.False(filter.IsEmpty);

		var open = Parse("{}");

		Assert.Null(open.Ids);
		Assert.Null(open.Authors);
		Assert.Null(open.Kinds);
		Assert.Empty(open.Tags);
		Assert.Null(open.Since);
		Assert.Null(open.Until);
		Assert.Null(open.Limit);
		Assert.False(open.IsEmpty);
	}

	/// <summary>The request of the application's own order book is one that the relay answers.</summary>
	[Fact]
	public void RequestOfTheOrderBookIsRead()
	{
		var filter = Parse("""{"kinds":[15750],"since":1795520700}""", RelayLimits.ForBlackHoleEvents);

		Assert.Equal([15750], filter.Kinds!);
		Assert.Equal(1795520700, filter.Since);
	}

	/// <summary>
	/// A filter with something in it that the relay does not know is not answered: passing over that part would
	/// answer with more than was asked for.
	/// </summary>
	[Theory]
	[InlineData("""{"search":"bitcoin"}""")]
	[InlineData("""{"kinds":[1],"unknown":true}""")]
	[InlineData("""{"#ee":["x"]}""")]
	[InlineData("""{"#":["x"]}""")]
	[InlineData("""{"#1":["x"]}""")]
	[InlineData("""{"e":["x"]}""")]
	public void FilterWithUnknownPartsIsNotAnswered(string json)
	{
		Assert.Equal("unsupported: the filter contains unknown elements", GetProblem(json));
	}

	[Theory]
	[InlineData("[]", "invalid: a filter is an object")]
	[InlineData("5", "invalid: a filter is an object")]
	[InlineData("""{"ids":"x"}""", "invalid: the ids of a filter")]
	[InlineData("""{"ids":["short"]}""", "invalid: the ids of a filter")]
	[InlineData("""{"ids":[5]}""", "invalid: the ids of a filter")]
	[InlineData("""{"ids":["AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"]}""", "invalid: the ids of a filter")]
	[InlineData("""{"ids":[],"ids":[]}""", "invalid: the ids of a filter")]
	[InlineData("""{"authors":["short"]}""", "invalid: the authors of a filter")]
	[InlineData("""{"authors":{}}""", "invalid: the authors of a filter")]
	[InlineData("""{"kinds":[-1]}""", "invalid: the kinds of a filter")]
	[InlineData("""{"kinds":[65536]}""", "invalid: the kinds of a filter")]
	[InlineData("""{"kinds":["1"]}""", "invalid: the kinds of a filter")]
	[InlineData("""{"kinds":[1.5]}""", "invalid: the kinds of a filter")]
	[InlineData("""{"kinds":1}""", "invalid: the kinds of a filter")]
	[InlineData("""{"since":"100"}""", "invalid: the since of a filter")]
	[InlineData("""{"since":-1}""", "invalid: the since of a filter")]
	[InlineData("""{"until":1.5}""", "invalid: the until of a filter")]
	[InlineData("""{"until":null}""", "invalid: the until of a filter")]
	[InlineData("""{"limit":-1}""", "invalid: the limit of a filter")]
	[InlineData("""{"limit":"5"}""", "invalid: the limit of a filter")]
	[InlineData("""{"limit":99999999999}""", "invalid: the limit of a filter")]
	[InlineData("""{"#e":"x"}""", "invalid: the values of a tag in a filter")]
	[InlineData("""{"#e":[5]}""", "invalid: the values of a tag in a filter")]
	[InlineData("""{"#e":["x"],"#e":["y"]}""", "invalid: the values of a tag in a filter")]
	[InlineData("""{"#e":["half a pair \ud83d"]}""", "invalid: the values of a tag in a filter")]
	public void WhatIsNoFilterIsRefused(string json, string expectedProblemStart)
	{
		Assert.StartsWith(expectedProblemStart, GetProblem(json), StringComparison.Ordinal);
	}

	/// <summary>A filter lists no more values than the relay says.</summary>
	[Fact]
	public void FilterListsNoMoreValuesThanTheLimit()
	{
		var limits = Limits with { MaxFilterValues = 3 };

		Assert.Null(GetProblem("""{"kinds":[1,2,3],"#t":["a","b","c"]}""", limits));
		Assert.StartsWith("invalid: the kinds of a filter are a list of up to 3 numbers", GetProblem("""{"kinds":[1,2,3,4]}""", limits), StringComparison.Ordinal);
		Assert.StartsWith("invalid: the values of a tag in a filter are a list of up to 3 texts", GetProblem("""{"#t":["a","b","c","d"]}""", limits), StringComparison.Ordinal);
		Assert.StartsWith("invalid: the ids of a filter are a list of up to 3 ids", GetProblem($$"""{"ids":["{{IdA}}","{{IdA}}","{{IdA}}","{{IdA}}"]}""", limits), StringComparison.Ordinal);
	}

	/// <summary>A filter that no event can meet is told from one that asks for everything.</summary>
	[Theory]
	[InlineData("""{"ids":[]}""")]
	[InlineData("""{"authors":[]}""")]
	[InlineData("""{"kinds":[]}""")]
	[InlineData("""{"#e":[]}""")]
	[InlineData("""{"limit":0}""")]
	[InlineData("""{"since":200,"until":100}""")]
	public void FilterThatNothingMeetsIsEmpty(string json)
	{
		Assert.True(Parse(json).IsEmpty);
	}

	/// <summary>An event has to meet everything that a filter names. Of the values that a filter lists for one thing, one is enough.</summary>
	[Fact]
	public void EventMeetsAFilterIfItMeetsAllOfIt()
	{
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();

		var note = TestRelayEvent.Create(key, kind: 1, "text", [["e", IdA, "wss://relay.example"], ["t", "nostr"], ["t", "relay"], ["alone"]], createdAt: 150);
		var author = TestRelayEvent.GetPublicKey(key);
		var otherAuthor = TestRelayEvent.GetPublicKey(otherKey);

		Assert.True(Parse("{}").Matches(note));
		Assert.True(Parse($$"""{"ids":["{{note.Id}}","{{IdB}}"]}""").Matches(note));
		Assert.False(Parse($$"""{"ids":["{{IdB}}"]}""").Matches(note));
		Assert.True(Parse($$"""{"authors":["{{otherAuthor}}","{{author}}"]}""").Matches(note));
		Assert.False(Parse($$"""{"authors":["{{otherAuthor}}"]}""").Matches(note));
		Assert.True(Parse("""{"kinds":[0,1]}""").Matches(note));
		Assert.False(Parse("""{"kinds":[0,2]}""").Matches(note));

		// The times count with their ends.
		Assert.True(Parse("""{"since":150,"until":150}""").Matches(note));
		Assert.False(Parse("""{"since":151}""").Matches(note));
		Assert.False(Parse("""{"until":149}""").Matches(note));

		// A tag is found by its name and by what follows the name, and by nothing further back in it.
		Assert.True(Parse($$"""{"#e":["{{IdA}}"]}""").Matches(note));
		Assert.False(Parse("""{"#e":["wss://relay.example"]}""").Matches(note));
		Assert.True(Parse("""{"#t":["other","relay"]}""").Matches(note));
		Assert.False(Parse("""{"#t":["other"]}""").Matches(note));
		Assert.False(Parse("""{"#T":["nostr"]}""").Matches(note));
		Assert.False(Parse("""{"#p":["anything"]}""").Matches(note));

		// Every tag that is asked for has to be there.
		Assert.True(Parse($$"""{"#e":["{{IdA}}"],"#t":["nostr"]}""").Matches(note));
		Assert.False(Parse($$"""{"#e":["{{IdA}}"],"#t":["other"]}""").Matches(note));

		// And everything together.
		Assert.True(Parse($$"""{"authors":["{{author}}"],"kinds":[1],"#t":["nostr"],"since":100,"until":200,"limit":0}""").Matches(note));
		Assert.False(Parse($$"""{"authors":["{{author}}"],"kinds":[2],"#t":["nostr"],"since":100,"until":200}""").Matches(note));
	}
}
