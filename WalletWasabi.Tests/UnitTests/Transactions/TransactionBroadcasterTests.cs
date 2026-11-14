using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionBroadcaster"/>: transactions leave the wallet through the user's node only.
/// </summary>
public class TransactionBroadcasterTests
{
	[Fact]
	public async Task BroadcastsThroughTheNodeAsync()
	{
		var sentTransactions = new List<Transaction>();
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx =>
			{
				sentTransactions.Add(tx);
				return tx.GetHash();
			}
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		var broadcaster = new TransactionBroadcaster(rpc, new MempoolService(eventBus));

		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		await broadcaster.SendTransactionAsync(tx);

		Assert.Same(tx.Transaction, Assert.Single(sentTransactions));

		// The node took it, so it is in the mempool as far as the wallet is concerned.
		Assert.Equal(Height.Mempool, tx.Height);
		Assert.Same(tx, Assert.Single(announcedTransactions));
	}

	[Fact]
	public async Task TellsWhyTheNodeRefusedTheTransactionAsync()
	{
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw new RPCException(RPCErrorCode.RPC_TRANSACTION_REJECTED, "min relay fee not met, 110 < 141", null)
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		var broadcaster = new TransactionBroadcaster(rpc, new MempoolService(eventBus));

		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Contains("min relay fee not met, 110 < 141", exception.Message);
		Assert.IsType<RPCException>(exception.InnerException);

		// Nothing is assumed about a transaction that was not broadcast.
		Assert.Equal(Height.Unknown, tx.Height);
		Assert.Empty(announcedTransactions);
	}

	[Fact]
	public async Task TellsThatTheNodeCannotBeReachedAsync()
	{
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw new SocketException((int)SocketError.ConnectionRefused)
		};

		var broadcaster = new TransactionBroadcaster(rpc, new MempoolService(new EventBus()));
		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Contains("Connection refused", exception.Message);
		Assert.Equal(Height.Unknown, tx.Height);
	}

	[Fact]
	public async Task CancellationIsNotAFailureOfTheNodeAsync()
	{
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw new OperationCanceledException()
		};

		var broadcaster = new TransactionBroadcaster(rpc, new MempoolService(new EventBus()));
		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		await Assert.ThrowsAsync<OperationCanceledException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Equal(Height.Unknown, tx.Height);
	}
}
