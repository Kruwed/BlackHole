namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The script of a fidelity bond, which is the one of JoinMarket:
/// <c>&lt;locktime&gt; OP_CHECKLOCKTIMEVERIFY OP_DROP &lt;public key&gt; OP_CHECKSIG</c>.
/// The key can spend, but not before the locktime. A bond is a P2WSH output that pays to this script.
/// </summary>
public static class BondScript
{
	/// <summary>The script that the output of the bond commits to and that the transaction that spends the bond reveals.</summary>
	public static Script Create(PubKey pubKey, BondLocktime locktime)
	{
		if (!pubKey.IsCompressed)
		{
			throw new ArgumentException("The key of a fidelity bond is a compressed public key.", nameof(pubKey));
		}

		return new Script(
			Op.GetPushOp(locktime.UnixTime),
			OpcodeType.OP_CHECKLOCKTIMEVERIFY,
			OpcodeType.OP_DROP,
			Op.GetPushOp(pubKey.ToBytes()),
			OpcodeType.OP_CHECKSIG);
	}

	/// <summary>The script of the output of the bond.</summary>
	public static Script GetScriptPubKey(Script bondScript) => bondScript.WitHash.ScriptPubKey;

	public static BitcoinAddress GetAddress(Script bondScript, Network network) => bondScript.WitHash.GetAddress(network);
}
