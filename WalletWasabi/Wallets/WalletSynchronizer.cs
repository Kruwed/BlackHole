using Microsoft.Extensions.Hosting;
using NBitcoin.RPC;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Wallets;

/// <summary>
/// What the wallet is doing to keep up with the user's node.
/// </summary>
public abstract record WalletSyncStatus
{
	/// <summary>The node was not asked yet.</summary>
	public record NotStarted : WalletSyncStatus;

	/// <summary>The node does not answer, or it cannot be used.</summary>
	public record NodeUnavailable(string Reason) : WalletSyncStatus;

	/// <summary>Something went wrong in the wallet itself while it was processing what the node told.</summary>
	public record Failed(string Reason) : WalletSyncStatus;

	/// <summary>The node is still downloading the chain: it knows of more blocks than it has, or it does not have yet what the wallet processed already.</summary>
	public record NodeSynchronizing(uint NodeHeight, uint NodeHeaders) : WalletSyncStatus;

	/// <summary>The node is pruned and it does not have a block any more that the wallet did not process yet.</summary>
	public record BlockPruned(uint MissingHeight) : WalletSyncStatus;

	/// <summary>The wallet is processing the blocks that it did not see yet.</summary>
	public record Scanning(uint Height, uint TipHeight) : WalletSyncStatus;

	/// <summary>The wallet processed every block that the node has.</summary>
	public record Synchronized(uint Height) : WalletSyncStatus;

	/// <summary>Tells the status in a sentence.</summary>
	public string ToUserFriendlyString() =>
		this switch
		{
			NotStarted => "Waiting for the Bitcoin node.",
			NodeUnavailable(var reason) => reason,
			Failed(var reason) => $"The wallet failed to process what the Bitcoin node told: {reason}",
			NodeSynchronizing(var nodeHeight, var nodeHeaders) when nodeHeaders > nodeHeight =>
				$"The Bitcoin node is downloading the chain: it has {nodeHeight:N0} of {nodeHeaders:N0} blocks.",
			NodeSynchronizing(var nodeHeight, _) => $"The Bitcoin node is behind the wallet: it has only {nodeHeight:N0} blocks.",
			BlockPruned(var missingHeight) =>
				$"The Bitcoin node is pruned and does not have block {missingHeight:N0} any more, which the wallet did not process yet. Blocks that old are only available from a node that keeps the whole chain.",
			Scanning(var height, var tipHeight) => $"Processing blocks: {height:N0} of {tipHeight:N0}.",
			Synchronized => "Synchronized with the Bitcoin node.",
			_ => ToString()
		};
}

/// <summary>
/// Keeps the wallet up to date using nothing but the RPC interface of the user's node: it processes every block
/// that the wallet did not see yet, follows the reorganizations of the chain, and watches the mempool of the node.
/// </summary>
public class WalletSynchronizer : BackgroundService
{
	/// <summary>How long to wait before asking the node again when there was nothing to do.</summary>
	internal TimeSpan IdlePeriod { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>How long to wait before trying again after a failure.</summary>
	internal TimeSpan RetryPeriod { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>The height of the wallet is written to the wallet file at least this often while blocks are processed.</summary>
	private static readonly TimeSpan PersistPeriod = TimeSpan.FromSeconds(30);

	/// <summary>Progress is announced at most this often while blocks are processed.</summary>
	private static readonly TimeSpan ProgressPeriod = TimeSpan.FromMilliseconds(500);

	/// <summary>That many hashes of blocks are asked from the node at once.</summary>
	private const int BlockHashBatchSize = 100;

	/// <summary>That many transactions of the mempool are asked from the node at once.</summary>
	private const int MempoolBatchSize = 50;

	/// <summary>That many transactions of the mempool are fetched at most before the node is asked about new blocks again.</summary>
	private const int MaxMempoolTransactionsPerRound = 2_000;

	private readonly IRPCClient _rpcClient;
	private readonly KeyManager _keyManager;
	private readonly AllTransactionStore _transactionStore;
	private readonly TransactionProcessor _transactionProcessor;
	private readonly EventBus _eventBus;
	private readonly Action? _onSynchronizedFirst;
	private readonly bool _watchMempool;
	private readonly int _maxParallelBlockDownloads;
	private readonly TaskCompletionSource _initialSynchronizationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Lock _statusLock = new();

	private WalletSyncStatus _status = new WalletSyncStatus.NotStarted();
	private bool _isChainVerified;
	private DateTimeOffset _lastPersisted = DateTimeOffset.UtcNow;
	private DateTimeOffset _lastProgressAnnounced = DateTimeOffset.MinValue;
	private uint? _lastAnnouncedHeight;

	/// <summary>The transactions that were in the mempool of the node when it was asked the last time.</summary>
	private HashSet<uint256> _mempoolTxids = [];

	/// <summary>Transactions of the mempool that were not looked at yet.</summary>
	private readonly Queue<uint256> _mempoolBacklog = new();

	private DateTimeOffset _nextMempoolPoll = DateTimeOffset.MinValue;

	/// <param name="onSynchronizedFirst">
	/// Called once, when every block of the node is processed for the first time and before the mempool of the node is looked at.
	/// </param>
	/// <param name="watchMempool">Whether to look at the mempool of the node at all. Without it the wallet learns about transactions when they are confirmed.</param>
	public WalletSynchronizer(
		IRPCClient rpcClient,
		KeyManager keyManager,
		AllTransactionStore transactionStore,
		TransactionProcessor transactionProcessor,
		EventBus eventBus,
		Action? onSynchronizedFirst = null,
		bool watchMempool = true,
		int? maxParallelBlockDownloads = null)
	{
		_rpcClient = rpcClient;
		_keyManager = keyManager;
		_transactionStore = transactionStore;
		_transactionProcessor = transactionProcessor;
		_eventBus = eventBus;
		_onSynchronizedFirst = onSynchronizedFirst;
		_watchMempool = watchMempool;

		// Blocks are parsed where they are downloaded, so this is about the processors of this machine as much as about the node.
		_maxParallelBlockDownloads = maxParallelBlockDownloads ?? Math.Clamp(Environment.ProcessorCount, 2, 8);
	}

	/// <summary>Completes when the wallet processed every block of the node for the first time.</summary>
	public Task InitialSynchronizationFinished => _initialSynchronizationFinished.Task;

	public WalletSyncStatus Status
	{
		get
		{
			lock (_statusLock)
			{
				return _status;
			}
		}
	}

	/// <inheritdoc />
	protected override async Task ExecuteAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TimeSpan delay;

			try
			{
				delay = await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				WalletSyncStatus status = IsNodeFailure(ex)
					? new WalletSyncStatus.NodeUnavailable(DescribeNodeFailure(ex))
					: new WalletSyncStatus.Failed(ex.Message);

				// The same failure is logged once, not every few seconds.
				if (Status != status)
				{
					if (status is WalletSyncStatus.NodeUnavailable { Reason: var reason })
					{
						Logger.LogWarning($"The wallet cannot synchronize with the Bitcoin node: {reason}");
						Logger.LogDebug(ex);
					}
					else
					{
						Logger.LogError(ex);
					}
				}

				SetStatus(status);
				delay = RetryPeriod;
			}

			try
			{
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		Logger.LogDebug("Wallet synchronizer's execution was stopped.");
	}

	/// <summary>
	/// Brings the wallet as far as the node is at the moment.
	/// </summary>
	/// <returns>How long to wait before doing this again.</returns>
	internal async Task<TimeSpan> SynchronizeAsync(CancellationToken cancellationToken)
	{
		var info = await _rpcClient.GetBlockchainInfoAsync(cancellationToken).ConfigureAwait(false);
		await AssertChainOfWalletAsync(cancellationToken).ConfigureAwait(false);

		var nodeHeight = (uint)info.Blocks;
		var nodeHeaders = (uint)Math.Max(info.Headers, info.Blocks);

		if (_keyManager.GetBestHeight() is null)
		{
			// A new wallet: nothing concerns it up to the tip of the chain. The headers tell where that is, even while the node is still downloading the blocks.
			_keyManager.StartAt(nodeHeaders);
			Logger.LogInfo($"Wallet: the history of the new wallet starts after block {nodeHeaders}.");
		}

		await RememberBlockAtWalletHeightAsync(nodeHeight, cancellationToken).ConfigureAwait(false);
		await RewindToCommonBlockAsync(nodeHeight, cancellationToken).ConfigureAwait(false);

		var walletHeight = _keyManager.GetBestHeight()!.Height;

		if (walletHeight < nodeHeight)
		{
			try
			{
				await ProcessBlocksAsync(walletHeight, nodeHeight, info.Pruned, cancellationToken).ConfigureAwait(false);
			}
			catch (BlockPrunedException ex)
			{
				if (Status is not WalletSyncStatus.BlockPruned)
				{
					Logger.LogError(ex.Message);
				}

				SetStatus(new WalletSyncStatus.BlockPruned(ex.Height));
				return RetryPeriod;
			}

			// More blocks may have arrived in the meantime.
			return TimeSpan.Zero;
		}

		// The block of the newest header is on its way most of the time. More than that means that the node is catching up.
		if (walletHeight > nodeHeight || nodeHeaders > nodeHeight + 1)
		{
			SetStatus(new WalletSyncStatus.NodeSynchronizing(nodeHeight, nodeHeaders));
			return IdlePeriod;
		}

		if (!_initialSynchronizationFinished.Task.IsCompleted)
		{
			_onSynchronizedFirst?.Invoke();
		}

		SetStatus(new WalletSyncStatus.Synchronized(walletHeight));
		AnnounceWalletHeight(walletHeight);
		_initialSynchronizationFinished.TrySetResult();

		var isMempoolBacklogLeft = _watchMempool && await ProcessMempoolAsync(cancellationToken).ConfigureAwait(false);
		return isMempoolBacklogLeft ? TimeSpan.Zero : IdlePeriod;
	}

	/// <summary>
	/// Makes sure that the node is on the chain of the wallet, and not on another network.
	/// </summary>
	private async Task AssertChainOfWalletAsync(CancellationToken cancellationToken)
	{
		if (_isChainVerified)
		{
			return;
		}

		var network = _keyManager.GetNetwork();
		var genesisHash = await _rpcClient.GetBlockHashAsync(0, cancellationToken).ConfigureAwait(false);

		if (genesisHash != network.GenesisHash)
		{
			throw new WrongChainException($"The Bitcoin node is not on the {network} network that the wallet belongs to.");
		}

		_isChainVerified = true;
	}

	/// <summary>
	/// A wallet that knows its height but not the block at that height (a new one, or one that was told where to start) takes
	/// the block of the node for it. Without that it could not tell later whether the blocks that it processed on top are still part of the chain.
	/// </summary>
	private async Task RememberBlockAtWalletHeightAsync(uint nodeHeight, CancellationToken cancellationToken)
	{
		var (bestHeight, blockHashes) = _keyManager.GetProcessedBlocks();

		if (bestHeight is null || !blockHashes.IsEmpty || bestHeight.Height > nodeHeight)
		{
			return;
		}

		var blockHash = await _rpcClient.GetBlockHashAsync((int)bestHeight.Height, cancellationToken).ConfigureAwait(false);
		_keyManager.SetBestBlock(bestHeight, blockHash);
	}

	/// <summary>
	/// Compares the last blocks that the wallet processed with the blocks of the node. If the chain was reorganized,
	/// the blocks that are not part of it any more are undone and the wallet goes back to the last block that it has in common with the node.
	/// </summary>
	private async Task RewindToCommonBlockAsync(uint nodeHeight, CancellationToken cancellationToken)
	{
		var (bestHeight, blockHashes) = _keyManager.GetProcessedBlocks();

		if (bestHeight is null || blockHashes.IsEmpty)
		{
			return;
		}

		var walletHeight = bestHeight.Height;
		long oldestKnownHeight = (long)walletHeight - (blockHashes.Length - 1);
		long commonHeight = Math.Min(walletHeight, nodeHeight);

		// A node that is far behind the wallet has nothing to compare. It has to catch up first.
		if (commonHeight < oldestKnownHeight)
		{
			return;
		}

		while (commonHeight >= oldestKnownHeight)
		{
			var nodeBlockHash = await _rpcClient.GetBlockHashAsync((int)commonHeight, cancellationToken).ConfigureAwait(false);

			if (nodeBlockHash == blockHashes[(int)(commonHeight - oldestKnownHeight)])
			{
				break;
			}

			commonHeight--;
		}

		if (commonHeight == walletHeight)
		{
			return;
		}

		// The newest block goes first.
		for (long height = walletHeight; height > commonHeight; height--)
		{
			var blockHash = blockHashes[(int)(height - oldestKnownHeight)];
			Logger.LogInfo($"Wallet: block {height} ({blockHash}) is not part of the chain of the node any more. Its transactions are unconfirmed again.");

			_transactionProcessor.UndoBlock(new ChainHeight((uint)height));
			_transactionStore.ReleaseToMempoolFromBlock(blockHash);
		}

		// When no remembered block is left in the chain of the node, this is where the chains may have parted at the latest.
		_keyManager.SetMaxBestHeight(new ChainHeight((uint)Math.Max(commonHeight, 0)));
		AnnounceWalletHeight((uint)Math.Max(commonHeight, 0));
	}

	/// <summary>
	/// Processes the blocks above the height of the wallet, up to the given height of the node.
	/// </summary>
	private async Task ProcessBlocksAsync(uint walletHeight, uint nodeHeight, bool isNodePruned, CancellationToken cancellationToken)
	{
		var (_, knownBlockHashes) = _keyManager.GetProcessedBlocks();
		var previousBlockHash = knownBlockHashes.IsEmpty ? null : knownBlockHashes[^1];

		using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var downloads = new Queue<(uint Height, uint256 BlockHash, Task<Block> Block)>();
		var hashesToDownload = new Queue<(uint Height, uint256 BlockHash)>();
		var nextHeightToAsk = walletHeight + 1;
		var stopwatch = Stopwatch.StartNew();
		var startHeight = walletHeight;
		var isHeightUnsaved = false;

		ReportProgress(walletHeight, nodeHeight, force: true);

		try
		{
			while (true)
			{
				// Keep the downloads going. The blocks are processed in their order, one after the other.
				while (downloads.Count < _maxParallelBlockDownloads)
				{
					if (hashesToDownload.Count == 0 && nextHeightToAsk <= nodeHeight)
					{
						var count = (int)Math.Min(BlockHashBatchSize, nodeHeight - nextHeightToAsk + 1);
						var blockHashes = await GetBlockHashesAsync(nextHeightToAsk, count, downloadCts.Token).ConfigureAwait(false);

						for (int i = 0; i < blockHashes.Length; i++)
						{
							hashesToDownload.Enqueue((nextHeightToAsk + (uint)i, blockHashes[i]));
						}

						nextHeightToAsk += (uint)count;
					}

					if (!hashesToDownload.TryDequeue(out var next))
					{
						break;
					}

					downloads.Enqueue((next.Height, next.BlockHash, _rpcClient.GetBlockAsync(next.BlockHash, downloadCts.Token)));
				}

				if (!downloads.TryDequeue(out var download))
				{
					break;
				}

				Block block;
				try
				{
					block = await download.Block.ConfigureAwait(false);
				}
				catch (RPCException ex) when (isNodePruned && IsBlockNotAvailable(ex))
				{
					throw new BlockPrunedException(download.Height, ex);
				}

				// The chain was reorganized while the blocks were on their way. The next round sorts it out.
				if (previousBlockHash is not null && block.Header.HashPrevBlock != previousBlockHash)
				{
					Logger.LogInfo($"Wallet: block {download.Height} ({download.BlockHash}) does not follow the last processed block. The chain was reorganized.");
					return;
				}

				var results = _transactionProcessor.ProcessBlock(block, new ChainHeight(download.Height));

				var isLastBlock = download.Height == nodeHeight;
				var now = DateTimeOffset.UtcNow;
				var persist = results.Count > 0 || isLastBlock || now - _lastPersisted >= PersistPeriod;

				_keyManager.SetBestBlock(new ChainHeight(download.Height), download.BlockHash, toFile: persist);
				previousBlockHash = download.BlockHash;
				isHeightUnsaved = !persist;

				if (persist)
				{
					_lastPersisted = now;
				}

				ReportProgress(download.Height, nodeHeight, force: isLastBlock);
			}
		}
		finally
		{
			// What was processed is kept, however this ends.
			if (isHeightUnsaved)
			{
				_keyManager.ToFile();
				_lastPersisted = DateTimeOffset.UtcNow;
			}

			// Nobody waits for the downloads that are still on their way.
			await downloadCts.CancelAsync().ConfigureAwait(false);

			foreach (var (_, _, pendingDownload) in downloads)
			{
				_ = pendingDownload.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
			}
		}

		var processedCount = nodeHeight - startHeight;
		if (processedCount > 1)
		{
			Logger.LogInfo($"Wallet: processed {processedCount} blocks up to height {nodeHeight} in {stopwatch.Elapsed.TotalSeconds:0.#} seconds.");
		}
	}

	private async Task<uint256[]> GetBlockHashesAsync(uint firstHeight, int count, CancellationToken cancellationToken)
	{
		if (count == 1)
		{
			return [await _rpcClient.GetBlockHashAsync((int)firstHeight, cancellationToken).ConfigureAwait(false)];
		}

		var batch = _rpcClient.PrepareBatch();
		var requests = Enumerable.Range(0, count)
			.Select(i => batch.GetBlockHashAsync((int)firstHeight + i, cancellationToken))
			.ToArray();

		await batch.SendBatchAsync(cancellationToken).ConfigureAwait(false);
		return await Task.WhenAll(requests).ConfigureAwait(false);
	}

	/// <summary>Makes the next round ask the node about its mempool, however recently that was done. For tests.</summary>
	internal void ForceMempoolPoll() => _nextMempoolPoll = DateTimeOffset.MinValue;

	/// <summary>
	/// Looks at the transactions that arrived in the mempool of the node since the last time.
	/// </summary>
	/// <returns><c>true</c> if there are transactions left that were not looked at yet.</returns>
	private async Task<bool> ProcessMempoolAsync(CancellationToken cancellationToken)
	{
		if (DateTimeOffset.UtcNow >= _nextMempoolPoll)
		{
			var stopwatch = Stopwatch.StartNew();
			var txids = await _rpcClient.GetRawMempoolAsync(cancellationToken).ConfigureAwait(false);
			var known = _mempoolTxids;

			foreach (var txid in txids)
			{
				if (!known.Contains(txid))
				{
					_mempoolBacklog.Enqueue(txid);
				}
			}

			_mempoolTxids = [.. txids];

			// A node that takes long to tell its mempool, because it is far away or its mempool is huge, is asked less often.
			_nextMempoolPoll = DateTimeOffset.UtcNow + TimeSpan.FromTicks(Math.Max(IdlePeriod.Ticks * 4 / 5, stopwatch.Elapsed.Ticks * 20));
		}

		var fetched = 0;
		var batchTxids = new List<uint256>(MempoolBatchSize);

		while (fetched < MaxMempoolTransactionsPerRound && _mempoolBacklog.Count > 0)
		{
			batchTxids.Clear();

			while (batchTxids.Count < MempoolBatchSize && _mempoolBacklog.TryDequeue(out var txid))
			{
				// It may have left the mempool while it was waiting here.
				if (_mempoolTxids.Contains(txid))
				{
					batchTxids.Add(txid);
				}
			}

			if (batchTxids.Count == 0)
			{
				continue;
			}

			foreach (var transaction in await GetMempoolTransactionsAsync(batchTxids, cancellationToken).ConfigureAwait(false))
			{
				ProcessMempoolTransaction(transaction);
			}

			fetched += batchTxids.Count;
		}

		return _mempoolBacklog.Count > 0;
	}

	private async Task<IEnumerable<Transaction>> GetMempoolTransactionsAsync(IReadOnlyList<uint256> txids, CancellationToken cancellationToken)
	{
		var batch = _rpcClient.PrepareBatch();
		var requests = txids
			.Select(txid => batch.GetRawTransactionAsync(txid, throwIfNotFound: false, cancellationToken))
			.ToArray();

		await batch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

		// A transaction that left the mempool in the meantime is not found.
		return (await Task.WhenAll(requests).ConfigureAwait(false)).Where(transaction => transaction is not null);
	}

	private void ProcessMempoolTransaction(Transaction transaction)
	{
		if (!_transactionProcessor.ConcernsWallet(transaction))
		{
			return;
		}

		var txid = transaction.GetHash();

		// The wallet knows its own transactions already.
		if (_transactionStore.TryGetTransaction(txid, out _))
		{
			return;
		}

		try
		{
			_transactionProcessor.Process(new SmartTransaction(transaction, Height.Mempool));
		}
		catch (Exception ex)
		{
			Logger.LogWarning($"Wallet: failed to process the unconfirmed transaction {txid}: {ex}");
		}
	}

	private void SetStatus(WalletSyncStatus status)
	{
		lock (_statusLock)
		{
			if (_status == status)
			{
				return;
			}

			_status = status;
		}

		_eventBus.Publish(new WalletSyncStatusChanged(status));
	}

	/// <summary>
	/// Tells how far the processing of the blocks got. The status is always current, but it is not announced block by block.
	/// </summary>
	private void ReportProgress(uint height, uint tipHeight, bool force)
	{
		var status = new WalletSyncStatus.Scanning(height, tipHeight);

		lock (_statusLock)
		{
			_status = status;
		}

		var now = DateTimeOffset.UtcNow;

		if (force || now - _lastProgressAnnounced >= ProgressPeriod)
		{
			_lastProgressAnnounced = now;
			_eventBus.Publish(new WalletSyncStatusChanged(status));
			AnnounceWalletHeight(height);
		}
	}

	private void AnnounceWalletHeight(uint height)
	{
		if (_lastAnnouncedHeight != height)
		{
			_lastAnnouncedHeight = height;
			_eventBus.Publish(new ClientTipHeightChanged(height));
		}
	}

	private static bool IsBlockNotAvailable(RPCException ex) =>
		ex.RPCCode == RPCErrorCode.RPC_MISC_ERROR
		|| ex.Message.Contains("pruned", StringComparison.OrdinalIgnoreCase)
		|| ex.Message.Contains("not available", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Tells failures of the node, or of the way to it, from failures of this application.
	/// </summary>
	private static bool IsNodeFailure(Exception ex) =>
		ex is RPCException or HttpRequestException or HttpIOException or SocketException or TimeoutException or OperationCanceledException or WrongChainException or BlockPrunedException
		|| (ex is AggregateException aggregate && aggregate.InnerExceptions.All(IsNodeFailure))
		|| (ex.InnerException is { } inner && IsNodeFailure(inner));

	private static string DescribeNodeFailure(Exception ex) =>
		ex switch
		{
			WrongChainException or BlockPrunedException => ex.Message,
			RPCException rpcException => $"The Bitcoin node refused a request ({rpcException.Message}).",
			HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "The Bitcoin node refused the RPC credentials.",
			HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "The Bitcoin node does not allow RPC requests from this computer.",
			OperationCanceledException or TimeoutException => "The Bitcoin node did not answer in time.",
			AggregateException { InnerExceptions: [var single, ..] } => DescribeNodeFailure(single),
			_ => $"The Bitcoin node cannot be reached ({ex.Message})."
		};

	private class WrongChainException(string message) : Exception(message);

	private class BlockPrunedException(uint height, Exception innerException)
		: Exception(new WalletSyncStatus.BlockPruned(height).ToUserFriendlyString(), innerException)
	{
		public uint Height { get; } = height;
	}
}
