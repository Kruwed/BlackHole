using System.Linq;
using NBitcoin;
using NBitcoin.DataEncoders;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The proof that a fidelity bond stands behind an offer: it names the bond, and the key of the bond signed the key of
/// the offer.
/// </summary>
public class BondProofTests
{
	private const uint UnlockHeight = 900_000;
	private static readonly OutPoint BondOutPoint = new(uint256.Parse(string.Concat(Enumerable.Repeat("aa", 31)) + "01"), 7);
	private static readonly byte[] OfferKey = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];

	/// <summary>
	/// The key of the bond signs SHA256(SHA256(tag) ‖ SHA256(tag) ‖ offer key ‖ txid ‖ vout ‖ unlock height), with the tag
	/// "BlackHole/FidelityBondProof", the transaction id as transactions have it and the two numbers as four bytes,
	/// the lowest first. The expected hash was worked out by hand.
	/// </summary>
	[Fact]
	public void WhatIsSignedIsTheKeyOfTheOfferAndTheBond()
	{
		Assert.Equal("BlackHole/FidelityBondProof", BondProof.Tag);
		Assert.Equal(
			"36ce5f189c7d87681ac1686e73f64359133f59880a6ba5caa7a0d810fbb283ab",
			Encoders.Hex.EncodeData(BondProof.GetHash(OfferKey, BondOutPoint, UnlockHeight)));
	}

	[Fact]
	public void EveryPartOfWhatIsSignedChangesTheHash()
	{
		var hash = BondProof.GetHash(OfferKey, BondOutPoint, UnlockHeight);
		byte[] otherOfferKey = [.. OfferKey];
		otherOfferKey[31] ^= 1;

		Assert.NotEqual(hash, BondProof.GetHash(otherOfferKey, BondOutPoint, UnlockHeight));
		Assert.NotEqual(hash, BondProof.GetHash(OfferKey, new OutPoint(BondOutPoint.Hash, 8), UnlockHeight));
		Assert.NotEqual(hash, BondProof.GetHash(OfferKey, new OutPoint(uint256.One, 7), UnlockHeight));
		Assert.NotEqual(hash, BondProof.GetHash(OfferKey, BondOutPoint, UnlockHeight + 1));
		Assert.Throws<ArgumentException>(() => BondProof.GetHash(new byte[33], BondOutPoint, UnlockHeight));
	}

	[Fact]
	public void ProofHoldsForTheOfferThatItWasMadeFor()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);

		Assert.True(proof.IsWellFormed);
		Assert.Equal(BondOutPoint, proof.OutPoint);
		Assert.Equal(UnlockHeight, proof.UnlockHeight);
		Assert.Equal(Encoders.Hex.EncodeData(bondKey.PubKey.TaprootInternalKey.ToBytes()), proof.Key);
		Assert.Equal(128, proof.Signature.Length);
		Assert.True(proof.IsSignedFor(OfferKey));
	}

	/// <summary>The proof of one offer is of no use to another: whoever copies it has another key.</summary>
	[Fact]
	public void ProofDoesNotHoldForAnotherOffer()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);
		byte[] otherOfferKey = [.. OfferKey];
		otherOfferKey[0] ^= 1;

		Assert.False(proof.IsSignedFor(otherOfferKey));
		Assert.False(proof.IsSignedFor(new byte[31]));
	}

	[Fact]
	public void ProofDoesNotHoldForAnotherBond()
	{
		using var bondKey = new Key();
		using var otherKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);

		Assert.False((proof with { OutPoint = new OutPoint(BondOutPoint.Hash, 8) }).IsSignedFor(OfferKey));
		Assert.False((proof with { OutPoint = new OutPoint(uint256.One, 7) }).IsSignedFor(OfferKey));
		Assert.False((proof with { UnlockHeight = UnlockHeight + 1 }).IsSignedFor(OfferKey));

		// The signature of another key.
		Assert.False((proof with { Key = Encoders.Hex.EncodeData(otherKey.PubKey.TaprootInternalKey.ToBytes()) }).IsSignedFor(OfferKey));
		Assert.False((proof with { Signature = BondProof.Create(otherKey, BondOutPoint, UnlockHeight, OfferKey).Signature }).IsSignedFor(OfferKey));
	}

	/// <summary>
	/// The script of a bond commits to its key and to its unlock height. So the output that a proof tells of is found
	/// by its script, and an output with another script is not the bond of the proof.
	/// </summary>
	[Fact]
	public void ProofTellsTheScriptOfItsBond()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);

		Assert.Equal(BondScript.GetScriptPubKey(BondScript.CreateLeaf(bondKey.PubKey, UnlockHeight)), proof.GetScriptPubKey());
		Assert.NotEqual(proof.GetScriptPubKey(), (proof with { UnlockHeight = UnlockHeight + 1 }).GetScriptPubKey());
	}

	[Theory]
	[InlineData("", null, 900_000u)]
	[InlineData("00", null, 900_000u)]
	[InlineData("zz", null, 900_000u)]
	[InlineData(null, "", 900_000u)]
	[InlineData(null, "abcd", 900_000u)]
	[InlineData(null, null, 0u)]
	[InlineData(null, null, 500_000_000u)] // From here on a lock time is a date, not a height
	public void ProofThatIsNotWellFormedHoldsForNothing(string? key, string? signature, uint unlockHeight)
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);

		var malformed = proof with
		{
			Key = key is null ? proof.Key : key == "zz" ? new string('z', 64) : key,
			Signature = signature ?? proof.Signature,
			UnlockHeight = unlockHeight
		};

		Assert.False(malformed.IsWellFormed);
		Assert.False(malformed.IsSignedFor(OfferKey));
	}

	/// <summary>
	/// A proof is handed on as one text: its transaction, the number of its output, its unlock height, its key and its
	/// signature, with colons between them. That is how the config of a coordinator carries the proof that a wallet made.
	/// </summary>
	[Fact]
	public void ProofIsWrittenAsOneTextAndReadBack()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);
		var text = proof.ToText();

		Assert.Equal($"{BondOutPoint.Hash}:7:900000:{proof.Key}:{proof.Signature}", text);

		Assert.True(BondProof.TryParse(text, out var read));
		Assert.Equal(proof, read);
		Assert.True(read.IsSignedFor(OfferKey));

		Assert.True(BondProof.TryParse($"  {text}\n", out read));
		Assert.Equal(proof, read);

		// The same from the five parts, which is how a tag of an event carries it.
		Assert.True(BondProof.TryCreate(BondOutPoint.Hash.ToString(), "7", "900000", proof.Key, proof.Signature, out read));
		Assert.Equal(proof, read);

		string[] parts = text.Split(':');

		foreach (var wrong in new[]
		{
			"",
			"a proof",
			string.Join(':', parts[..4]),
			text + ":more",
			string.Join(':', parts[0][..^1], parts[1], parts[2], parts[3], parts[4]),
			string.Join(':', parts[0], "-1", parts[2], parts[3], parts[4]),
			string.Join(':', parts[0], "seven", parts[2], parts[3], parts[4]),
			string.Join(':', parts[0], parts[1], "0", parts[3], parts[4]),
			string.Join(':', parts[0], parts[1], "500000000", parts[3], parts[4]),
			string.Join(':', parts[0], parts[1], parts[2], parts[3][..^2], parts[4]),
			string.Join(':', parts[0], parts[1], parts[2], parts[3], parts[4] + "00"),
			text.Replace(':', ' ')
		})
		{
			Assert.False(BondProof.TryParse(wrong, out read), wrong);
			Assert.Null(read);
		}
	}

	[Fact]
	public void ProofWithoutAKeyOrAHeightTellsNoScript()
	{
		using var bondKey = new Key();
		var proof = BondProof.Create(bondKey, BondOutPoint, UnlockHeight, OfferKey);

		Assert.Throws<InvalidOperationException>(() => (proof with { Key = "00" }).GetScriptPubKey());
		Assert.Throws<InvalidOperationException>(() => (proof with { UnlockHeight = 0 }).GetScriptPubKey());
		Assert.Throws<InvalidOperationException>(() => (proof with { UnlockHeight = BondScript.MaxUnlockHeight + 1 }).GetScriptPubKey());
	}
}
