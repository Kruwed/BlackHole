using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NBitcoin.Secp256k1;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// An event of Nostr that the relay has read and checked: it has the fields that an event has and no others that
/// count, its id is the hash of what it says, and it is signed by the key that it names.
/// </summary>
/// <remarks>
/// Anybody can send anything to a relay. So nothing of an event is taken on trust, and nothing of it is read by code
/// that expects events to be well made: what is not exactly an event is none.
/// </remarks>
public sealed class RelayEvent
{
	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	private RelayEvent(string id, string publicKey, long createdAt, int kind, ImmutableArray<ImmutableArray<string>> tags, string content, string signature)
	{
		Id = id;
		PublicKey = publicKey;
		CreatedAt = createdAt;
		Kind = kind;
		Tags = tags;
		Content = content;
		Signature = signature;
		Json = WriteJson();
		Size = StrictUtf8.GetByteCount(Json);
	}

	/// <summary>The hash of the event, 64 characters of lowercase hex.</summary>
	public string Id { get; }

	/// <summary>The key that signed the event, 64 characters of lowercase hex.</summary>
	public string PublicKey { get; }

	/// <summary>When the event was made, as its maker tells it: seconds since 1970.</summary>
	public long CreatedAt { get; }

	public int Kind { get; }

	public ImmutableArray<ImmutableArray<string>> Tags { get; }

	public string Content { get; }

	public string Signature { get; }

	/// <summary>The event as the relay hands it on: the seven fields of an event, and nothing else that came with it.</summary>
	public string Json { get; }

	/// <summary>How many bytes <see cref="Json"/> has.</summary>
	public int Size { get; }

	/// <summary>Of a replaceable kind the relay keeps the newest event of a key and forgets the ones before.</summary>
	public bool IsReplaceable => Kind is 0 or 3 or (>= 10_000 and < 20_000);

	/// <summary>An ephemeral event is handed to whoever is listening, and not kept.</summary>
	public bool IsEphemeral => Kind is >= 20_000 and < 30_000;

	/// <summary>Of an addressable kind the relay keeps the newest event of a key and of a name, which the <c>d</c> tag tells.</summary>
	public bool IsAddressable => Kind is >= 30_000 and < 40_000;

	/// <summary>The name that tells the events of an addressable kind and of one key apart: the value of the first <c>d</c> tag, or nothing.</summary>
	public string Address => GetTagValue("d") ?? "";

	/// <summary>The value of the first tag of a name: what follows the name in the tag. Null if there is no such tag, or it has no value.</summary>
	public string? GetTagValue(string name)
	{
		foreach (var tag in Tags)
		{
			if (tag[0] == name)
			{
				return tag.Length > 1 ? tag[1] : null;
			}
		}

		return null;
	}

	/// <summary>Whether a text is lowercase hex of the given length, the way ids, keys and signatures are written.</summary>
	public static bool IsLowerHex([NotNullWhen(true)] string? text, int length)
	{
		if (text is null || text.Length != length)
		{
			return false;
		}

		foreach (var character in text)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Reads an event and checks it: its form, its size, its id and its signature.
	/// </summary>
	/// <param name="json">What a client sent as the event.</param>
	/// <param name="problem">Why it is no event that the relay takes, in the words that the relay answers with.</param>
	public static bool TryParse(JsonElement json, RelayLimits limits, [NotNullWhen(true)] out RelayEvent? relayEvent, [NotNullWhen(false)] out string? problem)
	{
		relayEvent = null;

		if (!TryRead(json, limits, out var read, out problem))
		{
			return false;
		}

		if (!read.HasItsId())
		{
			problem = "invalid: the id is not the hash of the event";
			return false;
		}

		if (!read.IsSignedByItsKey())
		{
			problem = "invalid: the signature is not the one of the key";
			return false;
		}

		relayEvent = read;
		return true;
	}

	/// <summary>
	/// Reads what a client sent as an event far enough to tell which event it claims to be: its id, if it has one.
	/// The relay answers about an event by its id, also when it refuses the event.
	/// </summary>
	public static string? ReadClaimedId(JsonElement json)
	{
		if (json.ValueKind == JsonValueKind.Object
			&& json.TryGetProperty("id", out var id)
			&& id.ValueKind == JsonValueKind.String
			&& TryGetText(id, out var text)
			&& IsLowerHex(text, 64))
		{
			return text;
		}

		return null;
	}

	/// <summary>
	/// What the id of an event is the hash of: the key, the time, the kind, the tags and the content, written as JSON
	/// without anything that could be written in two ways.
	/// </summary>
	/// <param name="escapeControlCharacters">
	/// The one thing that is written in two ways after all. The protocol names seven characters that are written with a
	/// backslash, and says that every other character is written as it is. The programs that people use write the
	/// control characters that are not among the seven as JSON has them, <c>\u0001</c> for example, as a text with
	/// such a character in it is no JSON otherwise. True writes them like the programs do, false like the protocol says.
	/// </param>
	public static byte[] GetIdPreimage(string publicKey, long createdAt, int kind, ImmutableArray<ImmutableArray<string>> tags, string content, bool escapeControlCharacters = true)
	{
		var text = new StringBuilder(capacity: 128 + content.Length);

		text.Append("[0,\"").Append(publicKey).Append("\",");
		text.Append(createdAt.ToString(CultureInfo.InvariantCulture)).Append(',');
		text.Append(kind.ToString(CultureInfo.InvariantCulture)).Append(",[");

		for (var i = 0; i < tags.Length; i++)
		{
			if (i > 0)
			{
				text.Append(',');
			}

			text.Append('[');

			for (var j = 0; j < tags[i].Length; j++)
			{
				if (j > 0)
				{
					text.Append(',');
				}

				AppendText(text, tags[i][j], escapeControlCharacters);
			}

			text.Append(']');
		}

		text.Append("],");
		AppendText(text, content, escapeControlCharacters);
		text.Append(']');

		return StrictUtf8.GetBytes(text.ToString());
	}

	private static bool TryRead(JsonElement json, RelayLimits limits, [NotNullWhen(true)] out RelayEvent? relayEvent, [NotNullWhen(false)] out string? problem)
	{
		relayEvent = null;

		if (json.ValueKind != JsonValueKind.Object)
		{
			problem = "invalid: an event is an object";
			return false;
		}

		string? id = null;
		string? publicKey = null;
		string? signature = null;
		string? content = null;
		long? createdAt = null;
		int? kind = null;
		ImmutableArray<ImmutableArray<string>>? tags = null;

		foreach (var property in json.EnumerateObject())
		{
			var value = property.Value;

			// A field that is there twice says two things, and nobody can tell which of them is meant.
			var isRepeated = property.Name switch
			{
				"id" => id is not null,
				"pubkey" => publicKey is not null,
				"sig" => signature is not null,
				"content" => content is not null,
				"created_at" => createdAt is not null,
				"kind" => kind is not null,
				"tags" => tags is not null,
				_ => false
			};

			if (isRepeated)
			{
				problem = $"invalid: the event has more than one {property.Name}";
				return false;
			}

			switch (property.Name)
			{
				case "id":
					if (value.ValueKind != JsonValueKind.String || !TryGetText(value, out id) || !IsLowerHex(id, 64))
					{
						problem = "invalid: the id is not 64 characters of lowercase hex";
						return false;
					}

					break;

				case "pubkey":
					if (value.ValueKind != JsonValueKind.String || !TryGetText(value, out publicKey) || !IsLowerHex(publicKey, 64))
					{
						problem = "invalid: the pubkey is not 64 characters of lowercase hex";
						return false;
					}

					break;

				case "sig":
					if (value.ValueKind != JsonValueKind.String || !TryGetText(value, out signature) || !IsLowerHex(signature, 128))
					{
						problem = "invalid: the sig is not 128 characters of lowercase hex";
						return false;
					}

					break;

				case "content":
					if (value.ValueKind != JsonValueKind.String || !TryGetText(value, out content))
					{
						problem = "invalid: the content is not a text";
						return false;
					}

					if (content.Length > limits.MaxContentLength)
					{
						problem = $"invalid: the content is longer than {limits.MaxContentLength} characters";
						return false;
					}

					break;

				case "created_at":
					// Seconds since 1970, in whole numbers, up to the year 9999.
					if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds) || seconds is < 0 or > 253_402_300_799)
					{
						problem = "invalid: created_at is not a time in seconds";
						return false;
					}

					createdAt = seconds;
					break;

				case "kind":
					if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 0 or > 65_535)
					{
						problem = "invalid: the kind is not a number from 0 to 65535";
						return false;
					}

					kind = number;
					break;

				case "tags":
					if (!TryReadTags(value, limits, out var readTags, out problem))
					{
						return false;
					}

					tags = readTags;
					break;
			}
		}

		if (id is null || publicKey is null || signature is null || content is null || createdAt is null || kind is null || tags is null)
		{
			problem = "invalid: the event lacks one of id, pubkey, created_at, kind, tags, content and sig";
			return false;
		}

		relayEvent = new RelayEvent(id, publicKey, createdAt.Value, kind.Value, tags.Value, content, signature);
		problem = null;
		return true;
	}

	private static bool TryReadTags(JsonElement json, RelayLimits limits, out ImmutableArray<ImmutableArray<string>> tags, [NotNullWhen(false)] out string? problem)
	{
		tags = [];

		if (json.ValueKind != JsonValueKind.Array)
		{
			problem = "invalid: the tags are not a list";
			return false;
		}

		if (json.GetArrayLength() > limits.MaxEventTags)
		{
			problem = $"invalid: the event has more than {limits.MaxEventTags} tags";
			return false;
		}

		var readTags = ImmutableArray.CreateBuilder<ImmutableArray<string>>(json.GetArrayLength());

		foreach (var tag in json.EnumerateArray())
		{
			// A tag is a list of texts, and the first of them is its name.
			if (tag.ValueKind != JsonValueKind.Array || tag.GetArrayLength() == 0)
			{
				problem = "invalid: a tag is not a list of texts with a name in front";
				return false;
			}

			var values = ImmutableArray.CreateBuilder<string>(tag.GetArrayLength());

			foreach (var value in tag.EnumerateArray())
			{
				if (value.ValueKind != JsonValueKind.String || !TryGetText(value, out var text))
				{
					problem = "invalid: a tag is not a list of texts with a name in front";
					return false;
				}

				values.Add(text);
			}

			readTags.Add(values.MoveToImmutable());
		}

		tags = readTags.MoveToImmutable();
		problem = null;
		return true;
	}

	/// <summary>Reads a text of JSON. A text that cannot be written in UTF-8, half of a pair of surrogates for example, is none.</summary>
	private static bool TryGetText(JsonElement json, [NotNullWhen(true)] out string? text)
	{
		try
		{
			text = json.GetString();
		}
		catch (InvalidOperationException)
		{
			text = null;
			return false;
		}

		if (text is null)
		{
			return false;
		}

		for (var i = 0; i < text.Length; i++)
		{
			if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
			{
				i++;
			}
			else if (char.IsSurrogate(text[i]))
			{
				text = null;
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Writes a text for <see cref="GetIdPreimage"/>: seven characters are written with a backslash, and every other
	/// character as it is, except for the other control characters if they are to be written as JSON has them.
	/// </summary>
	private static void AppendText(StringBuilder builder, string text, bool escapeControlCharacters)
	{
		builder.Append('"');

		foreach (var character in text)
		{
			switch (character)
			{
				case '\n': builder.Append("\\n"); break;
				case '"': builder.Append("\\\""); break;
				case '\\': builder.Append("\\\\"); break;
				case '\r': builder.Append("\\r"); break;
				case '\t': builder.Append("\\t"); break;
				case '\b': builder.Append("\\b"); break;
				case '\f': builder.Append("\\f"); break;

				case < ' ' when escapeControlCharacters:
					builder.Append("\\u00").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
					break;

				default: builder.Append(character); break;
			}
		}

		builder.Append('"');
	}

	/// <summary>Whether a text has a control character that is written in two ways, see <see cref="GetIdPreimage"/>.</summary>
	private static bool HasOtherControlCharacter(string text)
	{
		foreach (var character in text)
		{
			if (character < ' ' && character is not ('\n' or '\r' or '\t' or '\b' or '\f'))
			{
				return true;
			}
		}

		return false;
	}

	private bool HasItsId()
	{
		var id = Convert.FromHexString(Id);
		Span<byte> hash = stackalloc byte[32];

		System.Security.Cryptography.SHA256.HashData(GetIdPreimage(PublicKey, CreatedAt, Kind, Tags, Content), hash);

		if (hash.SequenceEqual(id))
		{
			return true;
		}

		// An event with a control character that is written in two ways may have been hashed the other way.
		if (!HasOtherControlCharacter(Content) && !Tags.Any(tag => tag.Any(HasOtherControlCharacter)))
		{
			return false;
		}

		System.Security.Cryptography.SHA256.HashData(GetIdPreimage(PublicKey, CreatedAt, Kind, Tags, Content, escapeControlCharacters: false), hash);

		return hash.SequenceEqual(id);
	}

	private bool IsSignedByItsKey() =>
		ECXOnlyPubKey.TryCreate(Convert.FromHexString(PublicKey), out var publicKey)
		&& SecpSchnorrSignature.TryCreate(Convert.FromHexString(Signature), out var signature)
		&& publicKey.SigVerifyBIP340(signature, Convert.FromHexString(Id));

	private string WriteJson()
	{
		var buffer = new ArrayBufferWriter<byte>(256 + Content.Length);

		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			writer.WriteStartObject();
			writer.WriteString("id", Id);
			writer.WriteString("pubkey", PublicKey);
			writer.WriteNumber("created_at", CreatedAt);
			writer.WriteNumber("kind", Kind);
			writer.WriteStartArray("tags");

			foreach (var tag in Tags)
			{
				writer.WriteStartArray();

				foreach (var value in tag)
				{
					writer.WriteStringValue(value);
				}

				writer.WriteEndArray();
			}

			writer.WriteEndArray();
			writer.WriteString("content", Content);
			writer.WriteString("sig", Signature);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
