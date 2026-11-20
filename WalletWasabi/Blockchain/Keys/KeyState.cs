namespace WalletWasabi.Blockchain.Keys;

public enum KeyState
{
	/// <summary>The key was not given out for a change address or to a coinjoin round, and nothing was paid to it.</summary>
	Clean,

	/// <summary>
	/// Nothing was paid to the key, and it is not given out as a new one. Of a change address: a coinjoin round got the key
	/// for an output, see <see cref="KeyManager.GetNextCoinJoinKeys"/>. Of a receive address: the user took it off the list.
	/// </summary>
	Locked,

	/// <summary>A transaction pays to the key, or a coinjoin round reported that it does.</summary>
	Used
}
