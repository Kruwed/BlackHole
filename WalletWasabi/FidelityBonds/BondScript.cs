using System.Diagnostics.CodeAnalysis;
using NBitcoin.DataEncoders;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The script of a fidelity bond: coins that cannot be spent before the chain reaches a block height, and then only
/// with the signature of one key.
/// </summary>
/// <remarks>
/// A bond is a Taproot output with nothing but a script to spend it by:
/// <c>&lt;unlock height&gt; OP_CHECKLOCKTIMEVERIFY OP_DROP &lt;public key&gt; OP_CHECKSIG</c>.
/// Its internal key is the point that BIP341 names for outputs without a key path. Nobody knows the private key of that
/// point, so whoever is shown the script and the output can tell that there is no way around the timelock.
/// </remarks>
public static class BondScript
{
	/// <summary>The highest height that a locktime can name. From 500,000,000 on, a locktime is a time and not a height.</summary>
	public const uint MaxUnlockHeight = 499_999_999;

	/// <summary>
	/// The internal key of every bond: the point <c>H</c> of BIP341, whose x coordinate is the hash of the generator of the curve.
	/// </summary>
	public static readonly TaprootInternalPubKey UnspendableKey =
		new(Encoders.Hex.DecodeData("50929b74c1a04954b78b4b6035e97a5e078a5a0f28ec96d547bfee9ace803ac0"));

	/// <summary>The script that the bond is spent by.</summary>
	/// <param name="unlockHeight">
	/// The height that the chain must have reached: a transaction that spends the bond is valid in the blocks above this height.
	/// </param>
	public static Script CreateLeaf(PubKey pubKey, uint unlockHeight) => CreateLeaf(pubKey.TaprootInternalKey.ToBytes(), unlockHeight);

	/// <param name="xOnlyPubKey">The x coordinate of the public key, which is how a script of a Taproot output names a key.</param>
	public static Script CreateLeaf(byte[] xOnlyPubKey, uint unlockHeight)
	{
		if (unlockHeight is 0 or > MaxUnlockHeight)
		{
			throw new ArgumentOutOfRangeException(nameof(unlockHeight), unlockHeight, $"A fidelity bond unlocks at a block height between 1 and {MaxUnlockHeight}.");
		}

		if (xOnlyPubKey.Length != 32)
		{
			throw new ArgumentException("The key of a fidelity bond is named by its x coordinate of 32 bytes.", nameof(xOnlyPubKey));
		}

		return new Script(
			Op.GetPushOp(unlockHeight),
			OpcodeType.OP_CHECKLOCKTIMEVERIFY,
			OpcodeType.OP_DROP,
			Op.GetPushOp(xOnlyPubKey),
			OpcodeType.OP_CHECKSIG);
	}

	/// <summary>The output script of the bond: the Taproot output that commits to the script and to nothing else.</summary>
	public static Script GetScriptPubKey(Script leaf) =>
		UnspendableKey.GetTaprootFullPubKey(ToTapScript(leaf).LeafHash).ScriptPubKey;

	public static BitcoinAddress GetAddress(Script leaf, Network network) =>
		GetScriptPubKey(leaf).GetDestinationAddress(network)
		?? throw new InvalidOperationException("The output of a fidelity bond has an address.");

	public static TapScript ToTapScript(Script leaf) => new(leaf, TapLeafVersion.C0);

	/// <summary>What a transaction needs to know to spend the bond by its script.</summary>
	public static TaprootSpendInfo GetSpendInfo(Script leaf)
	{
		var builder = new TaprootBuilder();
		builder.AddLeaf(0, ToTapScript(leaf));
		return builder.Finalize(UnspendableKey);
	}

	/// <summary>
	/// Reads the unlock height and the key out of the script of a bond. Only the exact form that <see cref="CreateLeaf(byte[], uint)"/>
	/// writes is taken for one.
	/// </summary>
	public static bool TryParseLeaf(Script script, out uint unlockHeight, [NotNullWhen(true)] out byte[]? xOnlyPubKey)
	{
		unlockHeight = 0;
		xOnlyPubKey = null;

		// One byte for a height up to 16 and up to five for a higher one, then two opcodes, the key with its length and one opcode.
		if (script.Length is < 37 or > 41)
		{
			return false;
		}

		Op[] ops;
		try
		{
			ops = script.ToOps().ToArray();
		}
		catch (FormatException)
		{
			return false;
		}

		if (ops.Length != 5
			|| ops[1].Code != OpcodeType.OP_CHECKLOCKTIMEVERIFY
			|| ops[2].Code != OpcodeType.OP_DROP
			|| ops[3].PushData is not { Length: 32 } key
			|| ops[4].Code != OpcodeType.OP_CHECKSIG)
		{
			return false;
		}

		long height;
		if (ops[0].Code is >= OpcodeType.OP_1 and <= OpcodeType.OP_16)
		{
			height = ops[0].Code - OpcodeType.OP_1 + 1;
		}
		else if (ops[0].PushData is { Length: >= 1 and <= 4 } number)
		{
			// A script number: the lowest byte first, and the highest bit of the last byte is the sign.
			height = 0;
			for (var i = 0; i < number.Length; i++)
			{
				height |= (long)number[i] << (8 * i);
			}

			if ((number[^1] & 0x80) != 0)
			{
				return false;
			}
		}
		else
		{
			return false;
		}

		// Written again, it has to be the same script: a height can be pushed in more than one way, and only one is a bond.
		if (height is < 1 or > MaxUnlockHeight || !CreateLeaf(key, (uint)height).ToBytes().AsSpan().SequenceEqual(script.ToBytes(true)))
		{
			return false;
		}

		unlockHeight = (uint)height;
		xOnlyPubKey = key;
		return true;
	}
}
