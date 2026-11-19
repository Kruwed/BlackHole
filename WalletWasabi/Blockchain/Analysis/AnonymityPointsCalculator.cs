using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Blockchain.Analysis;

/// <summary>
/// Finds out the anonymity points of a transaction of the wallet, see <see cref="AnonymityPoints"/>.
/// <para>
/// The points depend on the transactions that created the inputs: was the output that an input spends a matched
/// output there? The wallet has its own transactions. For the inputs of others, which are most of the inputs of a
/// coinjoin, it asks the node. A node with an index of all transactions simply has them. Any other node is told in
/// which block to look, and that block is found through the transaction that spends the output.
/// </para>
/// </summary>
public class AnonymityPointsCalculator
{
	/// <summary>How many inputs the node is asked about in one go.</summary>
	public const int BatchSize = 20;

	/// <summary>How long the node may take to answer one batch of requests.</summary>
	private static readonly TimeSpan BatchTimeout = TimeSpan.FromMinutes(5);

	/// <summary>How many transactions of others the calculator remembers at most.</summary>
	private const int MaxRememberedTransactions = 5_000;

	private readonly IRPCClient _rpcClient;
	private readonly ITransactionStore _transactionStore;

	/// <summary>
	/// What the node told about transactions of others: which of their outputs are matched. The outputs of one coinjoin
	/// are spent by many later transactions, and a calculation that the node did not answer completely is repeated, so
	/// the node is asked for a transaction only once while the application runs.
	/// </summary>
	private readonly Dictionary<uint256, bool[]> _matchedOutputsOfOthers = [];

	public AnonymityPointsCalculator(IRPCClient rpcClient, ITransactionStore transactionStore)
	{
		_rpcClient = rpcClient;
		_transactionStore = transactionStore;
	}

	/// <returns>The points of the transaction, or null if the node could not tell enough about its inputs.</returns>
	/// <remarks>The method is not thread safe: one calculation at a time.</remarks>
	public async Task<int?> CalculateAsync(SmartTransaction smartTransaction, CancellationToken cancellationToken)
	{
		var transaction = smartTransaction.Transaction;
		var matchedOutputs = AnonymityPoints.CountMatchedOutputs(transaction);

		// Without matched outputs there are no points, and the input of a coinbase transaction was nobody's output.
		if (matchedOutputs == 0 || transaction.IsCoinBase)
		{
			return 0;
		}

		var matchedInputs = 0;
		var inputsToAskAbout = new List<OutPoint>();

		foreach (var prevOut in transaction.Inputs.Select(input => input.PrevOut))
		{
			if (_transactionStore.TryGetTransaction(prevOut.Hash, out var parent))
			{
				matchedInputs += AnonymityPoints.IsMatchedOutput(parent.Transaction, prevOut.N) ? 1 : 0;
			}
			else if (_matchedOutputsOfOthers.TryGetValue(prevOut.Hash, out var matchedOutputsOfParent))
			{
				matchedInputs += WasMatchedOutput(matchedOutputsOfParent, prevOut) ? 1 : 0;
			}
			else
			{
				inputsToAskAbout.Add(prevOut);
			}
		}

		var search = new ParentSearch(_rpcClient, smartTransaction);
		var unknownInputs = 0;

		foreach (var prevOuts in inputsToAskAbout.Chunk(BatchSize))
		{
			// The points are never more than the matched outputs: from here on the other inputs change nothing.
			if (matchedInputs >= matchedOutputs)
			{
				break;
			}

			// What is remembered is given up as a whole, and never in the middle of a batch.
			if (_matchedOutputsOfOthers.Count > MaxRememberedTransactions - BatchSize)
			{
				_matchedOutputsOfOthers.Clear();
			}

			// Inputs that spend outputs of one transaction may be in different batches: what was asked for before is known by now.
			var unknownPrevOuts = prevOuts.Where(prevOut => !_matchedOutputsOfOthers.ContainsKey(prevOut.Hash)).ToArray();

			if (unknownPrevOuts.Length > 0)
			{
				foreach (var parent in (await search.FindAsync(unknownPrevOuts, cancellationToken).ConfigureAwait(false)).Values)
				{
					Remember(parent);
				}
			}

			foreach (var prevOut in prevOuts)
			{
				if (_matchedOutputsOfOthers.TryGetValue(prevOut.Hash, out var matchedOutputsOfParent))
				{
					matchedInputs += WasMatchedOutput(matchedOutputsOfParent, prevOut) ? 1 : 0;
				}
				else
				{
					unknownInputs++;
				}
			}
		}

		if (matchedInputs < matchedOutputs && unknownInputs > 0)
		{
			return null;
		}

		return AnonymityPoints.Calculate(matchedInputs, matchedOutputs);
	}

	private static bool WasMatchedOutput(bool[] matchedOutputsOfParent, OutPoint prevOut) =>
		prevOut.N < matchedOutputsOfParent.Length && matchedOutputsOfParent[prevOut.N];

	private void Remember(Transaction transactionOfOthers)
	{
		var amounts = transactionOfOthers.Outputs.Select(output => output.Value.Satoshi).ToArray();

		_matchedOutputsOfOthers[transactionOfOthers.GetHash()] = Enumerable
			.Range(0, amounts.Length)
			.Select(index => AnonymityPoints.IsMatched(amounts, (uint)index))
			.ToArray();
	}

	/// <summary>
	/// Looks for the transactions that created the outputs that one transaction spends.
	/// </summary>
	private class ParentSearch(IRPCClient rpcClient, SmartTransaction spender)
	{
		/// <summary>The heights at which the outputs were created that the spender spends, one for every input, once the node was asked.</summary>
		private IReadOnlyList<int?>? _prevoutHeights;

		private bool _askedForPrevoutHeights;

		/// <returns>The transactions that were found, by their identifiers.</returns>
		public async Task<Dictionary<uint256, Transaction>> FindAsync(IReadOnlyList<OutPoint> prevOuts, CancellationToken cancellationToken)
		{
			using var timeout = new CancellationTokenSource(BatchTimeout);
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

			try
			{
				var parents = await FindWithoutBlocksAsync(prevOuts, linked.Token).ConfigureAwait(false);
				var missing = prevOuts.Where(prevOut => !parents.ContainsKey(prevOut.Hash)).ToArray();

				if (missing.Length > 0)
				{
					foreach (var parent in await FindInBlocksAsync(missing, linked.Token).ConfigureAwait(false))
					{
						parents[parent.GetHash()] = parent;
					}
				}

				return parents;
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
			{
				throw new TimeoutException($"The node did not tell about the inputs of {spender.GetHash()} in time.");
			}
		}

		/// <summary>
		/// A node that has an index of all transactions finds each of them, and every node finds the ones in its mempool.
		/// </summary>
		private async Task<Dictionary<uint256, Transaction>> FindWithoutBlocksAsync(IReadOnlyList<OutPoint> prevOuts, CancellationToken cancellationToken)
		{
			var batch = rpcClient.PrepareBatch();
			var requests = prevOuts
				.Select(prevOut => prevOut.Hash)
				.Distinct()
				.Select(txid => batch.GetRawTransactionAsync(txid, throwIfNotFound: false, cancellationToken))
				.ToArray();

			await batch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			var found = new Dictionary<uint256, Transaction>();

			foreach (var request in requests)
			{
				if (await AnswerOrNullAsync(request).ConfigureAwait(false) is { } transaction)
				{
					found[transaction.GetHash()] = transaction;
				}
			}

			return found;
		}

		/// <summary>
		/// Any node finds a transaction in a block that it is told, as long as it has the block. The heights of the blocks
		/// come from the spender: from the data that the node keeps of the block of the spender to undo it, or, for a
		/// spender that is not in a block yet, from the outputs themselves, which are unspent until then.
		/// </summary>
		private async Task<IEnumerable<Transaction>> FindInBlocksAsync(IReadOnlyList<OutPoint> prevOuts, CancellationToken cancellationToken)
		{
			var heights = spender.BlockHash is { } blockHash
				? await GetHeightsFromSpenderAsync(prevOuts, blockHash, cancellationToken).ConfigureAwait(false)
				: await GetHeightsFromUnspentOutputsAsync(prevOuts, cancellationToken).ConfigureAwait(false);

			if (heights.Count == 0)
			{
				return [];
			}

			var hashBatch = rpcClient.PrepareBatch();
			var hashRequests = heights.Values
				.Distinct()
				.ToDictionary(height => height, height => hashBatch.GetBlockHashAsync(height, cancellationToken));

			await hashBatch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			var transactionBatch = rpcClient.PrepareBatch();
			var transactionRequests = new List<Task<Transaction?>>();

			foreach (var (txid, height) in heights)
			{
				if (await AnswerOrNullAsync(hashRequests[height]).ConfigureAwait(false) is { } hash)
				{
					transactionRequests.Add(transactionBatch.GetRawTransactionFromBlockAsync(txid, hash, cancellationToken));
				}
			}

			await transactionBatch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			var found = new List<Transaction>();

			foreach (var request in transactionRequests)
			{
				if (await AnswerOrNullAsync(request).ConfigureAwait(false) is { } transaction)
				{
					found.Add(transaction);
				}
			}

			return found;
		}

		private async Task<Dictionary<uint256, int>> GetHeightsFromSpenderAsync(IReadOnlyList<OutPoint> prevOuts, uint256 blockHash, CancellationToken cancellationToken)
		{
			if (!_askedForPrevoutHeights)
			{
				_prevoutHeights = await rpcClient.GetPrevoutHeightsAsync(spender.GetHash(), blockHash, cancellationToken).ConfigureAwait(false);
				_askedForPrevoutHeights = true;
			}

			var heights = new Dictionary<uint256, int>();
			var inputs = spender.Transaction.Inputs;

			if (_prevoutHeights is not { } prevoutHeights || prevoutHeights.Count != inputs.Count)
			{
				return heights;
			}

			var wanted = prevOuts.ToHashSet();

			for (var i = 0; i < inputs.Count; i++)
			{
				if (wanted.Contains(inputs[i].PrevOut) && prevoutHeights[i] is { } height)
				{
					heights[inputs[i].PrevOut.Hash] = height;
				}
			}

			return heights;
		}

		private async Task<Dictionary<uint256, int>> GetHeightsFromUnspentOutputsAsync(IReadOnlyList<OutPoint> prevOuts, CancellationToken cancellationToken)
		{
			var heights = new Dictionary<uint256, int>();
			var tipHeight = await rpcClient.GetBlockCountAsync(cancellationToken).ConfigureAwait(false);

			var batch = rpcClient.PrepareBatch();
			var requests = prevOuts
				.Select(prevOut => (PrevOut: prevOut, Request: batch.GetTxOutAsync(prevOut.Hash, (int)prevOut.N, includeMempool: false, cancellationToken)))
				.ToArray();

			await batch.SendBatchAsync(cancellationToken).ConfigureAwait(false);

			var answers = new List<(OutPoint PrevOut, GetTxOutResponse Answer)>();

			foreach (var (prevOut, request) in requests)
			{
				if (await AnswerOrNullAsync(request).ConfigureAwait(false) is { } answer)
				{
					answers.Add((prevOut, answer));
				}
			}

			// The answers count the confirmations from the tip. If a block arrived in between, they do not all count
			// from the same one, and the calculation is left for the next time.
			if (await rpcClient.GetBlockCountAsync(cancellationToken).ConfigureAwait(false) != tipHeight)
			{
				return heights;
			}

			foreach (var (prevOut, answer) in answers)
			{
				if (answer.Confirmations > 0)
				{
					heights[prevOut.Hash] = tipHeight - answer.Confirmations + 1;
				}
			}

			return heights;
		}

		/// <summary>A request that the node refuses is a request without an answer. A node that cannot be reached is another matter.</summary>
		private static async Task<T?> AnswerOrNullAsync<T>(Task<T> request)
			where T : class?
		{
			try
			{
				return await request.ConfigureAwait(false);
			}
			catch (RPCException)
			{
				return null;
			}
		}
	}
}
