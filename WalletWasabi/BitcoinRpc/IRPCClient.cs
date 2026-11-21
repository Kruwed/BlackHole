using NBitcoin.RPC;

namespace WalletWasabi.BitcoinRpc;

public interface IRPCClient
{
	Network Network { get; }
	RPCCredentialString CredentialString { get; }

	Task<uint256> GetBestBlockHashAsync(CancellationToken cancellationToken = default);

	Task<Block> GetBlockAsync(uint256 blockId, CancellationToken cancellationToken = default);

	Task<Block> GetBlockAsync(uint blockHeight, CancellationToken cancellationToken = default);

	Task<BlockHeader> GetBlockHeaderAsync(uint256 blockHash, CancellationToken cancellationToken = default);

	Task<uint256[]> GenerateAsync(int blockCount, CancellationToken cancellationToken = default);

	Task StopAsync(CancellationToken cancellationToken = default);

	Task<BlockchainInfo> GetBlockchainInfoAsync(CancellationToken cancellationToken = default);

	Task<PeerInfo[]> GetPeersInfoAsync(CancellationToken cancellationToken = default);

	Task<TimeSpan> UptimeAsync(CancellationToken cancellationToken = default);

	Task<uint256> SendRawTransactionAsync(Transaction transaction, CancellationToken cancellationToken = default);

	/// <summary>
	/// Tells which transactions the node is broadcasting privately at the moment: the ones that it was handed and has
	/// not heard back from the network yet. Until it hears a transaction back, the node does not have it in its mempool.
	/// Bitcoin Core knows the request since version 31.
	/// </summary>
	Task<uint256[]> GetPrivateBroadcastQueueAsync(CancellationToken cancellationToken = default);

	/// <summary>Makes the node stop broadcasting a transaction privately.</summary>
	/// <returns>Whether the node was broadcasting it. It is not if it heard the transaction back already, or was never handed it.</returns>
	Task<bool> AbortPrivateBroadcastAsync(uint256 txid, CancellationToken cancellationToken = default);

	Task<MempoolEntry> GetMempoolEntryAsync(uint256 txid, bool throwIfNotFound = true, CancellationToken cancellationToken = default);

	Task<uint256[]> GetRawMempoolAsync(CancellationToken cancellationToken = default);

	Task<MemPoolInfo> GetMempoolInfoAsync(CancellationToken cancellationToken = default);

	Task<EstimateSmartFeeResponse> EstimateSmartFeeAsync(int confirmationTarget, EstimateSmartFeeMode estimateMode = EstimateSmartFeeMode.Conservative, CancellationToken cancellationToken = default);

	Task<GetTxOutResponse?> GetTxOutAsync(uint256 txid, int index, bool includeMempool = true, CancellationToken cancellationToken = default);

	IRPCClient PrepareBatch();

	Task<uint256> SendToAddressAsync(BitcoinAddress address, Money amount, bool replaceable = false, CancellationToken cancellationToken = default);

	Task<uint256> GetBlockHashAsync(int height, CancellationToken cancellationToken = default);

	Task InvalidateBlockAsync(uint256 blockHash, CancellationToken cancellationToken = default);

	Task<BumpResponse> BumpFeeAsync(uint256 txid, CancellationToken cancellationToken = default);

	Task<Transaction> GetRawTransactionAsync(uint256 txid, bool throwIfNotFound = true, CancellationToken cancellationToken = default);

	Task<IEnumerable<Transaction>> GetRawTransactionsAsync(IEnumerable<uint256> txids, CancellationToken cancellationToken);

	/// <summary>
	/// Asks for a transaction of the given block. A node finds a transaction that way without an index of all transactions.
	/// </summary>
	/// <returns>The transaction, or null if the node does not find it in that block.</returns>
	Task<Transaction?> GetRawTransactionFromBlockAsync(uint256 txid, uint256 blockHash, CancellationToken cancellationToken = default);

	/// <summary>
	/// Tells for a transaction of the given block at which heights the outputs were created that it spends.
	/// </summary>
	/// <returns>
	/// One height for every input, in the order of the inputs, with null where the node does not tell one, as for the
	/// input of a coinbase transaction. Null if the node does not find the transaction in that block or cannot tell any
	/// height: an older node does not know the request, and a pruned one may not have the data any more.
	/// </returns>
	Task<IReadOnlyList<int?>?> GetPrevoutHeightsAsync(uint256 txid, uint256 blockHash, CancellationToken cancellationToken = default);

	Task<int> GetBlockCountAsync(CancellationToken cancellationToken = default);

	Task<BitcoinAddress> GetNewAddressAsync(CancellationToken cancellationToken = default);

	Task<SignRawTransactionResponse> SignRawTransactionWithWalletAsync(SignRawTransactionRequest request, CancellationToken cancellationToken = default);

	Task<UnspentCoin[]> ListUnspentAsync(/*CancellationToken cancellationToken*/);

	Task SendBatchAsync(CancellationToken cancellationToken = default);

	Task<GetBlockRPCResponse> GetVerboseBlockAsync(uint256 blockHash, CancellationToken cancellationToken = default);

	Task<uint256[]> GenerateToAddressAsync(int nBlocks, BitcoinAddress address, CancellationToken cancellationToken = default);

	Task<RPCClient> CreateWalletAsync(string walletNameOrPath, CreateWalletOptions? options = null, CancellationToken cancellationToken = default);

	Task<ScanTxoutSetResponse> StartScanTxoutSetAsync(ScanTxoutSetParameters parameters, CancellationToken cancellationToken = default);
}
