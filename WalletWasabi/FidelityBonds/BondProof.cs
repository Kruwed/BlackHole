using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The proof that a fidelity bond stands behind an offer: the bond is named, and its key signed the key of the offer.
/// </summary>
/// <remarks>
/// <para>
/// A bond is a Taproot output with one script, <see cref="BondScript"/>, which names an unlock height and a key. Whoever
/// is given the two can work out the output script and look the output up: if the chain has an unspent output with that
/// script at the named place, the coins are locked until that height, and only the holder of the key gets them back.
/// The signature shows that the holder of the key is the one who made the offer: it is over the key that signed the
/// offer, so the proof of one offer is of no use in another.
/// </para>
/// <para>
/// What the bond holds is not part of the proof. It is what the output holds, and whoever checks the proof reads it
/// from the chain.
/// </para>
/// </remarks>
/// <param name="OutPoint">The output that is the bond.</param>
/// <param name="UnlockHeight">The height that the chain has to reach before the bond can be spent.</param>
/// <param name="Key">The key of the script of the bond, by its x coordinate, in hex.</param>
/// <param name="Signature">The signature of that key over the key of the offer, in hex.</param>
public sealed record BondProof(OutPoint OutPoint, uint UnlockHeight, string Key, string Signature)
{
	/// <summary>What the hash that is signed starts with, so that the signature cannot be taken for one of anything else.</summary>
	public const string Tag = "BlackHole/FidelityBondProof";

	private const int KeyLength = 32;
	private const int SignatureLength = 64;

	/// <summary>Signs the key of an offer with the key of a bond.</summary>
	/// <param name="bondKey">The private key of the script of the bond.</param>
	/// <param name="offerKey">The public key that signs the offer, by its x coordinate: 32 bytes.</param>
	public static BondProof Create(Key bondKey, OutPoint outPoint, uint unlockHeight, ReadOnlySpan<byte> offerKey)
	{
		var xOnlyKey = bondKey.PubKey.TaprootInternalKey.ToBytes();
		var hash = GetHash(offerKey, outPoint, unlockHeight);

		using var signingKey = ECPrivKey.Create(bondKey.ToBytes());
		var signature = new byte[SignatureLength];
		signingKey.SignBIP340(hash).WriteToSpan(signature);

		return new BondProof(outPoint, unlockHeight, Encoders.Hex.EncodeData(xOnlyKey), Encoders.Hex.EncodeData(signature));
	}

	/// <summary>
	/// Makes a proof of its five parts as texts carry them: the transaction and the number of the output, the unlock
	/// height, the key and the signature. Whether the signature is one for a given key is another question, see
	/// <see cref="IsSignedFor"/>.
	/// </summary>
	/// <returns>False if the parts are not those of a proof.</returns>
	public static bool TryCreate(string txid, string vout, string unlockHeight, string key, string signature, [NotNullWhen(true)] out BondProof? proof)
	{
		proof = txid.Length == 64
			&& uint256.TryParse(txid, out var hash)
			&& uint.TryParse(vout, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
			&& uint.TryParse(unlockHeight, NumberStyles.None, CultureInfo.InvariantCulture, out var height)
				? new BondProof(new OutPoint(hash, index), height, key, signature)
				: null;

		if (proof is { IsWellFormed: false })
		{
			proof = null;
		}

		return proof is not null;
	}

	/// <summary>
	/// Reads a proof that is written as one text, see <see cref="ToText"/>. That is how a proof is handed from the
	/// wallet that made it to whoever shows it, like a coordinator in its config file.
	/// </summary>
	public static bool TryParse(string text, [NotNullWhen(true)] out BondProof? proof)
	{
		proof = null;

		return text.Trim().Split(':') is [var txid, var vout, var unlockHeight, var key, var signature]
			&& TryCreate(txid, vout, unlockHeight, key, signature, out proof);
	}

	/// <summary>Writes the proof as one text: its transaction, the number of its output, its unlock height, its key and its signature, with colons between them.</summary>
	public string ToText() =>
		string.Create(CultureInfo.InvariantCulture, $"{OutPoint.Hash}:{OutPoint.N}:{UnlockHeight}:{Key}:{Signature}");

	/// <summary>What the key of the bond signs: the key of the offer, and the bond that the proof names.</summary>
	public static byte[] GetHash(ReadOnlySpan<byte> offerKey, OutPoint outPoint, uint unlockHeight)
	{
		if (offerKey.Length != KeyLength)
		{
			throw new ArgumentException("The key of an offer is named by its x coordinate of 32 bytes.", nameof(offerKey));
		}

		var tagHash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(Tag));
		var message = new byte[tagHash.Length * 2 + KeyLength + 32 + 4 + 4];
		var position = 0;

		tagHash.CopyTo(message, position);
		position += tagHash.Length;
		tagHash.CopyTo(message, position);
		position += tagHash.Length;
		offerKey.CopyTo(message.AsSpan(position));
		position += KeyLength;
		outPoint.Hash.ToBytes().CopyTo(message, position);
		position += 32;
		BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(position), outPoint.N);
		position += 4;
		BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(position), unlockHeight);

		return System.Security.Cryptography.SHA256.HashData(message);
	}

	/// <summary>Whether the proof is well formed: a height that a bond can unlock at, a key and a signature of the right lengths.</summary>
	public bool IsWellFormed =>
		UnlockHeight is > 0 and <= BondScript.MaxUnlockHeight
		&& TryDecode(Key, KeyLength, out _)
		&& TryDecode(Signature, SignatureLength, out _);

	/// <summary>Whether the key of the bond signed the key of the given offer.</summary>
	/// <param name="offerKey">The public key that signed the offer, by its x coordinate: 32 bytes.</param>
	public bool IsSignedFor(ReadOnlySpan<byte> offerKey)
	{
		if (offerKey.Length != KeyLength
			|| UnlockHeight is 0 or > BondScript.MaxUnlockHeight
			|| !TryDecode(Key, KeyLength, out var key)
			|| !TryDecode(Signature, SignatureLength, out var signature)
			|| !ECXOnlyPubKey.TryCreate(key, out var publicKey)
			|| !SecpSchnorrSignature.TryCreate(signature, out var schnorrSignature))
		{
			return false;
		}

		return publicKey.SigVerifyBIP340(schnorrSignature, GetHash(offerKey, OutPoint, UnlockHeight));
	}

	/// <summary>The output script that the bond of the proof has, if the proof tells the truth about its height and its key.</summary>
	/// <exception cref="InvalidOperationException">The proof is not well formed.</exception>
	public Script GetScriptPubKey() =>
		TryDecode(Key, KeyLength, out var key) && UnlockHeight is > 0 and <= BondScript.MaxUnlockHeight
			? BondScript.GetScriptPubKey(BondScript.CreateLeaf(key, UnlockHeight))
			: throw new InvalidOperationException("The proof of the fidelity bond is not well formed.");

	private static bool TryDecode(string hex, int length, [NotNullWhen(true)] out byte[]? bytes)
	{
		bytes = null;

		if (hex.Length != length * 2)
		{
			return false;
		}

		try
		{
			bytes = Encoders.Hex.DecodeData(hex);
			return bytes.Length == length;
		}
		catch (FormatException)
		{
			return false;
		}
	}
}
