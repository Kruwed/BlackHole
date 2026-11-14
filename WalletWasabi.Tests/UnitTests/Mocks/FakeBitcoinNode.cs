using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;

namespace WalletWasabi.Tests.UnitTests.Mocks;

/// <summary>
/// A Bitcoin node as far as the wallet sees it through its RPC interface: a chain of blocks that grows and reorganizes, and a mempool.
/// </summary>
public class FakeBitcoinNode
{
	private readonly Lock _lock = new();

	/// <summary>The active chain. The index of a block is its height.</summary>
	private readonly List<Block> _chain = [];

	/// <summary>Every block that the node ever had, also those that are not part of the active chain any more.</summary>
	private readonly Dictionary<uint256, Block> _blocks = [];

	private readonly Dictionary<uint256, Transaction> _mempool = [];
	private readonly List<uint256> _requestedBlocks = [];
	private uint _nonce;

	public FakeBitcoinNode(Network network)
	{
		Network = network;
		Connect(network.GetGenesis());

		RpcClient = new MockRpcClient
		{
			Network = network,
			OnGetBlockchainInfoAsync = () => Answer(() => new BlockchainInfo
			{
				Blocks = Height,
				Headers = Math.Max(Headers ?? 0, Height),
				BestBlockHash = _chain[^1].GetHash(),
				Pruned = PrunedBelow > 0
			}),
			OnGetBestBlockHashAsync = () => Answer(() => _chain[^1].GetHash()),
			OnGetBlockHashAsync = height => Answer(() =>
				height >= 0 && height < _chain.Count
					? _chain[height].GetHash()
					: throw new RPCException(RPCErrorCode.RPC_INVALID_PARAMETER, "Block height out of range", null)),
			OnGetBlockAsync = blockHash => Answer(() =>
			{
				_requestedBlocks.Add(blockHash);

				if (!_blocks.TryGetValue(blockHash, out var block))
				{
					throw new RPCException(RPCErrorCode.RPC_INVALID_ADDRESS_OR_KEY, "Block not found", null);
				}

				var height = _chain.IndexOf(block);
				if (height >= 0 && height < PrunedBelow)
				{
					throw new RPCException(RPCErrorCode.RPC_MISC_ERROR, "Block not available (pruned data)", null);
				}

				return block;
			}),
			OnGetRawMempoolAsync = () => Answer(() =>
			{
				MempoolRequestCount++;
				return _mempool.Keys.ToArray();
			}),
			OnGetRawTransactionAsync = (txid, _) => Answer(() =>
			{
				RequestedMempoolTransactionCount++;
				return _mempool.GetValueOrDefault(txid)!;
			})
		};
	}

	public Network Network { get; }

	public MockRpcClient RpcClient { get; }

	/// <summary>Height of the newest block that the node has.</summary>
	public uint Height
	{
		get
		{
			lock (_lock)
			{
				return (uint)(_chain.Count - 1);
			}
		}
	}

	/// <summary>The headers that the node knows about, when it knows about more than it has blocks: it is still downloading the chain.</summary>
	public uint? Headers { get; set; }

	/// <summary>A pruned node does not have the blocks below this height any more.</summary>
	public uint PrunedBelow { get; set; }

	/// <summary>What every request fails with, when the node is not available.</summary>
	public Exception? Failure { get; set; }

	public int MempoolRequestCount { get; private set; }

	public int RequestedMempoolTransactionCount { get; private set; }

	/// <summary>The hashes of the blocks that were asked from the node, in the order of the requests.</summary>
	public IReadOnlyList<uint256> RequestedBlocks
	{
		get
		{
			lock (_lock)
			{
				return _requestedBlocks.ToArray();
			}
		}
	}

	public uint256 GetBlockHash(uint height)
	{
		lock (_lock)
		{
			return _chain[(int)height].GetHash();
		}
	}

	/// <summary>
	/// Adds a block with the given transactions to the chain. They leave the mempool.
	/// </summary>
	public Block MineBlock(params Transaction[] transactions)
	{
		lock (_lock)
		{
			var block = Network.Consensus.ConsensusFactory.CreateBlock();
			block.Header.HashPrevBlock = _chain[^1].GetHash();
			block.Header.BlockTime = _chain[^1].Header.BlockTime + TimeSpan.FromMinutes(10);

			// Makes blocks differ that are mined on top of the same block.
			block.Header.Nonce = ++_nonce;

			var coinbase = Network.CreateTransaction();
			coinbase.Inputs.Add(new TxIn(new OutPoint(), new Script(Op.GetPushOp(_chain.Count), Op.GetPushOp(_nonce))));
			coinbase.Outputs.Add(Money.Coins(50), CreateForeignScript());
			block.AddTransaction(coinbase);

			foreach (var transaction in transactions)
			{
				block.AddTransaction(transaction);
				_mempool.Remove(transaction.GetHash());
			}

			block.UpdateMerkleRoot();
			Connect(block);
			return block;
		}
	}

	public void MineBlocks(int count)
	{
		for (int i = 0; i < count; i++)
		{
			MineBlock();
		}
	}

	/// <summary>
	/// Takes the newest blocks off the chain, as a reorganization does before other blocks take their place.
	/// </summary>
	/// <returns>The blocks that are not part of the chain any more, the newest first.</returns>
	public IReadOnlyList<Block> DisconnectBlocks(int count)
	{
		lock (_lock)
		{
			var disconnected = new List<Block>();

			for (int i = 0; i < count; i++)
			{
				disconnected.Add(_chain[^1]);
				_chain.RemoveAt(_chain.Count - 1);
			}

			return disconnected;
		}
	}

	public void AddToMempool(Transaction transaction)
	{
		lock (_lock)
		{
			_mempool[transaction.GetHash()] = transaction;
		}
	}

	public void RemoveFromMempool(Transaction transaction)
	{
		lock (_lock)
		{
			_mempool.Remove(transaction.GetHash());
		}
	}

	/// <summary>A script that the wallet has nothing to do with.</summary>
	private static Script CreateForeignScript(ScriptPubKeyType type = ScriptPubKeyType.TaprootBIP86)
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(type);
	}

	private void Connect(Block block)
	{
		_chain.Add(block);
		_blocks[block.GetHash()] = block;
	}

	private Task<T> Answer<T>(Func<T> answer)
	{
		lock (_lock)
		{
			if (Failure is { } failure)
			{
				return Task.FromException<T>(failure);
			}

			try
			{
				return Task.FromResult(answer());
			}
			catch (Exception ex)
			{
				return Task.FromException<T>(ex);
			}
		}
	}
}
