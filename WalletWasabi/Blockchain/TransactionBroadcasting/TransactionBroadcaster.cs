using System.Diagnostics;
using System.Net.Sockets;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Blockchain.TransactionBroadcasting;

/// <summary>
/// Broadcasts the transactions of the wallet. They only ever leave through the user's own node, and the node is
/// taken to broadcast them privately: Bitcoin Core since version 31 with <c>privatebroadcast=1</c>. Nothing here
/// finds out whether the node does that. It is assumed.
/// </summary>
/// <remarks>
/// A node that broadcasts privately does not announce a transaction that it is handed to its peers. It sends it over
/// a few short-lived connections through Tor or I2P, so that nothing tells where the transaction came from, and it
/// takes the transaction into its own mempool only when it hears it back from the network, seconds later. Until
/// then the node knows nothing of what the transaction pays out, and it keeps sending it. Two things follow for a
/// wallet. A transaction that spends from one that is still on its way is refused by the node, so it waits for that
/// one. And a transaction that takes the place of one that is still on its way, a speed-up or a cancellation, leaves
/// the node sending both, so the node is told to stop sending the one that is replaced.
/// </remarks>
public class TransactionBroadcaster(IRPCClient rpcClient, AllTransactionStore transactionStore, EventBus eventBus)
{
	/// <summary>
	/// How long a transaction waits for the node to hear back the transactions that it spends from. A node needs
	/// seconds for that; one that cannot reach Tor or I2P never does.
	/// </summary>
	internal TimeSpan ParentWaitLimit { get; init; } = TimeSpan.FromSeconds(60);

	/// <summary>How long the transaction waits between two attempts meanwhile.</summary>
	internal TimeSpan ParentPollPeriod { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>How long the node is given to answer when it is told to stop broadcasting a transaction that was replaced.</summary>
	internal TimeSpan AbortTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <exception cref="InvalidOperationException">The node did not take the transaction. The message tells why.</exception>
	public async Task SendTransactionAsync(SmartTransaction tx, CancellationToken cancellationToken = default)
	{
		var txId = tx.GetHash();
		Logger.LogInfo($"Handing transaction {txId} to the node for its private broadcast.");

		// What the wallet has of the transactions that this one takes the place of, before it learns about this one.
		var replacedTxIds = FindReplacedTransactions(tx.Transaction);

		try
		{
			await SubmitAsync(tx.Transaction, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			var reason = GetFailureReason(ex);
			Logger.LogInfo($"The node did not take transaction {txId}. Reason: {reason}.");
			throw new InvalidOperationException($"The Bitcoin node did not accept the transaction: {reason}", ex);
		}

		Logger.LogInfo($"The node took transaction {txId} for its private broadcast.");
		BelieveTransaction(tx);

		await AbortBroadcastAsync(replacedTxIds, txId).ConfigureAwait(false);
	}

	/// <summary>
	/// Hands the transaction to the node. If the node refuses it because it does not know what the transaction spends,
	/// and the node is still broadcasting a transaction that it spends from, the node gets the transaction again
	/// until it has heard that one back.
	/// </summary>
	private async Task SubmitAsync(Transaction transaction, CancellationToken cancellationToken)
	{
		Stopwatch? waiting = null;
		var isLastAttempt = false;

		while (true)
		{
			try
			{
				await rpcClient.SendRawTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (RPCException ex) when (IsMissingInputs(ex) && !isLastAttempt)
			{
				if (await FindParentInQueueAsync(transaction, cancellationToken).ConfigureAwait(false) is not { } parentTxId)
				{
					// Nothing that it spends from is on its way. A transaction may have come back to the node between its
					// refusal and its answer about what it is broadcasting, so the node is asked once more. What it says
					// then is final.
					isLastAttempt = true;
					continue;
				}

				if (waiting is null)
				{
					waiting = Stopwatch.StartNew();
					Logger.LogInfo($"Transaction {transaction.GetHash()} spends from {parentTxId}, which the node has not heard back from the network yet. Waiting for that.");
				}
				else if (waiting.Elapsed >= ParentWaitLimit)
				{
					throw new TimeoutException(
						$"it spends from transaction {parentTxId}, which the node is still broadcasting and has not heard back from the network in {waiting.Elapsed.TotalSeconds:0} seconds. " +
						"The node takes a transaction that spends from another one only when that one has come back to it. Send this transaction again later.",
						ex);
				}

				await Task.Delay(ParentPollPeriod, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Tells a transaction that the given one spends from and that the node is still broadcasting, if there is one.</summary>
	private async Task<uint256?> FindParentInQueueAsync(Transaction transaction, CancellationToken cancellationToken)
	{
		uint256[] queue;

		try
		{
			queue = await rpcClient.GetPrivateBroadcastQueueAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// A node that does not tell what it is broadcasting leaves its refusal as it is.
			Logger.LogDebug($"The node did not tell which transactions it is broadcasting: {ex.Message}");
			return null;
		}

		return transaction.Inputs
			.Select(input => input.PrevOut.Hash)
			.FirstOrDefault(queue.Contains);
	}

	/// <summary>
	/// The unconfirmed transactions of the wallet that spend something that the given transaction spends too. The
	/// given one takes their place: it is a speed-up or a cancellation of them.
	/// </summary>
	private uint256[] FindReplacedTransactions(Transaction transaction)
	{
		var txId = transaction.GetHash();
		var spent = transaction.Inputs.Select(input => input.PrevOut).ToHashSet();

		return transactionStore.MempoolStore.GetTransactions()
			.Where(x => x.GetHash() != txId && x.Transaction.Inputs.Any(input => spent.Contains(input.PrevOut)))
			.Select(x => x.GetHash())
			.ToArray();
	}

	/// <summary>
	/// Tells the node to stop broadcasting the transactions that were replaced. A node that has heard such a
	/// transaction back already is not broadcasting it any more, and the replacement drives it out of the mempools.
	/// One that the node is still sending would go on being sent next to its replacement.
	/// </summary>
	private async Task AbortBroadcastAsync(uint256[] replacedTxIds, uint256 replacementTxId)
	{
		foreach (var replacedTxId in replacedTxIds)
		{
			try
			{
				// The replacement is with the node already. Nothing that goes wrong from here on, and nobody who gives up
				// waiting, turns that into a failure.
				using var timeout = new CancellationTokenSource(AbortTimeout);

				if (await rpcClient.AbortPrivateBroadcastAsync(replacedTxId, timeout.Token).ConfigureAwait(false))
				{
					Logger.LogInfo($"The node stopped broadcasting transaction {replacedTxId}, which {replacementTxId} replaces.");
				}
			}
			catch (Exception ex)
			{
				// The node gives up a transaction that was replaced by itself, after a while.
				Logger.LogWarning($"The node was not told to stop broadcasting transaction {replacedTxId}, which {replacementTxId} replaces: {ex.Message}");
			}
		}
	}

	/// <summary>Whether the node refused a transaction because it does not know an output that the transaction spends, or knows it as spent.</summary>
	private static bool IsMissingInputs(RPCException ex) =>
		ex.RPCCode == RPCErrorCode.RPC_TRANSACTION_ERROR && ex.Message.Contains("missing", StringComparison.OrdinalIgnoreCase);

	private static string GetFailureReason(Exception ex) =>
		ex switch
		{
			// The code only tells the kind of the failure, the node's own message tells the reason (e.g. which policy rule was broken).
			RPCException rpcException => rpcException.Message == rpcException.RPCCodeMessage
				? rpcException.RPCCodeMessage
				: $"{rpcException.RPCCodeMessage} ({rpcException.Message})",
			SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Failed to connect to Bitcoin RPC. Connection refused.",
			_ => ex.Message
		};

	private void BelieveTransaction(SmartTransaction transaction)
	{
		if (transaction.Height == Height.Unknown)
		{
			transaction.SetUnconfirmed();
		}

		// The wallet processes it right away. The node itself does not have it in its mempool before it hears it back
		// from the network, and the wallet does not wait for that either.
		eventBus.Publish(new NewTransactionInMempool(transaction));
	}
}
