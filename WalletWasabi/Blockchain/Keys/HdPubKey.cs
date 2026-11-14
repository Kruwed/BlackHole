using NBitcoin.Secp256k1;
using WalletWasabi.Bases;
using WalletWasabi.Blockchain.Analysis.Clustering;

namespace WalletWasabi.Blockchain.Keys;

public class HdPubKey : NotifyPropertyChangedBase, IEquatable<HdPubKey>
{
	public const int DefaultHighAnonymitySet = int.MaxValue;

	private readonly Lazy<Script> _p2Taproot;
	private Cluster _cluster;

	public HdPubKey(PubKey pubKey, KeyPath fullKeyPath, KeyState keyState, bool isHandedOut = false)
	{
		PubKey = pubKey;
		FullKeyPath = fullKeyPath;
		_cluster = new Cluster([this]);
		IsHandedOut = isHandedOut;
		KeyState = keyState;

		_p2Taproot = new Lazy<Script>(() => PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), isThreadSafe: true);

		Index = (int)FullKeyPath.Indexes[4];

		int change = (int)FullKeyPath.Indexes[3];
		if (change == 0)
		{
			IsInternal = false;
		}
		else if (change == 1)
		{
			IsInternal = true;
		}
		else
		{
			IsInternal = false;
		}
	}

	public Cluster Cluster
	{
		get => _cluster;
		set => RaiseAndSetIfChanged(ref _cluster, value);
	}

	public Dictionary<uint256, double> HistoricalAnonSet { get; } = new();

	public double AnonymitySet
	{
		get;
		private set => RaiseAndSetIfChanged(ref field, value);
	} = DefaultHighAnonymitySet;

	public PubKey PubKey { get; }

	public KeyPath FullKeyPath { get; }

	/// <summary>
	/// Whether the address of the key was already given to the user to receive a payment to.
	/// An address is given out only once, so that two payers never get the same one.
	/// </summary>
	public bool IsHandedOut { get; private set; }

	public KeyState KeyState { get; private set; }

	public ECPubKey? TweakData { get; set; }

	public Script P2Taproot => _p2Taproot.Value;

	public int Index { get; }
	public bool IsInternal { get; }

	public void SetAnonymitySet(double anonset, uint256? outputAnonSetReasonTxId = null)
	{
		if (outputAnonSetReasonTxId is not null)
		{
			HistoricalAnonSet[outputAnonSetReasonTxId] = anonset;
		}

		AnonymitySet = anonset;
	}

	public void SetHandedOut()
	{
		IsHandedOut = true;
	}

	public void SetKeyState(KeyState state, KeyManager? kmToFile = null)
	{
		if (KeyState == state)
		{
			return;
		}

		KeyState = state;

		kmToFile?.ToFile();
	}

	public bool ContainsScript(Script scriptPubKey) =>
		P2Taproot == scriptPubKey;

	#region Equality

	public override bool Equals(object? obj) => Equals(obj as HdPubKey);

	public bool Equals(HdPubKey? other) => this == other;

	public override int GetHashCode() => PubKey.GetHashCode();

	public static bool operator ==(HdPubKey? x, HdPubKey? y) => x?.PubKey == y?.PubKey;

	public static bool operator !=(HdPubKey? x, HdPubKey? y) => !(x == y);

	#endregion Equality
}
