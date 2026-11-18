using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The keys of the fidelity bonds of a wallet, and the bond addresses that the wallet watches.
/// </summary>
/// <remarks>
/// <para>
/// The keys are a chain of their own in the account of the wallet, the third one after the receive and the change
/// addresses: <c>account/2/height</c>, where the last number is the block height that the bond unlocks at. So every
/// unlock height has its own key and its own address. The public keys come from the public key of the account: watching
/// for bonds takes no password.
/// </para>
/// <para>
/// There are far too many heights to watch the addresses of all of them. The wallet watches the ones that it gave out
/// (<see cref="Register"/>), and the wallet file remembers them. A wallet that was recovered from its backup does not
/// know them any more: while it goes through the chain again, it looks for a bond at every height that it could have
/// chosen (<see cref="PrepareSearch"/>), which takes a while to prepare.
/// </para>
/// </remarks>
public class FidelityBondKeys
{
	/// <summary>The number of the chain of the bond keys within the account.</summary>
	public const uint Chain = 2;

	/// <summary>
	/// The furthest that a bond can unlock from the tip of the chain: the blocks of ten years. A recovered wallet looks
	/// for its bonds up to that far ahead, so this limit cannot be raised without making old bonds harder to find.
	/// </summary>
	public const uint MaxLockBlocks = 525_600;

	private readonly ExtPubKey _chainExtPubKey;
	private readonly Lock _watchedLock = new();
	private readonly Lock _searchLock = new();

	/// <summary>The watched addresses. Replaced as a whole when a height is added, so that it can be read without a lock.</summary>
	private volatile Watched _watched = Watched.Empty;

	/// <summary>The output keys of the heights that are searched, by their first half. Null when no search is on.</summary>
	private volatile ConcurrentDictionary<UInt128, uint>? _search;

	private uint _searchFrom;
	private uint _searchTo;
	private int _version;

	public FidelityBondKeys(ExtPubKey accountExtPubKey, KeyPath accountKeyPath)
	{
		_chainExtPubKey = accountExtPubKey.Derive(Chain);
		ChainKeyPath = accountKeyPath.Derive(Chain);
	}

	/// <summary>Raised when the wallet starts to watch the address of one more unlock height.</summary>
	public event Action<uint>? HeightRegistered;

	public KeyPath ChainKeyPath { get; }

	/// <summary>The unlock heights whose addresses the wallet watches, in ascending order.</summary>
	public IReadOnlyCollection<uint> KnownHeights => _watched.Heights;

	/// <summary>Whether the wallet looks for bonds at heights that it does not remember, see <see cref="PrepareSearch"/>.</summary>
	public bool IsSearching => _search is not null;

	/// <summary>Counts the changes of what the wallet watches. The same outputs are bonds as long as this number is the same.</summary>
	public int Version => Volatile.Read(ref _version);

	public KeyPath GetKeyPath(uint unlockHeight) => ChainKeyPath.Derive(AssertHeight(unlockHeight));

	public PubKey GetPubKey(uint unlockHeight) => _chainExtPubKey.Derive(AssertHeight(unlockHeight)).PubKey;

	/// <summary>The script that the bond of the height is spent by, see <see cref="BondScript"/>.</summary>
	public Script GetLeaf(uint unlockHeight) => BondScript.CreateLeaf(GetPubKey(unlockHeight), unlockHeight);

	/// <summary>The output script of the bonds that unlock at the height.</summary>
	public Script GetScriptPubKey(uint unlockHeight) => BondScript.GetScriptPubKey(GetLeaf(unlockHeight));

	/// <summary>
	/// The address to pay to in order to lock coins until the height. The wallet only sees what is paid to an address that
	/// it watches: <see cref="Register"/> comes before the address is given to anyone.
	/// </summary>
	public BitcoinAddress GetAddress(uint unlockHeight, Network network) => BondScript.GetAddress(GetLeaf(unlockHeight), network);

	/// <summary>Makes the wallet watch the address of the bonds that unlock at the height.</summary>
	/// <returns><c>false</c> if the wallet watched it already.</returns>
	public bool Register(uint unlockHeight)
	{
		if (!RegisterWithoutNotice([unlockHeight]))
		{
			return false;
		}

		HeightRegistered?.Invoke(unlockHeight);
		return true;
	}

	/// <summary>Takes the heights that the wallet file remembers.</summary>
	internal void Load(IEnumerable<uint> unlockHeights) => RegisterWithoutNotice(unlockHeights);

	/// <summary>Tells if an output with this script is a bond of the wallet, and at which height it unlocks.</summary>
	/// <remarks>
	/// Asked about every output of the chain. While a search is on, a bond at a height that the wallet did not remember
	/// is found too, and its height is watched from then on.
	/// </remarks>
	public bool TryGetUnlockHeight(Script scriptPubKey, out uint unlockHeight)
	{
		var watched = _watched;
		var search = _search;

		// Nearly every wallet has no bond at all, and a bond is a Taproot output.
		if ((watched.ByScriptPubKey.Count == 0 && search is null) || !TryGetFingerprint(scriptPubKey, out var fingerprint))
		{
			unlockHeight = 0;
			return false;
		}

		if (watched.ByScriptPubKey.TryGetValue(scriptPubKey, out unlockHeight))
		{
			return true;
		}

		// The first half of the output key picks the height. Only the whole script tells that it is the one.
		if (search is not null && search.TryGetValue(fingerprint, out var candidate) && GetScriptPubKey(candidate) == scriptPubKey)
		{
			Register(candidate);
			unlockHeight = candidate;
			return true;
		}

		unlockHeight = 0;
		return false;
	}

	/// <summary>
	/// Makes <see cref="TryGetUnlockHeight"/> find the bonds of every unlock height from <paramref name="from"/> to
	/// <paramref name="to"/>, not only of the heights that the wallet watches. Called again, it adds what is missing.
	/// </summary>
	/// <remarks>
	/// Every height takes two calculations on the curve, one for its key and one for its output. Ten years of heights keep
	/// the processors of a laptop busy for about a minute.
	/// </remarks>
	public void PrepareSearch(uint from, uint to, CancellationToken cancellationToken)
	{
		from = Math.Max(from, 1);
		to = Math.Min(to, BondScript.MaxUnlockHeight);

		// One search is prepared at a time. The lock is not the one of the watched heights: a new height does not wait for this.
		lock (_searchLock)
		{
			if (_search is { } existing && from >= _searchFrom)
			{
				// More of the chain, at its upper end.
				if (to > _searchTo)
				{
					Add(existing, _searchTo + 1, to, cancellationToken);
					_searchTo = to;
				}

				return;
			}

			if (to < from)
			{
				return;
			}

			var search = new ConcurrentDictionary<UInt128, uint>();
			Add(search, from, to, cancellationToken);
			_searchFrom = from;
			_searchTo = to;
			_search = search;
			Interlocked.Increment(ref _version);
		}
	}

	/// <summary>Ends the search. The bonds that it found stay watched.</summary>
	public void EndSearch()
	{
		lock (_searchLock)
		{
			_search = null;
			_searchFrom = 0;
			_searchTo = 0;
			Interlocked.Increment(ref _version);
		}
	}

	/// <summary>That many heights are handed to a thread of the search at a time.</summary>
	private const int SearchBatch = 256;

	private void Add(ConcurrentDictionary<UInt128, uint> search, uint from, uint to, CancellationToken cancellationToken)
	{
		if (to < from)
		{
			return;
		}

		long next = from;
		long end = (long)to + 1;

		void Work()
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				var start = Interlocked.Add(ref next, SearchBatch) - SearchBatch;
				if (start >= end)
				{
					return;
				}

				for (var height = start; height < Math.Min(start + SearchBatch, end); height++)
				{
					if (TryGetFingerprint(GetScriptPubKey((uint)height), out var fingerprint))
					{
						// Should two heights ever share the first half of their keys, the search misses one of them. That is as
						// likely as guessing a private key.
						search.TryAdd(fingerprint, (uint)height);
					}
				}
			}
		}

		// The heights of a few new blocks are no work.
		if (end - from <= 4 * SearchBatch)
		{
			Work();
			return;
		}

		// Years of heights keep the processors busy for a while. They get threads of their own, so that nothing else in
		// the application waits for a thread of the pool in the meantime, and one processor is left to the rest of it.
		var failures = new ConcurrentQueue<Exception>();
		var threads = Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount - 1))
			.Select(i => new Thread(() =>
			{
				try
				{
					Work();
				}
				catch (Exception ex)
				{
					failures.Enqueue(ex);
				}
			})
			{
				IsBackground = true,
				Name = $"Fidelity bond search {i + 1}",
				Priority = ThreadPriority.BelowNormal
			})
			.ToList();

		threads.ForEach(thread => thread.Start());
		threads.ForEach(thread => thread.Join());

		cancellationToken.ThrowIfCancellationRequested();

		if (!failures.IsEmpty)
		{
			throw new AggregateException("The search for fidelity bonds could not be prepared.", failures);
		}
	}

	private bool RegisterWithoutNotice(IEnumerable<uint> unlockHeights)
	{
		lock (_watchedLock)
		{
			var watched = _watched;
			var added = unlockHeights.Where(height => !watched.Heights.Contains(height)).Distinct().ToArray();

			if (added.Length == 0)
			{
				return false;
			}

			var byScriptPubKey = new Dictionary<Script, uint>(watched.ByScriptPubKey);
			foreach (var unlockHeight in added)
			{
				byScriptPubKey[GetScriptPubKey(unlockHeight)] = unlockHeight;
			}

			_watched = new Watched(byScriptPubKey, watched.Heights.Union(added));
			Interlocked.Increment(ref _version);
			return true;
		}
	}

	/// <summary>The first half of the key of a Taproot output.</summary>
	private static bool TryGetFingerprint(Script scriptPubKey, out UInt128 fingerprint)
	{
		// OP_1 and a key of 32 bytes.
		if (scriptPubKey.Length == 34 && scriptPubKey.ToBytes(true) is [(byte)OpcodeType.OP_1, 32, ..] bytes)
		{
			fingerprint = BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(2, 16));
			return true;
		}

		fingerprint = default;
		return false;
	}

	private static uint AssertHeight(uint unlockHeight) =>
		unlockHeight is >= 1 and <= BondScript.MaxUnlockHeight
			? unlockHeight
			: throw new ArgumentOutOfRangeException(nameof(unlockHeight), unlockHeight, $"A fidelity bond unlocks at a block height between 1 and {BondScript.MaxUnlockHeight}.");

	/// <summary>Never changed once it is made.</summary>
	private sealed record Watched(Dictionary<Script, uint> ByScriptPubKey, ImmutableSortedSet<uint> Heights)
	{
		public static readonly Watched Empty = new([], []);
	}
}
