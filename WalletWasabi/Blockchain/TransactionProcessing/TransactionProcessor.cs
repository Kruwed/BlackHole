using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Cache;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Blockchain.TransactionProcessing;

public class TransactionProcessor(
	AllTransactionStore transactionStore,
	KeyManager keyManager,
	EventBus eventBus)
{
	/// <remarks>Intentionally, <c>static</c> to avoid modifying smart transactions from multiple threads.</remarks>
	private static object Lock { get; } = new();

	private static readonly TimeSpan AwareExpiration = TimeSpan.FromMinutes(30);

	public AllTransactionStore TransactionStore { get; } = transactionStore;
	private readonly MemoryCache<uint256, bool> _aware = new(AwareExpiration);

	public KeyManager KeyManager { get; } = keyManager;

	public CoinsRegistry Coins { get; } = new();

	public IEnumerable<ProcessedResult> Process(IEnumerable<SmartTransaction> txs)
	{
		var results = new List<ProcessedResult>();

		lock (Lock)
		{
			foreach (var tx in txs)
			{
				results.Add(ProcessNoLock(tx));
			}
		}

		foreach (var result in results.Where(x => x.IsNews))
		{
			eventBus.Publish(new WalletRelevantTransactionProcessed(result));
		}

		return results;
	}

	public IEnumerable<ProcessedResult> Process(params SmartTransaction[] txs)
		=> Process(txs as IEnumerable<SmartTransaction>);

	/// <summary>
	/// Processes the transactions of a block that concern the wallet.
	/// </summary>
	/// <returns>What became of those transactions. Empty if nothing in the block concerns the wallet.</returns>
	public IReadOnlyList<ProcessedResult> ProcessBlock(Block block, ChainHeight height)
	{
		var blockHash = block.GetHash();
		var blockTime = block.Header.BlockTime;
		var transactions = block.Transactions;
		var results = new List<ProcessedResult>();

		lock (Lock)
		{
			// Using a key for the first time makes the wallet watch more keys. The transactions of the block that were
			// passed over by then may pay to those, so the block is gone through again until no more keys turn up.
			var processedIndexes = new HashSet<int>();
			var lateTransactionIds = new HashSet<uint256>();
			var isFirstPass = true;
			int keyCount;

			do
			{
				keyCount = KeyManager.GetKeyCount();

				for (int i = 0; i < transactions.Count; i++)
				{
					var transaction = transactions[i];
					bool process;

					if (processedIndexes.Contains(i))
					{
						// Once is enough, unless a coin that the transaction spends turned up only after it was processed.
						process = lateTransactionIds.Count > 0 && transaction.Inputs.Any(input => lateTransactionIds.Contains(input.PrevOut.Hash));
					}
					else
					{
						process = ConcernsWallet(transaction);
					}

					if (process)
					{
						var tx = new SmartTransaction(transaction, height, blockHash, blockIndex: i, firstSeen: blockTime);

						if (processedIndexes.Add(i) && !isFirstPass)
						{
							lateTransactionIds.Add(tx.GetHash());
						}

						results.Add(ProcessNoLock(tx));
					}
				}

				isFirstPass = false;
			}
			while (KeyManager.GetKeyCount() != keyCount);
		}

		foreach (var result in results.Where(x => x.IsNews))
		{
			eventBus.Publish(new WalletRelevantTransactionProcessed(result));
		}

		return results;
	}

	/// <summary>
	/// Tells if the transaction has anything to do with the wallet: it pays to the wallet, it spends a coin of the wallet,
	/// or it spends what a transaction of the wallet spends too.
	/// </summary>
	/// <remarks>Cheap enough to be asked about every transaction of the chain.</remarks>
	public bool ConcernsWallet(Transaction transaction)
	{
		foreach (var output in transaction.Outputs)
		{
			if (KeyManager.TryGetKeyForScriptPubKey(output.ScriptPubKey, out _))
			{
				return true;
			}
		}

		if (!transaction.IsCoinBase)
		{
			foreach (var input in transaction.Inputs)
			{
				if (Coins.IsKnownPrevOut(input.PrevOut))
				{
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Was the transaction already processed by the transaction processor?
	/// </summary>
	public bool IsAware(uint256 tx)
	{
		lock (Lock)
		{
			return _aware.TryGet(tx, out _);
		}
	}

	public ProcessedResult Process(SmartTransaction tx)
	{
		ProcessedResult result;
		lock (Lock)
		{
			_aware.TryAdd(tx.GetHash(), true, AwareExpiration);
			result = ProcessNoLock(tx);
		}

		if (result.IsNews)
		{
			eventBus.Publish(new WalletRelevantTransactionProcessed(result));
		}

		return result;
	}

	private ProcessedResult ProcessNoLock(SmartTransaction tx)
	{
		var result = new ProcessedResult(tx);

		// We do not care about non-witness transactions for other than mempool cleanup.
		if (!tx.Transaction.SegWitInvolved())
		{
			return result;
		}

		uint256 txId = tx.GetHash();

		// Never let an unconfirmed transaction spend our coins without our signatures.
		// Context: https://github.com/spesmilo/electrum/security/advisories/GHSA-6jp4-8v48-cc5v
		var myInputs = Coins.GetMyInputs(tx);
		if (!tx.Confirmed && !AreSignaturesValid(tx.Transaction, myInputs))
		{
			return result;
		}

		// If we already have the transaction, then let's work on that.
		if (TransactionStore.TryGetTransaction(txId, out var foundTx))
		{
			foundTx.TryUpdate(tx);
			tx = foundTx;
			result = new ProcessedResult(tx);
		}

		if (!tx.Transaction.IsCoinBase && !Coins.IsKnown(txId)) // Transactions we already have and processed would be "double spends" but they shouldn't.
		{
			var doubleSpentSpenders = new List<SmartCoin>();
			var doubleSpentCoins = new List<SmartCoin>();
			foreach (var txIn in tx.Transaction.Inputs)
			{
				if (Coins.TryGetCoinsByInputPrevOut(txIn.PrevOut, out var coins))
				{
					doubleSpentSpenders.AddRange(coins);
				}

				if (Coins.TryGetByOutPoint(txIn.PrevOut, out var coin) && coin.IsSpent())
				{
					doubleSpentCoins.Add(coin);
				}
			}

			var doubleSpentTransactions = doubleSpentCoins.Select(x => x.SpenderTransaction!).Concat(doubleSpentSpenders.Select(x => x.Transaction)).ToHashSet();

			if (doubleSpentTransactions.Count > 0)
			{
				tx.SetReplacement();
			}

			if (tx.Height == Height.Mempool)
			{
				// if the received transaction is spending at least one input already
				// spent by a previous unconfirmed transaction then it is not considered a double
				// spending transaction but a replacement transaction.
				var isReplacementTx = doubleSpentSpenders.Count != 0;
				if (isReplacementTx)
				{
					// Undo the replaced transaction by removing the coins it created (if other coin
					// spends it, remove that too and so on) and restoring those that it replaced.
					// After undoing the replaced transaction it will process the replacement transaction.
					var replacedTxId = doubleSpentSpenders.First().TransactionId;
					var (replaced, restored) = Coins.Undo(replacedTxId);

					result.ReplacedCoins.AddRange(replaced);
					result.RestoredCoins.AddRange(restored);
				}
			}
			else // new confirmation always enjoys priority
			{
				foreach (var doubleSpentTx in doubleSpentTransactions)
				{
					var unconfirmedDoubleSpentTxId = doubleSpentTx.GetHash();
					if (TransactionStore.MempoolStore.TryGetTransaction(unconfirmedDoubleSpentTxId, out var replacedTx) && replacedTx.IsReplacement)
					{
						var (replaced, restored) = Coins.Undo(unconfirmedDoubleSpentTxId);

						result.ReplacedCoins.AddRange(replaced);
						result.RestoredCoins.AddRange(restored);
					}
					else
					{
						// remove double spent spenders recursively (if other coin spends it, remove that too and so on), will add later if they came to our keys
						foreach (var doubleSpentTxid in doubleSpentSpenders.Select(x => x.TransactionId).Distinct())
						{
							Coins.Undo(doubleSpentTxid);
						}

						result.SuccessfullyDoubleSpentCoins.AddRange(doubleSpentSpenders);
					}
				}
			}

			// Recursively double spent transactions could be here.
			foreach (var doubleSpentTx in result.ReplacedCoins.Select(coin => coin.Transaction))
			{
				doubleSpentTransactions.Add(doubleSpentTx);
			}

			foreach (var replacedTransactionId in doubleSpentTransactions.Select(x => x.GetHash()))
			{
				TransactionStore.MempoolStore.TryRemove(replacedTransactionId, out _);
			}
		}

		for (var i = 0U; i < tx.Transaction.Outputs.Count; i++)
		{
			// If transaction received to any of the wallet keys:
			var output = tx.Transaction.Outputs[i];
			if (KeyManager.TryGetKeyForScriptPubKey(output.ScriptPubKey, out HdPubKey? foundKey))
			{
				KeyManager.SetKeyState(KeyState.Used, foundKey);

				SmartCoin newCoin = new(tx, i, foundKey);

				result.ReceivedCoins.Add(newCoin);

				// If we did not have it.
				if (Coins.TryAdd(newCoin))
				{
					result.NewlyReceivedCoins.Add(newCoin);
				}
				else // If we had this coin already.
				{
					if (newCoin.Height != Height.Mempool) // Update the height of this old coin we already had.
					{
						if (Coins.AsAllCoinsView().TryGetByOutPoint(new OutPoint(txId, i), out var oldCoin)) // Just to be sure, it is a concurrent collection.
						{
							result.NewlyConfirmedReceivedCoins.Add(newCoin);
							oldCoin.Height = newCoin.Height;
						}
					}
				}
			}
		}

		// If spends any of our coin
		foreach (var coin in myInputs)
		{
			var alreadyKnown = coin.SpenderTransaction == tx;
			result.SpentCoins.Add(coin);
			Coins.Spend(coin, tx);
			result.RestoredCoins.Remove(coin);

			if (!alreadyKnown)
			{
				result.NewlySpentCoins.Add(coin);
			}

			if (tx.Confirmed)
			{
				result.NewlyConfirmedSpentCoins.Add(coin);
			}
		}

		if (tx.WalletInputs.Count != 0 || tx.WalletOutputs.Count != 0)
		{
			TransactionStore.AddOrUpdate(tx);
		}

		BlockchainAnalyzer.Analyze(result.Transaction);
		SyncAnonScoresFromSharedTransaction(result.Transaction);

		return result;
	}

	/// <summary>
	/// Every loaded wallet shares a single transaction store, hence the same <see cref="SmartTransaction"/>
	/// instances. <see cref="SmartTransaction.WalletOutputs"/> is a set keyed by outpoint
	/// (<see cref="SmartCoin"/> equality is outpoint-only), so when the same wallet is loaded more than once
	/// only the first instance's coin is kept there and gets its anonymity set calculated by the
	/// <see cref="BlockchainAnalyzer"/>. The other instances' <see cref="HdPubKey"/> objects for the very same
	/// output are never analyzed and keep the <see cref="HdPubKey.DefaultHighAnonymitySet"/> sentinel
	/// (<see cref="int.MaxValue"/>), which makes the coin look fully private and silently excludes it from
	/// coinjoins. Copy the calculated value onto this wallet's own key.
	/// <remarks>Context: https://github.com/WalletWasabi/WalletWasabi/issues/7453</remarks>
	/// </summary>
	private void SyncAnonScoresFromSharedTransaction(SmartTransaction tx)
	{
		foreach (var representative in tx.WalletOutputs)
		{
			if (KeyManager.TryGetKeyForScriptPubKey(representative.ScriptPubKey, out HdPubKey? ownKey)
				&& !ReferenceEquals(ownKey, representative.HdPubKey))
			{
				ownKey.SetAnonymitySet(representative.AnonymitySet);
			}
		}
	}

	private static bool AreSignaturesValid(Transaction tx, IReadOnlyList<SmartCoin> myInputs)
	{
		var myCoins = myInputs.DistinctBy(x => x.Outpoint).ToDictionary(x => x.Outpoint, x => x.Coin);
		var allInputsAreMine = tx.Inputs.All(x => myCoins.ContainsKey(x.PrevOut));
		var precomputedData = allInputsAreMine
			? tx.PrecomputeTransactionData(tx.Inputs.Select(x => myCoins[x.PrevOut].TxOut).ToArray())
			: tx.PrecomputeTransactionData();

		// Taproot signatures commit to every spent output, so we can only verify them when all the inputs are ours.
		// We never RBF a transaction with foreign inputs, so skipping those cannot trick us into signing a payment.
		return tx.Inputs.AsIndexedInputs().All(input =>
			!myCoins.TryGetValue(input.PrevOut, out var coin)
			|| (!allInputsAreMine && coin.ScriptPubKey.IsScriptType(ScriptType.Taproot))
			|| input.VerifyScript(coin, ScriptVerify.Standard, precomputedData, out _));
	}

	public void UndoBlock(ChainHeight blockHeight)
	{
		Coins.SwitchToUnconfirmFromBlock(blockHeight);
	}
}
