using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Services;

/// <summary>
/// Tests for <see cref="CpfpInfo"/>: what the wallet makes of a mempool entry of the node.
/// </summary>
public class CpfpInfoTests
{
	[Fact]
	public void TransactionWithoutUnconfirmedRelatives()
	{
		// The node counts the transaction itself among its ancestors and its descendants.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(2_000),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(2_000),
			AncestorVirtualSizeBytes = 200,
			DescendantFees = Money.Satoshis(2_000),
			DescendantVirtualSizeBytes = 200
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		Assert.Equal(Money.Satoshis(2_000), info.Fee);
		Assert.Equal(200, info.VSize);
		Assert.Equal(Money.Zero, info.AncestorsFee);
		Assert.Equal(0, info.AncestorsVSize);
		Assert.Equal(new FeeRate(10m), info.EffectiveFeeRate);
	}

	[Fact]
	public void CheapAncestorsLowerTheEffectiveFeeRate()
	{
		// The transaction pays 10 sat/vb, but it cannot be mined without an ancestor that pays 1 sat/vb.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(2_000),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(2_300),
			AncestorVirtualSizeBytes = 500,
			DescendantFees = Money.Satoshis(2_000),
			DescendantVirtualSizeBytes = 200
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		// The ancestors are told apart from the transaction.
		Assert.Equal(Money.Satoshis(2_000), info.Fee);
		Assert.Equal(200, info.VSize);
		Assert.Equal(Money.Satoshis(300), info.AncestorsFee);
		Assert.Equal(300, info.AncestorsVSize);
		Assert.Equal(new FeeRate(Money.Satoshis(2_300), 500), info.EffectiveFeeRate);
	}

	[Fact]
	public void DescendantsThatPayMoreRaiseTheEffectiveFeeRate()
	{
		// The transaction pays 1 sat/vb, and a child pays for both of them.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(200),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(200),
			AncestorVirtualSizeBytes = 200,
			DescendantFees = Money.Satoshis(6_200),
			DescendantVirtualSizeBytes = 400
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		Assert.Equal(new FeeRate(Money.Satoshis(6_200), 400), info.EffectiveFeeRate);
	}

	[Fact]
	public void AncestorsThatPayMoreDoNotRaiseTheEffectiveFeeRate()
	{
		// The ancestor pays 30 sat/vb and is mined without the transaction, which pays 10 sat/vb.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(2_000),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(11_000),
			AncestorVirtualSizeBytes = 500,
			DescendantFees = Money.Satoshis(2_000),
			DescendantVirtualSizeBytes = 200
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		Assert.Equal(new FeeRate(10m), info.EffectiveFeeRate);
		Assert.Equal(Money.Satoshis(9_000), info.AncestorsFee);
		Assert.Equal(300, info.AncestorsVSize);
	}

	[Fact]
	public void DescendantsPayForCheapAncestorsToo()
	{
		// A parent of 300 vbytes pays 300 sats, the transaction of 200 vbytes pays 400 sats, and a child of 100 vbytes pays 5 300 sats.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(400),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(700),
			AncestorVirtualSizeBytes = 500,
			DescendantFees = Money.Satoshis(5_700),
			DescendantVirtualSizeBytes = 300
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		// The three of them are mined together.
		Assert.Equal(new FeeRate(Money.Satoshis(6_000), 600), info.EffectiveFeeRate);
	}

	[Fact]
	public void DescendantsThatPayLessDoNotLowerTheEffectiveFeeRate()
	{
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(2_000),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(2_000),
			AncestorVirtualSizeBytes = 200,
			DescendantFees = Money.Satoshis(2_200),
			DescendantVirtualSizeBytes = 400
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		Assert.Equal(new FeeRate(10m), info.EffectiveFeeRate);
	}

	[Fact]
	public void PrefersTheChunkFeeRateThatTheNodeTells()
	{
		// Nodes that order their mempool by chunks tell the fee rate that the transaction is going to be mined with.
		var entry = new MempoolEntry
		{
			BaseFee = Money.Satoshis(200),
			VirtualSizeBytes = 200,
			AncestorFees = Money.Satoshis(200),
			AncestorVirtualSizeBytes = 200,
			DescendantFees = Money.Satoshis(200),
			DescendantVirtualSizeBytes = 200,
			ChunkFees = Money.Satoshis(5_000),
			ChunkWeight = 1_998
		};

		var info = CpfpInfo.FromMempoolEntry(entry);

		// The weight is rounded up to virtual bytes.
		Assert.Equal(new FeeRate(Money.Satoshis(5_000), 500), info.EffectiveFeeRate);
		Assert.Equal(Money.Satoshis(200), info.Fee);
	}
}

/// <summary>
/// Tests for <see cref="CpfpInfoUpdater"/>: the fees of unconfirmed transactions come from the mempool of the user's node.
/// </summary>
public class CpfpInfoUpdaterTests
{
	/// <summary>A transaction of 150 vbytes that pays 1 500 sats, with a child of 300 vbytes that pays 9 000 sats.</summary>
	private static readonly FeeRate WithChildFeeRate = new(Money.Satoshis(10_500), 450);

	[Fact]
	public async Task NothingIsKnownAtTheStartAsync()
	{
		var node = new TestNode();
		var handler = CpfpInfoUpdater.Create(node.RpcClient, new EventBus());

		Assert.Empty(await GetCachedAsync(handler));
		Assert.Equal(0, node.RequestCount);
	}

	[Fact]
	public async Task TellsWhatTheNodeKnowsAboutATransactionAsync()
	{
		var node = new TestNode();
		var eventBus = new EventBus();
		var arrivals = 0;
		using var subscription = eventBus.Subscribe<CpfpInfoArrived>(_ => arrivals++);
		var handler = CpfpInfoUpdater.Create(node.RpcClient, eventBus);

		var tx = BitcoinFactory.CreateSmartTransaction();
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);

		var result = await GetInfoAsync(handler, tx);

		Assert.True(result.IsOk);
		Assert.Equal(Money.Satoshis(1_500), result.Value.Fee);
		Assert.Equal(150, result.Value.VSize);
		Assert.Equal(new FeeRate(10m), result.Value.EffectiveFeeRate);
		Assert.Equal(1, arrivals);

		// It is remembered for the transaction history.
		var cached = Assert.Single(await GetCachedAsync(handler));
		Assert.Same(tx, cached.Transaction);
		Assert.Equal(result.Value, cached.CpfpInfo);
	}

	[Fact]
	public async Task AsksTheNodeEveryTimeAndAnnouncesOnlyNewsAsync()
	{
		var node = new TestNode();
		var eventBus = new EventBus();
		var arrivals = 0;
		using var subscription = eventBus.Subscribe<CpfpInfoArrived>(_ => arrivals++);
		var handler = CpfpInfoUpdater.Create(node.RpcClient, eventBus);

		var tx = BitcoinFactory.CreateSmartTransaction();
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);

		await GetInfoAsync(handler, tx);
		await GetInfoAsync(handler, tx);

		Assert.Equal(2, node.RequestCount);
		Assert.Equal(1, arrivals);

		// A child that pays more changes the effective fee rate of the transaction.
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150, descendantsFee: Money.Satoshis(9_000), descendantsVSize: 300);

		var result = await GetInfoAsync(handler, tx);

		Assert.Equal(WithChildFeeRate, result.Value.EffectiveFeeRate);
		Assert.Equal(2, arrivals);
		Assert.Equal(WithChildFeeRate, Assert.Single(await GetCachedAsync(handler)).CpfpInfo.EffectiveFeeRate);
	}

	[Fact]
	public async Task FailsForATransactionThatIsNotInTheMempoolOfTheNodeAsync()
	{
		var node = new TestNode();
		var eventBus = new EventBus();
		var arrivals = 0;
		using var subscription = eventBus.Subscribe<CpfpInfoArrived>(_ => arrivals++);
		var handler = CpfpInfoUpdater.Create(node.RpcClient, eventBus);

		var tx = BitcoinFactory.CreateSmartTransaction();

		var result = await GetInfoAsync(handler, tx);

		Assert.False(result.IsOk);
		Assert.Equal(0, arrivals);
		Assert.Empty(await GetCachedAsync(handler));

		// A transaction that left the mempool is forgotten.
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);
		Assert.True((await GetInfoAsync(handler, tx)).IsOk);
		Assert.Single(await GetCachedAsync(handler));

		node.Remove(tx);
		Assert.False((await GetInfoAsync(handler, tx)).IsOk);
		Assert.Empty(await GetCachedAsync(handler));
	}

	[Fact]
	public async Task FailsWhenTheNodeDoesNotAnswerAsync()
	{
		var node = new TestNode();
		var handler = CpfpInfoUpdater.Create(node.RpcClient, new EventBus());

		var tx = BitcoinFactory.CreateSmartTransaction();
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);
		Assert.True((await GetInfoAsync(handler, tx)).IsOk);

		node.Failure = new HttpRequestException("Connection refused");
		var result = await GetInfoAsync(handler, tx);

		Assert.False(result.IsOk);
		Assert.Equal("Connection refused", result.Error);

		// A request that timed out is a failure too: whoever asked gets an answer.
		node.Failure = new TaskCanceledException("The request timed out.");
		result = await GetInfoAsync(handler, tx);

		Assert.False(result.IsOk);

		// What was known is kept until the node answers again.
		Assert.Single(await GetCachedAsync(handler));
	}

	[Fact]
	public async Task StopsWhenTheApplicationStopsAsync()
	{
		var node = new TestNode();
		var handler = CpfpInfoUpdater.Create(node.RpcClient, new EventBus());
		var tx = BitcoinFactory.CreateSmartTransaction();

		using var cts = new CancellationTokenSource();
		node.OnRequest = cts.Cancel;
		node.Failure = new OperationCanceledException();

		var replyChannel = new TestReplyChannel<Result<CpfpInfo, string>>();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => handler(new CpfpInfoMessage.GetInfoForTransaction(tx, replyChannel), Unit.Instance, cts.Token));

		Assert.False(replyChannel.WasReplied);
	}

	[Fact]
	public async Task FetchesAheadOnlyForTransactionsThatCanBeSpedUpAsync()
	{
		var node = new TestNode();
		var handler = CpfpInfoUpdater.Create(node.RpcClient, new EventBus());

		// A confirmed transaction needs no speed up.
		var confirmedTx = BitcoinFactory.CreateSmartTransaction(height: new Height.ChainHeight(8_888));
		node.Add(confirmedTx, fee: Money.Satoshis(1_500), vsize: 150);
		await handler(new CpfpInfoMessage.PreFetchInfoForTransaction(confirmedTx), Unit.Instance, CancellationToken.None);

		Assert.Equal(0, node.RequestCount);
		Assert.Empty(await GetCachedAsync(handler));

		var tx = BitcoinFactory.CreateSmartTransaction();
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);
		await handler(new CpfpInfoMessage.PreFetchInfoForTransaction(tx), Unit.Instance, CancellationToken.None);

		Assert.Equal(1, node.RequestCount);
		Assert.Same(tx, Assert.Single(await GetCachedAsync(handler)).Transaction);
	}

	[Fact]
	public async Task UpdateForgetsConfirmedTransactionsAndRefreshesTheOthersAsync()
	{
		var node = new TestNode();
		var eventBus = new EventBus();
		var arrivals = 0;
		using var subscription = eventBus.Subscribe<CpfpInfoArrived>(_ => arrivals++);
		var handler = CpfpInfoUpdater.Create(node.RpcClient, eventBus);

		var txToConfirm = BitcoinFactory.CreateSmartTransaction();
		var tx = BitcoinFactory.CreateSmartTransaction();
		node.Add(txToConfirm, fee: Money.Satoshis(1_500), vsize: 150);
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);

		await GetInfoAsync(handler, txToConfirm);
		await GetInfoAsync(handler, tx);
		Assert.Equal(2, (await GetCachedAsync(handler)).Length);
		Assert.Equal(2, node.RequestCount);
		Assert.Equal(2, arrivals);

		// A block confirms one of the transactions, and the other one gets a child.
		txToConfirm.TryUpdate(new SmartTransaction(txToConfirm.Transaction, new Height.ChainHeight(8_888)));
		node.Remove(txToConfirm);
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150, descendantsFee: Money.Satoshis(9_000), descendantsVSize: 300);

		await handler(new CpfpInfoMessage.UpdateMessage(), Unit.Instance, CancellationToken.None);

		var cached = Assert.Single(await GetCachedAsync(handler));
		Assert.Same(tx, cached.Transaction);
		Assert.Equal(WithChildFeeRate, cached.CpfpInfo.EffectiveFeeRate);
		Assert.Equal(3, node.RequestCount);
		Assert.Equal(3, arrivals);

		// The node is not asked again right away.
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150, descendantsFee: Money.Satoshis(12_000), descendantsVSize: 300);
		await handler(new CpfpInfoMessage.UpdateMessage(), Unit.Instance, CancellationToken.None);

		Assert.Equal(3, node.RequestCount);
		Assert.Equal(WithChildFeeRate, Assert.Single(await GetCachedAsync(handler)).CpfpInfo.EffectiveFeeRate);
	}

	private static async Task<Result<CpfpInfo, string>> GetInfoAsync(MessageHandler<CpfpInfoMessage, Unit> handler, SmartTransaction tx)
	{
		var replyChannel = new TestReplyChannel<Result<CpfpInfo, string>>();
		await handler(new CpfpInfoMessage.GetInfoForTransaction(tx, replyChannel), Unit.Instance, CancellationToken.None);

		Assert.True(replyChannel.WasReplied);
		return replyChannel.Result!;
	}

	private static async Task<CachedCpfpInfo[]> GetCachedAsync(MessageHandler<CpfpInfoMessage, Unit> handler)
	{
		var replyChannel = new TestReplyChannel<CachedCpfpInfo[]>();
		await handler(new CpfpInfoMessage.GetCachedCpfpInfo(replyChannel), Unit.Instance, CancellationToken.None);

		Assert.True(replyChannel.WasReplied);
		return replyChannel.Result!;
	}
}

/// <summary>
/// Tests for <see cref="CpfpInfoProvider"/>: the requests go through the mailbox of the updater.
/// </summary>
public class CpfpInfoProviderTests
{
	[Fact]
	public async Task GetCachedCpfpInfoAsync_ReturnsResultFromMailboxAsync()
	{
		using var cts = new CancellationTokenSource();
		var handler = CpfpInfoUpdater.Create(new TestNode().RpcClient, new EventBus());

		using var mailbox = CreateAndStartMailboxProcessor(handler, cts.Token);
		var provider = new CpfpInfoProvider(mailbox);

		var result = await provider.GetCachedCpfpInfoAsync(CancellationToken.None);

		Assert.NotNull(result);
		Assert.Empty(result);
	}

	[Fact]
	public async Task ScheduleRequest_PostsPreFetchMessage_AndProcessesItAsync()
	{
		using var cts = new CancellationTokenSource();
		var messageReceived = new TaskCompletionSource<CpfpInfoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var mailbox = CreateAndStartMailboxProcessor((message, _, _) =>
		{
			messageReceived.TrySetResult(message);
			return Task.FromResult(Unit.Instance);
		}, cts.Token);
		var provider = new CpfpInfoProvider(mailbox);
		var tx = BitcoinFactory.CreateSmartTransaction();

		provider.ScheduleRequest(tx);
		var message = await messageReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Same(tx, Assert.IsType<CpfpInfoMessage.PreFetchInfoForTransaction>(message).SmartTransaction);
	}

	[Fact]
	public async Task GetCpfpInfoAsync_AnswersAndFillsTheCacheAsync()
	{
		using var cts = new CancellationTokenSource();
		var node = new TestNode();
		var handler = CpfpInfoUpdater.Create(node.RpcClient, new EventBus());

		using var mailbox = CreateAndStartMailboxProcessor(handler, cts.Token);
		var provider = new CpfpInfoProvider(mailbox);

		var tx = BitcoinFactory.CreateSmartTransaction();
		var unknownTx = BitcoinFactory.CreateSmartTransaction();
		node.Add(tx, fee: Money.Satoshis(1_500), vsize: 150);

		var result = await provider.GetCpfpInfoAsync(tx, CancellationToken.None);
		var resultForUnknown = await provider.GetCpfpInfoAsync(unknownTx, CancellationToken.None);

		Assert.True(result.IsOk);
		Assert.False(resultForUnknown.IsOk);

		var cached = Assert.Single(await provider.GetCachedCpfpInfoAsync(CancellationToken.None));
		Assert.Same(tx, cached.Transaction);
	}

	[Fact]
	public async Task GetCachedCpfpInfoAsync_ThrowsOperationCanceledException_WhenCancelledAsync()
	{
		using var processorCts = new CancellationTokenSource();

		// Withhold the reply so completion cannot race the cancellation assertion.
		using var mailbox = CreateAndStartMailboxProcessor((_, _, _) => Task.FromResult(Unit.Instance), processorCts.Token);
		var provider = new CpfpInfoProvider(mailbox);

		using var requestCts = new CancellationTokenSource();
		requestCts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => provider.GetCachedCpfpInfoAsync(requestCts.Token));
	}

	[Fact]
	public async Task GetCpfpInfoAsync_ThrowsOperationCanceledException_WhenCancelledBeforeResponseAsync()
	{
		using var processorCts = new CancellationTokenSource();
		var messageReceived = new TaskCompletionSource();

		async Task<Unit> HandlerAsync(CpfpInfoMessage msg, Unit _, CancellationToken ct)
		{
			if (msg is CpfpInfoMessage.GetInfoForTransaction)
			{
				messageReceived.SetResult();
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
			}
			return Unit.Instance;
		}

		using var mailbox = CreateAndStartMailboxProcessor(HandlerAsync, processorCts.Token);
		var provider = new CpfpInfoProvider(mailbox);
		var tx = BitcoinFactory.CreateSmartTransaction();

		using var requestCts = new CancellationTokenSource();

		var task = provider.GetCpfpInfoAsync(tx, requestCts.Token);
		await messageReceived.Task;
		requestCts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
	}

	[Fact]
	public async Task GetCpfpInfoAsync_ThrowsOperationCanceledException_WhenMailboxProcessorIsCancelledAsync()
	{
		using var processorCts = new CancellationTokenSource();
		var messageReceived = new TaskCompletionSource();

		async Task<Unit> HandlerAsync(CpfpInfoMessage msg, Unit _, CancellationToken ct)
		{
			if (msg is CpfpInfoMessage.GetInfoForTransaction)
			{
				messageReceived.SetResult();
				await Task.Delay(TimeSpan.FromSeconds(30), ct);
			}
			return Unit.Instance;
		}

		using var mailbox = CreateAndStartMailboxProcessor(HandlerAsync, processorCts.Token);
		var provider = new CpfpInfoProvider(mailbox);
		var tx = BitcoinFactory.CreateSmartTransaction();

		var task = provider.GetCpfpInfoAsync(tx, CancellationToken.None);
		await messageReceived.Task;
		processorCts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
	}

	[Fact]
	public async Task DisposedMailbox_RefusesRequestsAsync()
	{
		var handler = CpfpInfoUpdater.Create(new TestNode().RpcClient, new EventBus());

		var mailbox = CreateAndStartMailboxProcessor(handler, CancellationToken.None);
		var provider = new CpfpInfoProvider(mailbox);
		var tx = BitcoinFactory.CreateSmartTransaction();

		mailbox.Dispose();

		await Assert.ThrowsAsync<ObjectDisposedException>(
			() => provider.GetCpfpInfoAsync(tx, CancellationToken.None));

		// Scheduling is fire and forget.
		provider.ScheduleRequest(tx);
	}

	private static MailboxProcessor<CpfpInfoMessage> CreateAndStartMailboxProcessor(
		MessageHandler<CpfpInfoMessage, Unit> handler,
		CancellationToken cancellationToken)
	{
		var processor = new MailboxProcessor<CpfpInfoMessage>(
			nameof(CpfpInfoProviderTests),
			async (mailbox, ct) =>
			{
				while (!ct.IsCancellationRequested)
				{
					var message = await mailbox.ReceiveAsync(ct);
					await handler(message, Unit.Instance, ct);
				}
			},
			cancellationToken: cancellationToken);

		processor.Start();
		return processor;
	}
}

/// <summary>
/// The mempool of a node, as far as <c>getmempoolentry</c> tells about it.
/// </summary>
public class TestNode
{
	private readonly Dictionary<uint256, MempoolEntry> _mempool = new();

	public TestNode()
	{
		RpcClient = new MockRpcClient
		{
			OnGetMempoolEntryAsync = txid =>
			{
				RequestCount++;
				OnRequest?.Invoke();

				return Failure is { } failure
					? Task.FromException<MempoolEntry?>(failure)
					: Task.FromResult(_mempool.GetValueOrDefault(txid));
			}
		};
	}

	public MockRpcClient RpcClient { get; }

	public int RequestCount { get; private set; }

	/// <summary>What the node answers its requests with, when something is wrong with it.</summary>
	public Exception? Failure { get; set; }

	public Action? OnRequest { get; set; }

	/// <summary>Puts a transaction into the mempool, or replaces what the mempool tells about it.</summary>
	public void Add(SmartTransaction tx, Money fee, int vsize, Money? descendantsFee = null, int descendantsVSize = 0)
	{
		_mempool[tx.GetHash()] = new MempoolEntry
		{
			TransactionId = tx.GetHash(),
			BaseFee = fee,
			VirtualSizeBytes = vsize,
			AncestorFees = fee,
			AncestorVirtualSizeBytes = vsize,
			DescendantFees = fee + (descendantsFee ?? Money.Zero),
			DescendantVirtualSizeBytes = vsize + descendantsVSize
		};
	}

	public void Remove(SmartTransaction tx) => _mempool.Remove(tx.GetHash());
}

public class TestReplyChannel<T> : IReplyChannel<T>
{
	public T? Result { get; private set; }
	public bool WasReplied { get; private set; }

	public void Reply(T response)
	{
		Result = response;
		WasReplied = true;
	}
}
