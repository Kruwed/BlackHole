using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionBroadcaster"/>: transactions leave the wallet through the user's node only, and the
/// node is one that broadcasts them privately. Such a node does not have a transaction that it was handed in its
/// mempool until it hears it back from the network, and it tells what it is still broadcasting.
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
		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, eventBus);

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
		var attempts = 0;
		var queueQuestions = 0;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ =>
			{
				attempts++;
				throw new RPCException(RPCErrorCode.RPC_TRANSACTION_REJECTED, "min relay fee not met, 110 < 141", null);
			},
			OnGetPrivateBroadcastQueueAsync = () =>
			{
				queueQuestions++;
				return Task.FromResult<uint256[]>([]);
			}
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, eventBus);

		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Contains("min relay fee not met, 110 < 141", exception.Message);
		Assert.IsType<RPCException>(exception.InnerException);

		// Nothing is assumed about a transaction that was not broadcast.
		Assert.Equal(Height.Unknown, tx.Height);
		Assert.Empty(announcedTransactions);

		// Such a refusal has nothing to do with what the node is broadcasting: the node is asked once.
		Assert.Equal(1, attempts);
		Assert.Equal(0, queueQuestions);
	}

	[Fact]
	public async Task TellsThatTheNodeCannotBeReachedAsync()
	{
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw new SocketException((int)SocketError.ConnectionRefused)
		};

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());
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

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());
		var tx = BitcoinFactory.CreateSmartTransaction(height: Height.Unknown);

		await Assert.ThrowsAsync<OperationCanceledException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Equal(Height.Unknown, tx.Height);
	}

	/// <summary>
	/// The node knows nothing of a transaction that it is broadcasting until it hears it back from the network. A
	/// transaction that spends from such a one, like a payment from the change of a payment that was just sent, is
	/// refused until then. So it waits and goes to the node again.
	/// </summary>
	[Fact]
	public async Task WaitsForATransactionThatTheNodeIsStillBroadcastingAsync()
	{
		var parent = CreateTransaction(BitcoinFactory.CreateOutPoint());
		var child = CreateTransaction(new OutPoint(parent.GetHash(), 0));

		var attempts = 0;
		var queueQuestions = 0;
		var isParentBack = false;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx =>
			{
				attempts++;
				return isParentBack ? tx.GetHash() : throw MissingInputs();
			},
			OnGetPrivateBroadcastQueueAsync = () =>
			{
				// The node hears the parent back before it is asked the third time.
				isParentBack = ++queueQuestions == 3;
				return Task.FromResult<uint256[]>(isParentBack ? [] : [BitcoinFactory.CreateUint256(), parent.GetHash()]);
			}
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, eventBus) { ParentPollPeriod = TimeSpan.FromMilliseconds(5) };

		await broadcaster.SendTransactionAsync(child);

		// Refused three times while the parent was on its way, taken when it had arrived.
		Assert.Equal(4, attempts);
		Assert.Equal(3, queueQuestions);
		Assert.Equal(Height.Mempool, child.Height);
		Assert.Same(child, Assert.Single(announcedTransactions));
	}

	/// <summary>
	/// A parent that came back to the node between the refusal of the node and its answer about what it is
	/// broadcasting is not seen there any more. The node gets the transaction once more before its refusal stands.
	/// </summary>
	[Fact]
	public async Task GoesToTheNodeOnceMoreWhenNothingIsOnItsWayAsync()
	{
		var parent = CreateTransaction(BitcoinFactory.CreateOutPoint());
		var child = CreateTransaction(new OutPoint(parent.GetHash(), 0));

		var attempts = 0;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx => ++attempts == 1 ? throw MissingInputs() : tx.GetHash(),
			OnGetPrivateBroadcastQueueAsync = () => Task.FromResult<uint256[]>([])
		};

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());

		await broadcaster.SendTransactionAsync(child);

		Assert.Equal(2, attempts);
		Assert.Equal(Height.Mempool, child.Height);
	}

	/// <summary>
	/// The node refuses a transaction whose inputs it does not know, and nothing that the transaction spends from is on
	/// its way: the coins are spent or were never there. That is told as the node tells it, and at once.
	/// </summary>
	[Fact]
	public async Task RefusalStandsWhenNothingThatTheTransactionSpendsFromIsOnItsWayAsync()
	{
		var attempts = 0;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ =>
			{
				attempts++;
				throw MissingInputs();
			},

			// The node is broadcasting a transaction, but not one that this one spends from.
			OnGetPrivateBroadcastQueueAsync = () => Task.FromResult<uint256[]>([BitcoinFactory.CreateUint256()])
		};

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());
		var tx = CreateTransaction(BitcoinFactory.CreateOutPoint());

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Contains("bad-txns-inputs-missingorspent", exception.Message);
		Assert.IsType<RPCException>(exception.InnerException);
		Assert.Equal(2, attempts);
		Assert.Equal(Height.Unknown, tx.Height);
	}

	/// <summary>
	/// A node that cannot reach Tor or I2P does not get a transaction out, and never hears it back. What spends from
	/// that transaction does not wait for ever: after a while the user is told what the matter is.
	/// </summary>
	[Fact]
	public async Task StopsWaitingWhenTheNodeDoesNotHearTheParentBackAsync()
	{
		var parent = CreateTransaction(BitcoinFactory.CreateOutPoint());
		var child = CreateTransaction(new OutPoint(parent.GetHash(), 0));

		var attempts = 0;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ =>
			{
				attempts++;
				throw MissingInputs();
			},
			OnGetPrivateBroadcastQueueAsync = () => Task.FromResult<uint256[]>([parent.GetHash()])
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, eventBus)
		{
			ParentPollPeriod = TimeSpan.FromMilliseconds(5),
			ParentWaitLimit = TimeSpan.FromMilliseconds(100)
		};

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(child));

		Assert.StartsWith($"The Bitcoin node did not accept the transaction: it spends from transaction {parent.GetHash()}, which the node is still broadcasting", exception.Message);
		Assert.Contains("Send this transaction again later.", exception.Message);
		Assert.True(attempts > 2);
		Assert.Equal(Height.Unknown, child.Height);
		Assert.Empty(announcedTransactions);
	}

	/// <summary>The wait for a transaction that is on its way ends when the user gives up.</summary>
	[Fact]
	public async Task WaitForTheParentCanBeCancelledAsync()
	{
		var parent = CreateTransaction(BitcoinFactory.CreateOutPoint());
		var child = CreateTransaction(new OutPoint(parent.GetHash(), 0));

		using var cts = new CancellationTokenSource();
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw MissingInputs(),
			OnGetPrivateBroadcastQueueAsync = () =>
			{
				cts.Cancel();
				return Task.FromResult<uint256[]>([parent.GetHash()]);
			}
		};

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => broadcaster.SendTransactionAsync(child, cts.Token));

		Assert.Equal(Height.Unknown, child.Height);
	}

	/// <summary>
	/// A node that does not tell what it is broadcasting, like a Bitcoin Core before version 31, leaves nothing to
	/// wait for: its refusal is told.
	/// </summary>
	[Fact]
	public async Task RefusalStandsWhenTheNodeDoesNotTellWhatItIsBroadcastingAsync()
	{
		var attempts = 0;
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ =>
			{
				attempts++;
				throw MissingInputs();
			},
			OnGetPrivateBroadcastQueueAsync = () => throw new RPCException(RPCErrorCode.RPC_METHOD_NOT_FOUND, "Method not found", null)
		};

		using var store = CreateStore();
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());
		var tx = CreateTransaction(BitcoinFactory.CreateOutPoint());

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(tx));

		Assert.Contains("bad-txns-inputs-missingorspent", exception.Message);
		Assert.Equal(2, attempts);
	}

	/// <summary>
	/// A speed-up or a cancellation takes the place of a transaction of the wallet. If the node is still broadcasting
	/// that one, it would send both: it is told to stop sending the one that is replaced, once it has the other one.
	/// </summary>
	[Fact]
	public async Task StopsTheBroadcastOfATransactionThatIsReplacedAsync()
	{
		var coin = BitcoinFactory.CreateOutPoint();
		var original = CreateTransaction(coin, BitcoinFactory.CreateOutPoint());
		var unrelated = CreateTransaction(BitcoinFactory.CreateOutPoint());
		var replacement = CreateTransaction(coin);

		var calls = new List<string>();
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx =>
			{
				calls.Add($"send {tx.GetHash()}");
				return tx.GetHash();
			},
			OnAbortPrivateBroadcastAsync = txid =>
			{
				calls.Add($"abort {txid}");
				return Task.FromResult(true);
			}
		};

		using var store = CreateStore();
		store.AddOrUpdate(Unconfirmed(original));
		store.AddOrUpdate(Unconfirmed(unrelated));
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());

		await broadcaster.SendTransactionAsync(replacement);

		// First the node gets the replacement, then it is told to let go of the original. Nothing else is touched.
		Assert.Equal([$"send {replacement.GetHash()}", $"abort {original.GetHash()}"], calls);
		Assert.Equal(Height.Mempool, replacement.Height);
	}

	/// <summary>A transaction that takes the place of nothing leaves the node as it is, also when it is handed over a second time.</summary>
	[Fact]
	public async Task StopsNothingForATransactionThatReplacesNothingAsync()
	{
		var aborted = new List<uint256>();
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx => tx.GetHash(),
			OnAbortPrivateBroadcastAsync = txid =>
			{
				aborted.Add(txid);
				return Task.FromResult(true);
			}
		};

		using var store = CreateStore();
		store.AddOrUpdate(Unconfirmed(CreateTransaction(BitcoinFactory.CreateOutPoint())));
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());
		var tx = CreateTransaction(BitcoinFactory.CreateOutPoint());

		await broadcaster.SendTransactionAsync(tx);

		// The wallet has it now. Handing it over again does not make it a replacement of itself.
		store.AddOrUpdate(tx);
		await broadcaster.SendTransactionAsync(tx);

		Assert.Empty(aborted);
	}

	/// <summary>The original goes on as it was if the node does not take the transaction that was to replace it.</summary>
	[Fact]
	public async Task StopsNothingWhenTheNodeRefusesTheReplacementAsync()
	{
		var coin = BitcoinFactory.CreateOutPoint();
		var original = CreateTransaction(coin);
		var replacement = CreateTransaction(coin, BitcoinFactory.CreateOutPoint());

		var aborted = new List<uint256>();
		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = _ => throw new RPCException(RPCErrorCode.RPC_TRANSACTION_REJECTED, "insufficient fee, rejecting replacement", null),
			OnAbortPrivateBroadcastAsync = txid =>
			{
				aborted.Add(txid);
				return Task.FromResult(true);
			}
		};

		using var store = CreateStore();
		store.AddOrUpdate(Unconfirmed(original));
		var broadcaster = new TransactionBroadcaster(rpc, store, new EventBus());

		await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(replacement));

		Assert.Empty(aborted);
	}

	/// <summary>
	/// The replacement is with the node. That the node could not be told to stop sending the original does not make
	/// the replacement a failure: the node lets go of a transaction that was replaced by itself, later.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReplacementIsSentWhateverBecomesOfStoppingTheOriginalAsync(bool nodeHangs)
	{
		var coin = BitcoinFactory.CreateOutPoint();
		var original = CreateTransaction(coin);
		var replacement = CreateTransaction(coin, BitcoinFactory.CreateOutPoint());

		var rpc = new MockRpcClient
		{
			OnSendRawTransactionAsync = tx => tx.GetHash(),
			OnAbortPrivateBroadcastAsync = nodeHangs
				? _ => Task.FromException<bool>(new OperationCanceledException())
				: _ => Task.FromException<bool>(new RPCException(RPCErrorCode.RPC_METHOD_NOT_FOUND, "Method not found", null))
		};

		var eventBus = new EventBus();
		var announcedTransactions = new List<SmartTransaction>();
		using var subscription = eventBus.Subscribe<NewTransactionInMempool>(e => announcedTransactions.Add(e.Transaction));
		using var store = CreateStore();
		store.AddOrUpdate(Unconfirmed(original));
		var broadcaster = new TransactionBroadcaster(rpc, store, eventBus);

		await broadcaster.SendTransactionAsync(replacement);

		Assert.Equal(Height.Mempool, replacement.Height);
		Assert.Same(replacement, Assert.Single(announcedTransactions));
	}

	/// <summary>What a Bitcoin Core answers to a transaction that spends an output that it does not know, or knows as spent.</summary>
	private static RPCException MissingInputs() =>
		new(RPCErrorCode.RPC_TRANSACTION_ERROR, "bad-txns-inputs-missingorspent", null);

	/// <summary>A transaction that spends the given outputs and that the wallet has not handed to anybody yet.</summary>
	private static SmartTransaction CreateTransaction(params OutPoint[] spends)
	{
		var tx = Network.Main.CreateTransaction();

		foreach (var outPoint in spends)
		{
			tx.Inputs.Add(outPoint);
		}

		tx.Outputs.Add(Money.Coins(0.1m), BitcoinFactory.CreateScript());

		return new SmartTransaction(tx, Height.Unknown);
	}

	/// <summary>The transaction as the wallet has it once it was handed to the node.</summary>
	private static SmartTransaction Unconfirmed(SmartTransaction transaction)
	{
		transaction.SetUnconfirmed();
		return transaction;
	}

	/// <summary>The transactions of a wallet that has none yet.</summary>
	private static AllTransactionStore CreateStore() =>
		new(SqliteStorageHelper.InMemoryDatabase, Network.Main);
}
