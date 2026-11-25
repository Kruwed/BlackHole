using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using NBitcoin.Secp256k1;
using WalletWasabi.NostrRelay;

namespace WalletWasabi.Tests.Helpers;

/// <summary>Makes events of Nostr for tests, signed like a client signs them, and events that are wrong in one way.</summary>
public static class TestRelayEvent
{
	/// <summary>A time for events that tests make: 2026-11-24 12:00:00 UTC.</summary>
	public const long Now = 1_795_521_600;

	public static ECPrivKey NewKey() => ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

	public static string GetPublicKey(ECPrivKey key) => Convert.ToHexStringLower(key.CreateXOnlyPubKey().ToBytes());

	/// <summary>Makes the JSON of an event that is signed by the key.</summary>
	/// <param name="escapeControlCharacters">How control characters are written for the hash, see <see cref="RelayEvent.GetIdPreimage"/>.</param>
	public static string CreateJson(ECPrivKey key, int kind, string content, string[][]? tags = null, long createdAt = Now, bool escapeControlCharacters = true)
	{
		var publicKey = GetPublicKey(key);
		var tagList = (tags ?? []).Select(tag => tag.ToImmutableArray()).ToImmutableArray();
		var id = System.Security.Cryptography.SHA256.HashData(RelayEvent.GetIdPreimage(publicKey, createdAt, kind, tagList, content, escapeControlCharacters));

		return CreateJson(Convert.ToHexStringLower(id), publicKey, createdAt, kind, tags ?? [], content, Convert.ToHexStringLower(key.SignBIP340(id).ToBytes()));
	}

	/// <summary>Writes an event with exactly the given fields, right or wrong.</summary>
	public static string CreateJson(string id, string publicKey, long createdAt, int kind, string[][] tags, string content, string signature) =>
		JsonSerializer.Serialize(
			new Dictionary<string, object>
			{
				["id"] = id,
				["pubkey"] = publicKey,
				["created_at"] = createdAt,
				["kind"] = kind,
				["tags"] = tags,
				["content"] = content,
				["sig"] = signature
			},
			new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

	/// <summary>Makes an event that is signed by the key, read and checked by the relay's own reader.</summary>
	public static RelayEvent Create(ECPrivKey key, int kind, string content, string[][]? tags = null, long createdAt = Now, RelayLimits? limits = null)
	{
		using var json = JsonDocument.Parse(CreateJson(key, kind, content, tags, createdAt));

		return RelayEvent.TryParse(json.RootElement, limits ?? RelayLimits.ForCustomEvents, out var relayEvent, out var problem)
			? relayEvent
			: throw new InvalidOperationException(problem);
	}
}
