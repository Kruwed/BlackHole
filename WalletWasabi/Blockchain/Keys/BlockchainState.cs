using NBitcoin;

namespace WalletWasabi.Blockchain.Keys;

/// <summary>
/// How far the wallet got with processing the blocks of the chain.
/// </summary>
public class BlockchainState
{
	/// <summary>
	/// That many of the last processed blocks are remembered. After a reorganization of the chain they tell
	/// the last block that the wallet and the node have in common.
	/// </summary>
	public const int MaxRememberedBlocks = 101;

	private readonly List<uint256> _blockHashes;

	/// <param name="blockHashes">Hashes of the last processed blocks, the one at <paramref name="height"/> last.</param>
	public BlockchainState(Network? network = null, ChainHeight? height = null, ChainHeight? birthHeight = null, IEnumerable<uint256>? blockHashes = null)
	{
		Network = network ?? Network.Main;
		Height = height;
		BirthHeight = birthHeight;
		_blockHashes = height is null ? [] : TakeLast(blockHashes ?? [], (int)Math.Min(height.Height + 1, MaxRememberedBlocks));
	}

	public Network Network { get; }

	/// <summary>Height of the last block that the wallet processed.</summary>
	/// <remarks>
	/// <c>null</c> as long as the wallet does not know where its history starts: it was created before
	/// the node could tell the height of the chain.
	/// </remarks>
	public ChainHeight? Height { get; private set; }

	/// <summary>No block below this height concerns the wallet.</summary>
	public ChainHeight? BirthHeight { get; private set; }

	/// <summary>Hashes of the last processed blocks, the oldest first. The last one is the block at <see cref="Height"/>.</summary>
	/// <remarks>Empty when the wallet did not process any block since its height was set.</remarks>
	public IReadOnlyList<uint256> BlockHashes => _blockHashes;

	/// <summary>
	/// A new wallet starts here: nothing concerns it up to the tip of the chain at the time of its creation.
	/// </summary>
	public void StartAt(ChainHeight tipHeight)
	{
		Height = tipHeight;
		BirthHeight ??= tipHeight;
		_blockHashes.Clear();
	}

	/// <summary>
	/// Makes the wallet process the chain from the given block on, again or for the first time.
	/// </summary>
	public void ScanFrom(ChainHeight firstBlockHeight)
	{
		// Nothing concerns the wallet in the genesis block.
		Height = firstBlockHeight.Height > 0 ? firstBlockHeight - 1 : ChainHeight.Genesis;
		_blockHashes.Clear();
	}

	/// <summary>
	/// Remembers that the block was processed.
	/// </summary>
	public void SetProcessedBlock(ChainHeight height, uint256 blockHash)
	{
		// Only an unbroken sequence of blocks tells where two chains part.
		if (Height is null || height.Height != Height.Height + 1)
		{
			_blockHashes.Clear();
		}

		Height = height;
		_blockHashes.Add(blockHash);

		if (_blockHashes.Count > MaxRememberedBlocks)
		{
			_blockHashes.RemoveRange(0, _blockHashes.Count - MaxRememberedBlocks);
		}
	}

	/// <summary>
	/// Forgets the blocks that were processed above the given height.
	/// </summary>
	/// <returns><c>false</c> if the wallet is not above that height.</returns>
	public bool RewindTo(ChainHeight height)
	{
		if (Height is null || height >= Height)
		{
			return false;
		}

		var toForget = Height.Height - height.Height;
		_blockHashes.RemoveRange((int)Math.Max(0, _blockHashes.Count - toForget), (int)Math.Min(_blockHashes.Count, toForget));
		Height = height;
		return true;
	}

	private static List<uint256> TakeLast(IEnumerable<uint256> hashes, int count)
	{
		var all = hashes.ToList();
		return all.Count > count ? all.GetRange(all.Count - count, count) : all;
	}
}
