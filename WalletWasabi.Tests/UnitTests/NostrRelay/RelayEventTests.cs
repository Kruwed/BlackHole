using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// What the relay takes for an event: exactly the fields of an event, an id that is the hash of what the event says,
/// and the signature of the key that it names. Anybody can send anything, so everything else is refused.
/// </summary>
public class RelayEventTests
{
	private static readonly RelayLimits Limits = RelayLimits.ForCustomEvents;

	private static bool TryParse(string json, out RelayEvent? relayEvent, out string? problem, RelayLimits? limits = null)
	{
		using var document = JsonDocument.Parse(json);
		return RelayEvent.TryParse(document.RootElement, limits ?? Limits, out relayEvent, out problem);
	}

	/// <summary>An event that a client made is read with all that it says, and handed on with the seven fields of an event.</summary>
	[Fact]
	public void EventOfAClientIsRead()
	{
		using var key = TestRelayEvent.NewKey();
		string[][] tags = [["e", new string('a', 64), "wss://relay.example"], ["p", new string('b', 64)], ["alone"]];
		var json = TestRelayEvent.CreateJson(key, kind: 1, content: "Hello, relay.", tags);

		Assert.True(TryParse(json, out var relayEvent, out var problem), problem);
		Assert.NotNull(relayEvent);

		Assert.Equal(TestRelayEvent.GetPublicKey(key), relayEvent.PublicKey);
		Assert.Equal(TestRelayEvent.Now, relayEvent.CreatedAt);
		Assert.Equal(1, relayEvent.Kind);
		Assert.Equal("Hello, relay.", relayEvent.Content);
		Assert.Equal(tags, relayEvent.Tags.Select(tag => tag.ToArray()));
		Assert.Equal(new string('a', 64), relayEvent.GetTagValue("e"));
		Assert.Null(relayEvent.GetTagValue("alone"));
		Assert.Null(relayEvent.GetTagValue("x"));

		// What is handed on is what came, as the same event.
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(relayEvent.Json)));
		Assert.Equal(Encoding.UTF8.GetByteCount(relayEvent.Json), relayEvent.Size);
		Assert.True(TryParse(relayEvent.Json, out var again, out _));
		Assert.Equal(relayEvent.Id, again!.Id);
	}

	/// <summary>What a client adds to an event beyond its seven fields is not handed on: nothing vouches for it.</summary>
	[Fact]
	public void WhatIsNoFieldOfAnEventIsLeftOut()
	{
		using var key = TestRelayEvent.NewKey();
		var json = JsonNode.Parse(TestRelayEvent.CreateJson(key, kind: 1, content: "text"))!.AsObject();
		json["relay"] = "wss://relay.example";
		json["extra"] = new JsonObject { ["nested"] = new JsonArray(1, 2, 3) };

		Assert.True(TryParse(json.ToJsonString(), out var relayEvent, out var problem), problem);

		var handedOn = JsonNode.Parse(relayEvent!.Json)!.AsObject();

		Assert.Equal(["content", "created_at", "id", "kind", "pubkey", "sig", "tags"], handedOn.Select(field => field.Key).Order());
	}

	/// <summary>
	/// The id is the hash of the event written in one certain way. Seven characters are written with a backslash,
	/// and everything else as it is: letters of any script, signs that JSON could also write otherwise, and emoji.
	/// </summary>
	[Fact]
	public void IdIsTheHashOfTheEventWrittenTheOneWay()
	{
		var publicKey = new string('a', 64);
		ImmutableArray<ImmutableArray<string>> tags = [["t", "a \"quoted\" tag"], ["empty", ""]];
		var content = "line\nbreak \"quote\" back\\slash return\r tab\t backspace\b feed\f <html> & 'apostrophe' + / žluťoučký 日本語 😀 \u007f \u2028";

		var preimage = Encoding.UTF8.GetString(RelayEvent.GetIdPreimage(publicKey, 1_700_000_000, 30_023, tags, content));

		Assert.Equal(
			"[0,\"" + publicKey + "\",1700000000,30023,[[\"t\",\"a \\\"quoted\\\" tag\"],[\"empty\",\"\"]],"
			+ "\"line\\nbreak \\\"quote\\\" back\\\\slash return\\r tab\\t backspace\\b feed\\f <html> & 'apostrophe' + / žluťoučký 日本語 😀 \u007f \u2028\"]",
			preimage);

		// No tags and no content are a list and a text all the same.
		Assert.Equal("[0,\"" + publicKey + "\",0,0,[],\"\"]", Encoding.UTF8.GetString(RelayEvent.GetIdPreimage(publicKey, 0, 0, [], "")));
	}

	/// <summary>
	/// A control character that is none of the seven is written in two ways: as it is, which is what the protocol
	/// says, and the way JSON writes it, which is what the programs do that people use. An event counts either way.
	/// </summary>
	[Fact]
	public void ControlCharactersCountEitherWay()
	{
		var publicKey = new string('a', 64);
		var content = "bell\u0007 escape\u001b zero\u0000";

		Assert.Equal(
			"[0,\"" + publicKey + "\",1,1,[[\"t\",\"\\u0001\"]],\"bell\\u0007 escape\\u001b zero\\u0000\"]",
			Encoding.UTF8.GetString(RelayEvent.GetIdPreimage(publicKey, 1, 1, [["t", "\u0001"]], content)));

		Assert.Equal(
			"[0,\"" + publicKey + "\",1,1,[[\"t\",\"\u0001\"]],\"bell\u0007 escape\u001b zero\u0000\"]",
			Encoding.UTF8.GetString(RelayEvent.GetIdPreimage(publicKey, 1, 1, [["t", "\u0001"]], content, escapeControlCharacters: false)));

		using var key = TestRelayEvent.NewKey();

		foreach (var escape in new[] { true, false })
		{
			Assert.True(TryParse(TestRelayEvent.CreateJson(key, 1, content, escapeControlCharacters: escape), out var inContent, out var problem), problem);
			Assert.Equal(content, inContent!.Content);

			Assert.True(TryParse(TestRelayEvent.CreateJson(key, 1, "text", [["t", "\u0001"]], escapeControlCharacters: escape), out _, out problem), problem);
		}
	}

	/// <summary>
	/// The library that the application makes its own events with hashes and signs the way the relay expects: what
	/// the application posts, the relay of another user takes.
	/// </summary>
	[Theory]
	[InlineData("")]
	[InlineData("{\"minFeeRate\":1.5,\"buy\":120,\"sell\":0,\"liquidity\":150000000,\"points\":0,\"inputs\":3,\"extraInputs\":0}")]
	[InlineData("line\nbreak \"quote\" back\\slash tab\t")]
	[InlineData("žluťoučký 日本語")]
	[InlineData("emoji 😀")]
	[InlineData("<html> & 'apostrophe' + plus")]
	[InlineData("bell \u0007")]
	[InlineData("escape \u001b")]
	[InlineData("del \u007f separator \u2028")]
	public async Task EventsOfTheApplicationsLibraryAreTakenAsync(string content)
	{
		using var key = TestRelayEvent.NewKey();

		var nostrEvent = new NostrEvent
		{
			Kind = 15750,
			CreatedAt = DateTimeOffset.FromUnixTimeSeconds(TestRelayEvent.Now),
			Content = content,
			Tags =
			[
				new NostrEventTag { TagIdentifier = "type", Data = ["BlackHoleJoinMarket"] },
				new NostrEventTag { TagIdentifier = "network", Data = ["regtest"] }
			]
		};

		var signed = await nostrEvent.ComputeIdAndSignAsync(key);

		Assert.True(TryParse(JsonSerializer.Serialize(signed), out var relayEvent, out var problem), problem);
		Assert.Equal(signed.Id, relayEvent!.Id);
		Assert.Equal(content, relayEvent.Content);
		Assert.Equal("BlackHoleJoinMarket", relayEvent.GetTagValue("type"));

		// And the library finds nothing wrong with what the relay hands on.
		Assert.True(JsonSerializer.Deserialize<NostrEvent>(relayEvent.Json)!.Verify());
	}

	/// <summary>An event that says something else than what was signed is none.</summary>
	[Theory]
	[InlineData("content")]
	[InlineData("created_at")]
	[InlineData("kind")]
	[InlineData("tags")]
	[InlineData("pubkey")]
	public void EventThatWasChangedIsRefused(string field)
	{
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();
		var json = JsonNode.Parse(TestRelayEvent.CreateJson(key, kind: 1, content: "text", [["t", "tag"]]))!.AsObject();

		json[field] = field switch
		{
			"content" => "other text",
			"created_at" => TestRelayEvent.Now + 1,
			"kind" => 2,
			"tags" => new JsonArray(new JsonArray("t", "other tag")),
			_ => TestRelayEvent.GetPublicKey(otherKey)
		};

		Assert.False(TryParse(json.ToJsonString(), out var relayEvent, out var problem));
		Assert.Null(relayEvent);
		Assert.Equal("invalid: the id is not the hash of the event", problem);
	}

	/// <summary>An event with the right id and the signature of somebody else, or of nobody, is none.</summary>
	[Fact]
	public void EventThatItsKeyDidNotSignIsRefused()
	{
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();

		var json = JsonNode.Parse(TestRelayEvent.CreateJson(key, kind: 1, content: "text"))!.AsObject();
		var id = Convert.FromHexString(json["id"]!.GetValue<string>());

		json["sig"] = Convert.ToHexStringLower(otherKey.SignBIP340(id).ToBytes());
		Assert.False(TryParse(json.ToJsonString(), out _, out var problem));
		Assert.Equal("invalid: the signature is not the one of the key", problem);

		json["sig"] = new string('0', 128);
		Assert.False(TryParse(json.ToJsonString(), out _, out problem));
		Assert.Equal("invalid: the signature is not the one of the key", problem);

		// A key that is no point of the curve signed nothing.
		var withoutKey = TestRelayEvent.CreateJson(key, kind: 1, content: "text");
		var noKey = new string('f', 64);
		var forged = JsonNode.Parse(withoutKey)!.AsObject();
		forged["pubkey"] = noKey;
		forged["id"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(RelayEvent.GetIdPreimage(noKey, TestRelayEvent.Now, 1, [], "text")));
		Assert.False(TryParse(forged.ToJsonString(), out _, out problem));
		Assert.Equal("invalid: the signature is not the one of the key", problem);
	}

	/// <summary>What has not exactly the form of an event is refused before anything is hashed or checked.</summary>
	[Theory]
	[MemberData(nameof(GetMalformedEvents))]
	public void WhatHasNotTheFormOfAnEventIsRefused(string json, string expectedProblem)
	{
		Assert.False(TryParse(json, out var relayEvent, out var problem));
		Assert.Null(relayEvent);
		Assert.Equal(expectedProblem, problem);
	}

	public static TheoryData<string, string> GetMalformedEvents()
	{
		using var key = TestRelayEvent.NewKey();
		var good = TestRelayEvent.CreateJson(key, kind: 1, content: "text", [["t", "tag"]]);
		var data = new TheoryData<string, string>
		{
			{ "[]", "invalid: an event is an object" },
			{ "\"event\"", "invalid: an event is an object" },
			{ "null", "invalid: an event is an object" },
			{ "{}", "invalid: the event lacks one of id, pubkey, created_at, kind, tags, content and sig" }
		};

		string With(string field, string rawValue)
		{
			var fields = JsonNode.Parse(good)!.AsObject().ToDictionary(x => x.Key, x => x.Value!.ToJsonString());
			fields[field] = rawValue;
			return "{" + string.Join(",", fields.Select(x => $"\"{x.Key}\":{x.Value}")) + "}";
		}

		string Without(string field)
		{
			var json = JsonNode.Parse(good)!.AsObject();
			json.Remove(field);
			return json.ToJsonString();
		}

		const string LacksAField = "invalid: the event lacks one of id, pubkey, created_at, kind, tags, content and sig";

		foreach (var field in new[] { "id", "pubkey", "created_at", "kind", "tags", "content", "sig" })
		{
			data.Add(Without(field), LacksAField);
		}

		const string BadId = "invalid: the id is not 64 characters of lowercase hex";
		data.Add(With("id", "\"" + new string('a', 63) + "\""), BadId);
		data.Add(With("id", "\"" + new string('a', 65) + "\""), BadId);
		data.Add(With("id", "\"" + new string('A', 64) + "\""), BadId);
		data.Add(With("id", "\"" + new string('g', 64) + "\""), BadId);
		data.Add(With("id", "5"), BadId);
		data.Add(With("id", "null"), BadId);

		const string BadKey = "invalid: the pubkey is not 64 characters of lowercase hex";
		data.Add(With("pubkey", "\"" + new string('a', 66) + "\""), BadKey);
		data.Add(With("pubkey", "\"" + new string('B', 64) + "\""), BadKey);
		data.Add(With("pubkey", "[]"), BadKey);

		const string BadSignature = "invalid: the sig is not 128 characters of lowercase hex";
		data.Add(With("sig", "\"" + new string('a', 64) + "\""), BadSignature);
		data.Add(With("sig", "\"" + new string('C', 128) + "\""), BadSignature);
		data.Add(With("sig", "true"), BadSignature);

		const string BadTime = "invalid: created_at is not a time in seconds";
		data.Add(With("created_at", "\"1700000000\""), BadTime);
		data.Add(With("created_at", "1700000000.5"), BadTime);
		data.Add(With("created_at", "-1"), BadTime);
		data.Add(With("created_at", "253402300800"), BadTime);
		data.Add(With("created_at", "99999999999999999999999999"), BadTime);
		data.Add(With("created_at", "null"), BadTime);

		const string BadKind = "invalid: the kind is not a number from 0 to 65535";
		data.Add(With("kind", "\"1\""), BadKind);
		data.Add(With("kind", "-1"), BadKind);
		data.Add(With("kind", "65536"), BadKind);
		data.Add(With("kind", "1.5"), BadKind);

		data.Add(With("tags", "{}"), "invalid: the tags are not a list");
		data.Add(With("tags", "\"tags\""), "invalid: the tags are not a list");

		const string BadTag = "invalid: a tag is not a list of texts with a name in front";
		data.Add(With("tags", "[[]]"), BadTag);
		data.Add(With("tags", "[\"t\"]"), BadTag);
		data.Add(With("tags", "[[\"t\",1]]"), BadTag);
		data.Add(With("tags", "[[\"t\",null]]"), BadTag);
		data.Add(With("tags", "[[\"t\",[\"nested\"]]]"), BadTag);
		data.Add(With("tags", "[[\"t\",\"half a pair \\ud83d\"]]"), BadTag);

		const string BadContent = "invalid: the content is not a text";
		data.Add(With("content", "5"), BadContent);
		data.Add(With("content", "null"), BadContent);
		data.Add(With("content", "{\"text\":\"text\"}"), BadContent);
		data.Add(With("content", "\"half a pair \\ud83d\""), BadContent);
		data.Add(With("content", "\"the other half \\ude00 alone\""), BadContent);

		// A field that is there twice.
		foreach (var field in new[] { "id", "pubkey", "created_at", "kind", "tags", "content", "sig" })
		{
			var value = JsonNode.Parse(good)![field]!.ToJsonString();
			data.Add(good[..^1] + $",\"{field}\":{value}}}", $"invalid: the event has more than one {field}");
		}

		return data;
	}

	/// <summary>An event is no larger than the relay says: in tags, and in content.</summary>
	[Fact]
	public void EventBeyondTheLimitsIsRefused()
	{
		using var key = TestRelayEvent.NewKey();
		var limits = Limits with { MaxEventTags = 3, MaxContentLength = 10 };

		Assert.True(TryParse(TestRelayEvent.CreateJson(key, 1, new string('x', 10), [["a"], ["b"], ["c"]]), out _, out var problem, limits), problem);

		Assert.False(TryParse(TestRelayEvent.CreateJson(key, 1, new string('x', 11)), out _, out problem, limits));
		Assert.Equal("invalid: the content is longer than 10 characters", problem);

		Assert.False(TryParse(TestRelayEvent.CreateJson(key, 1, "text", [["a"], ["b"], ["c"], ["d"]]), out _, out problem, limits));
		Assert.Equal("invalid: the event has more than 3 tags", problem);
	}

	/// <summary>The kind of an event tells how the relay keeps it.</summary>
	[Theory]
	[InlineData(1, false, false, false)]
	[InlineData(0, true, false, false)]
	[InlineData(3, true, false, false)]
	[InlineData(4, false, false, false)]
	[InlineData(9_999, false, false, false)]
	[InlineData(10_000, true, false, false)]
	[InlineData(15_750, true, false, false)]
	[InlineData(19_999, true, false, false)]
	[InlineData(20_000, false, true, false)]
	[InlineData(29_999, false, true, false)]
	[InlineData(30_000, false, false, true)]
	[InlineData(39_999, false, false, true)]
	[InlineData(40_000, false, false, false)]
	public void KindTellsHowAnEventIsKept(int kind, bool isReplaceable, bool isEphemeral, bool isAddressable)
	{
		using var key = TestRelayEvent.NewKey();
		var relayEvent = TestRelayEvent.Create(key, kind, "text", [["d", "name"], ["d", "second name"]]);

		Assert.Equal(isReplaceable, relayEvent.IsReplaceable);
		Assert.Equal(isEphemeral, relayEvent.IsEphemeral);
		Assert.Equal(isAddressable, relayEvent.IsAddressable);
		Assert.Equal("name", relayEvent.Address);
		Assert.Equal("", TestRelayEvent.Create(key, kind, "text").Address);
	}

	/// <summary>The relay answers about an event by its id, also when it refuses the event, as long as there is an id.</summary>
	[Theory]
	[InlineData("{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
	[InlineData("{\"id\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}", null)]
	[InlineData("{\"id\":\"short\"}", null)]
	[InlineData("{\"id\":5}", null)]
	[InlineData("{}", null)]
	[InlineData("[]", null)]
	[InlineData("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", null)]
	public void IdThatAnEventClaimsIsRead(string json, string? expectedId)
	{
		using var document = JsonDocument.Parse(json);

		Assert.Equal(expectedId, RelayEvent.ReadClaimedId(document.RootElement));
	}

	[Theory]
	[InlineData("0123456789abcdef", 16, true)]
	[InlineData("0123456789abcdef", 15, false)]
	[InlineData("0123456789ABCDEF", 16, false)]
	[InlineData("0123456789abcdeg", 16, false)]
	[InlineData("", 0, true)]
	[InlineData(null, 0, false)]
	public void HexIsLowercaseAndOfTheRightLength(string? text, int length, bool isHex)
	{
		Assert.Equal(isHex, RelayEvent.IsLowerHex(text, length));
	}
}
