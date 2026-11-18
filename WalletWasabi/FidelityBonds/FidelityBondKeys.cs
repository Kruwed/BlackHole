using System.Diagnostics.CodeAnalysis;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The keys of the fidelity bonds of a wallet. They are a chain of their own in the account of the wallet, the third one
/// after the receive and the change addresses, as in JoinMarket: <c>account/2/index</c>, where the index is the number of
/// the month that the bond unlocks in. So there is one key, and one address, for every such month.
/// </summary>
/// <remarks>The public keys come from the public key of the account: watching for bonds takes no password.</remarks>
public class FidelityBondKeys
{
	/// <summary>The number of the chain of the bond keys within the account.</summary>
	public const uint Chain = 2;

	private readonly ExtPubKey _chainExtPubKey;
	private readonly Lazy<Dictionary<Script, BondLocktime>> _locktimesByScriptPubKey;

	public FidelityBondKeys(ExtPubKey accountExtPubKey, KeyPath accountKeyPath)
	{
		_chainExtPubKey = accountExtPubKey.Derive(Chain);
		ChainKeyPath = accountKeyPath.Derive(Chain);

		// Deriving all the keys takes a moment, and a wallet without bonds may never need them all.
		_locktimesByScriptPubKey = new(() => BondLocktime.All.ToDictionary(locktime => BondScript.GetScriptPubKey(GetScript(locktime))));
	}

	public KeyPath ChainKeyPath { get; }

	public KeyPath GetKeyPath(BondLocktime locktime) => ChainKeyPath.Derive((uint)locktime.Index);

	public PubKey GetPubKey(BondLocktime locktime) => _chainExtPubKey.Derive((uint)locktime.Index).PubKey;

	/// <summary>The script that the bond of the month commits to, see <see cref="BondScript"/>.</summary>
	public Script GetScript(BondLocktime locktime) => BondScript.Create(GetPubKey(locktime), locktime);

	/// <summary>The address to pay to in order to lock coins until the month.</summary>
	public BitcoinAddress GetAddress(BondLocktime locktime, Network network) => BondScript.GetAddress(GetScript(locktime), network);

	/// <summary>Tells if an output with this script is a bond of the wallet, and when it unlocks.</summary>
	public bool TryGetLocktime(Script scriptPubKey, [NotNullWhen(true)] out BondLocktime? locktime)
	{
		// This is asked about every output of the chain. Nearly all of them are told apart by their length and first byte:
		// a bond pays to the hash of a script, version 0.
		if (scriptPubKey.Length == 34 && scriptPubKey.ToBytes(true)[0] == (byte)OpcodeType.OP_0
			&& _locktimesByScriptPubKey.Value.TryGetValue(scriptPubKey, out var found))
		{
			locktime = found;
			return true;
		}

		locktime = null;
		return false;
	}
}
