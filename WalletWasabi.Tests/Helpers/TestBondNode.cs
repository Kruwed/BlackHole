using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using WalletWasabi.FidelityBonds;
using WalletWasabi.NostrRelay;

namespace WalletWasabi.Tests.Helpers;

/// <summary>A fidelity bond that a test locked, with the key that it is locked to.</summary>
public sealed record TestBond(Key Key, OutPoint OutPoint, uint UnlockHeight, long Amount)
{
	/// <summary>The name of the bond in the books of a relay.</summary>
	public string Id => RelayBondClaim.GetBondId(OutPoint);

	/// <summary>Proves the bond for the key of an event, the way the wallet does for the key of an offer.</summary>
	public BondProof Prove(ECPrivKey eventKey) =>
		BondProof.Create(Key, OutPoint, UnlockHeight, eventKey.CreateXOnlyPubKey().ToBytes());

	/// <summary>The tag that shows the bond in an event of the key.</summary>
	public string[] Tag(ECPrivKey eventKey) => RelayBondClaim.CreateTag(Prove(eventKey));
}

/// <summary>
/// A node for tests of the relay: it has the fidelity bonds that a test locks, and tells about them what the node of
/// the user tells, see <see cref="NodeBondChecker"/>.
/// </summary>
public sealed class TestBondNode : IDisposable
{
	public const uint Tip = 800_000;

	private readonly Lock _lock = new();
	private readonly Dictionary<OutPoint, (Script ScriptPubKey, long Amount)> _outputs = [];
	private readonly List<Key> _keys = [];
	private int _lookups;

	/// <summary>The height of the chain.</summary>
	public uint TipHeight { get; set; } = Tip;

	/// <summary>Whether the node can be asked.</summary>
	public bool IsAvailable { get; set; } = true;

	/// <summary>How often the node was asked about a bond.</summary>
	public int Lookups => Volatile.Read(ref _lookups);

	/// <summary>Locks coins in a bond that the chain has from now on.</summary>
	public TestBond Lock(long amount, uint unlockHeight = Tip + 1_000)
	{
		var key = new Key();
		var bond = new TestBond(key, new OutPoint(RandomUtils.GetUInt256(), 0), unlockHeight, amount);

		lock (_lock)
		{
			_keys.Add(key);
			_outputs[bond.OutPoint] = (BondScript.GetScriptPubKey(BondScript.CreateLeaf(key.PubKey, unlockHeight)), amount);
		}

		return bond;
	}

	/// <summary>A bond that nobody locked: the chain does not have it.</summary>
	public TestBond Invent(long amount, uint unlockHeight = Tip + 1_000)
	{
		var key = new Key();

		lock (_lock)
		{
			_keys.Add(key);
		}

		return new TestBond(key, new OutPoint(RandomUtils.GetUInt256(), 0), unlockHeight, amount);
	}

	/// <summary>Tells about a bond what the node tells: <see cref="CheckBond"/>.</summary>
	public Task<BondCheck> CheckAsync(string publicKey, BondProof proof, CancellationToken cancellationToken)
	{
		Interlocked.Increment(ref _lookups);

		if (!IsAvailable)
		{
			return Task.FromResult(new BondCheck(BondCheckResult.Unavailable));
		}

		lock (_lock)
		{
			var isBond = proof.UnlockHeight > TipHeight
				&& proof.IsSignedFor(Convert.FromHexString(publicKey))
				&& _outputs.TryGetValue(proof.OutPoint, out var output)
				&& output.ScriptPubKey == proof.GetScriptPubKey();

			return Task.FromResult(isBond
				? new BondCheck(BondCheckResult.Found, _outputs[proof.OutPoint].Amount)
				: new BondCheck(BondCheckResult.NotFound));
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			foreach (var key in _keys)
			{
				key.Dispose();
			}

			_keys.Clear();
		}
	}
}
