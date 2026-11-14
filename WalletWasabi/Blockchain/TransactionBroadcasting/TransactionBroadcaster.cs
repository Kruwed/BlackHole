using System.Net.Sockets;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;

namespace WalletWasabi.Blockchain.TransactionBroadcasting;

/// <summary>
/// Broadcasts the transactions of the wallet. They only ever leave through the user's own node.
/// </summary>
public class TransactionBroadcaster(IRPCClient rpcClient, EventBus eventBus)
{
	/// <exception cref="InvalidOperationException">The node did not take the transaction. The message tells why.</exception>
	public async Task SendTransactionAsync(SmartTransaction tx, CancellationToken cancellationToken = default)
	{
		var txId = tx.GetHash();
		Logger.LogInfo($"Trying to broadcast transaction via RPC: {txId}.");

		try
		{
			await rpcClient.SendRawTransactionAsync(tx.Transaction, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			var reason = GetFailureReason(ex);
			Logger.LogInfo($"Failed to broadcast transaction {txId} via RPC. Reason: {reason}.");
			throw new InvalidOperationException($"The Bitcoin node did not accept the transaction: {reason}", ex);
		}

		Logger.LogInfo($"Transaction {txId} is successfully broadcast by the RPC interface of the node.");
		BelieveTransaction(tx);
	}

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

		// The wallet processes it right away. It does not wait until the mempool of the node is looked at the next time.
		eventBus.Publish(new NewTransactionInMempool(transaction));
	}
}
