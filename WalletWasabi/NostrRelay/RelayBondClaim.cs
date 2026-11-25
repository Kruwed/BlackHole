using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// How a key shows a relay that a fidelity bond stands behind it: with the proof of the bond in an event that the key
/// signed. The proof is the one that offers carry, see <see cref="BondProof"/>: it names the bond, and the key of the
/// bond signed the key of the event, so it is of no use to any other key.
/// </summary>
/// <remarks>
/// An event carries the proof in one of two places. An offer of Black Hole has it in its content, as <c>bond</c>,
/// which is where the wallet puts it. Any event can have it in a tag:
/// <c>["bond", txid, vout, unlock height, key of the bond, signature]</c>, the numbers written as texts, like
/// everything in a tag. A key that has shown its bond need not show it again with every event: see
/// <see cref="RelaySenders"/>.
/// </remarks>
public static class RelayBondClaim
{
	/// <summary>The name of the tag that carries the proof of a fidelity bond.</summary>
	public const string TagName = "bond";

	private const string TypeTag = "type";

	/// <summary>
	/// Reads the proof that an event carries, if it carries one that is signed for the key of the event. Whether the
	/// chain has the bond is for the node to say.
	/// </summary>
	public static bool TryRead(RelayEvent relayEvent, [NotNullWhen(true)] out BondProof? proof)
	{
		proof = ReadTag(relayEvent) ?? ReadOffer(relayEvent);

		if (proof is null || !proof.IsWellFormed || !proof.IsSignedFor(Convert.FromHexString(relayEvent.PublicKey)))
		{
			proof = null;
			return false;
		}

		return true;
	}

	/// <summary>Writes the tag that carries a proof.</summary>
	public static string[] CreateTag(BondProof proof) =>
	[
		TagName,
		proof.OutPoint.Hash.ToString(),
		proof.OutPoint.N.ToString(CultureInfo.InvariantCulture),
		proof.UnlockHeight.ToString(CultureInfo.InvariantCulture),
		proof.Key,
		proof.Signature
	];

	/// <summary>The name that a bond has in the books of a relay: its transaction and the number of its output.</summary>
	public static string GetBondId(OutPoint outPoint) =>
		string.Create(CultureInfo.InvariantCulture, $"{outPoint.Hash}:{outPoint.N}");

	private static BondProof? ReadTag(RelayEvent relayEvent)
	{
		foreach (var tag in relayEvent.Tags)
		{
			if (tag[0] == TagName)
			{
				// The first tag of the name is the one that counts, as with every tag.
				return tag is [_, var txid, var vout, var unlockHeight, var key, var signature]
					? Create(txid, vout, unlockHeight, key, signature)
					: null;
			}
		}

		return null;
	}

	private static BondProof? ReadOffer(RelayEvent relayEvent)
	{
		if (relayEvent.Kind != MakerOfferEvent.Kind
			|| relayEvent.GetTagValue(TypeTag) != MakerOfferEvent.Type
			|| relayEvent.Content.Length is 0 or > MakerOfferEvent.MaxContentLength)
		{
			return null;
		}

		try
		{
			using var content = JsonDocument.Parse(relayEvent.Content, new JsonDocumentOptions { MaxDepth = 8 });

			if (content.RootElement.ValueKind != JsonValueKind.Object
				|| !content.RootElement.TryGetProperty("bond", out var bond)
				|| bond.ValueKind != JsonValueKind.Object
				|| !bond.TryGetProperty("txid", out var txid) || txid.ValueKind != JsonValueKind.String
				|| !bond.TryGetProperty("vout", out var vout) || vout.ValueKind != JsonValueKind.Number
				|| !bond.TryGetProperty("unlockHeight", out var unlockHeight) || unlockHeight.ValueKind != JsonValueKind.Number
				|| !bond.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String
				|| !bond.TryGetProperty("signature", out var signature) || signature.ValueKind != JsonValueKind.String)
			{
				return null;
			}

			return Create(txid.GetString()!, vout.GetRawText(), unlockHeight.GetRawText(), key.GetString()!, signature.GetString()!);
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			// Anybody can write anything into an event. What cannot be read shows no bond.
			return null;
		}
	}

	private static BondProof? Create(string txid, string vout, string unlockHeight, string key, string signature) =>
		txid.Length == 64
		&& uint256.TryParse(txid, out var hash)
		&& uint.TryParse(vout, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
		&& uint.TryParse(unlockHeight, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
			? new BondProof(new OutPoint(hash, index), height, key, signature)
			: null;
}
