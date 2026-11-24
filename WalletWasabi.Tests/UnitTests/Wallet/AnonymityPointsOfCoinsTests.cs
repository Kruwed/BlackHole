using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

/// <summary>
/// The anonymity points of the coins of a wallet. A coin has the points of the transaction that created it if it is a
/// matched output of that transaction, from its creation until it is spent. The wallet finds the points out with the
/// help of the node and remembers them in its file.
/// </summary>
public class AnonymityPointsOfCoinsTests
{
	[Fact]
	public async Task MatchedOutputGetsThePointsOfItsTransactionAndChangeGetsNoneAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		// A transaction with two inputs that came out of a set of three before, three equal outputs and two others.
		var coinjoin = environment.CreateForeignTransaction(0.5m, 0.5m, 0.5m, 0.2m);
		var transaction = environment.CreateTransaction(
			[new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 1), new OutPoint(coinjoin, 3)],
			ownAmounts: [0.3m, 0.0721m],
			foreignAmounts: [0.3m, 0.3m, 0.2278m]);

		environment.Process(transaction);

		var matched = Assert.Single(wallet.Coins, coin => coin.Amount == Money.Coins(0.3m));
		var change = Assert.Single(wallet.Coins, coin => coin.Amount == Money.Coins(0.0721m));

		// The change has no points whatever the transaction is worth. The matched output waits for the node.
		Assert.True(matched.IsMatchedOutput);
		Assert.False(change.IsMatchedOutput);
		Assert.Null(matched.AnonymityPoints);
		Assert.Equal(0, change.AnonymityPoints);

		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));

		Assert.Equal(2, matched.AnonymityPoints);
		Assert.Equal(0, change.AnonymityPoints);
		Assert.Equal(1, environment.AnnouncedChanges);

		// The wallet file remembers what the transaction is worth.
		Assert.Equal(2, wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));
	}

	[Fact]
	public async Task TransactionWithoutMatchedCoinOfTheWalletIsNotLookedAtAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		// The wallet only has the change of this coinjoin. The node has no answer at all: asking it would fail.
		var transaction = environment.CreateTransaction(
			[BitcoinFactory.CreateOutPoint()],
			ownAmounts: [0.0721m],
			foreignAmounts: [0.3m, 0.3m]);

		environment.Node.OnGetRawTransactionAsync = (_, _) => throw new InvalidOperationException("The node must not be asked.");
		environment.Process(transaction);

		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));

		Assert.Equal(0, Assert.Single(wallet.Coins).AnonymityPoints);
		Assert.Equal(0, environment.AnnouncedChanges);
		Assert.Null(wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));
	}

	[Fact]
	public async Task PointsThatWereFoundOutBeforeShowAtOnceAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		var transaction = environment.CreateTransaction(
			[BitcoinFactory.CreateOutPoint()],
			ownAmounts: [0.3m],
			foreignAmounts: [0.3m, 0.3m]);

		// The wallet ran before and found the points out then.
		wallet.KeyManager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [transaction.GetHash()] = 1 });
		environment.Node.OnGetRawTransactionAsync = (_, _) => throw new InvalidOperationException("The node must not be asked.");

		environment.Process(transaction);

		Assert.Equal(1, Assert.Single(wallet.Coins).AnonymityPoints);
		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));
	}

	[Fact]
	public async Task PointsStayUnknownWhileTheNodeDoesNotAnswerAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		var coinjoin = environment.CreateForeignTransaction(0.5m, 0.5m);
		var transaction = environment.CreateTransaction(
			[new OutPoint(coinjoin, 0)],
			ownAmounts: [0.2m],
			foreignAmounts: [0.2m, 0.0999m]);

		var knownTransactions = environment.Node.OnGetRawTransactionAsync;
		environment.Node.OnGetRawTransactionAsync = (_, _) => throw new HttpRequestException("Connection refused");
		environment.Process(transaction);
		var coin = Assert.Single(wallet.Coins);

		Assert.False(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));
		Assert.Null(coin.AnonymityPoints);
		Assert.Null(wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));

		// The node is back.
		environment.Node.OnGetRawTransactionAsync = knownTransactions;

		// The input came out of a pair, so the transaction is worth half a point, and so is the coin: it is a matched output.
		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));
		Assert.Equal(0.5m, coin.AnonymityPoints);
	}

	[Fact]
	public async Task PointsAreKeptAsSoonAsTheyAreFoundOutAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		// Three coinjoins with a coin of the wallet each. The node answers one question and cannot be reached after that.
		var transactions = Enumerable.Range(0, 3).Select(_ => environment.ProcessCoinjoinWorthOnePoint()).ToArray();
		var knownTransactions = environment.Node.OnGetRawTransactionAsync!;
		var questions = 0;

		environment.Node.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
			Interlocked.Increment(ref questions) == 1
				? knownTransactions(txid, throwIfNotFound)
				: throw new HttpRequestException("Connection refused");

		Assert.False(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));

		// The one answer is not lost: the coin shows its points and the wallet file has them. The node was not asked
		// about the third coinjoin after it failed to answer about the second one.
		Assert.Equal(2, questions);
		Assert.Single(wallet.Coins, coin => coin.AnonymityPoints == 1);
		Assert.Equal(2, wallet.Coins.Count(coin => coin.AnonymityPoints is null));
		Assert.Single(transactions, transaction => wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()) == 1);
		Assert.Equal(2, transactions.Count(transaction => wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()) is null));
		Assert.Equal(1, environment.AnnouncedChanges);

		// The node is back: only the two open questions are asked.
		environment.Node.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
		{
			Interlocked.Increment(ref questions);
			return knownTransactions(txid, throwIfNotFound);
		};

		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));
		Assert.Equal(4, questions);
		Assert.All(wallet.Coins, coin => Assert.Equal(1, coin.AnonymityPoints));
		Assert.All(transactions, transaction => Assert.Equal(1, wallet.KeyManager.GetAnonymityPoints(transaction.GetHash())));
	}

	[Fact]
	public async Task TransactionThatCannotBeWorkedOutDoesNotHoldUpTheOthersAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		var transactions = Enumerable.Range(0, 3).Select(_ => environment.ProcessCoinjoinWorthOnePoint()).ToArray();
		var knownTransactions = environment.Node.OnGetRawTransactionAsync!;

		// The node is there, but what it tells about the input of the coinjoin that it is asked about first cannot be used.
		uint256? troublesomeInput = null;

		environment.Node.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
		{
			troublesomeInput ??= txid;

			return txid == troublesomeInput
				? throw new FormatException("This is not a transaction.")
				: knownTransactions(txid, throwIfNotFound);
		};

		Assert.False(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));

		var troublesome = Assert.Single(transactions, transaction => transaction.Inputs[0].PrevOut.Hash == troublesomeInput);

		Assert.Null(wallet.KeyManager.GetAnonymityPoints(troublesome.GetHash()));
		Assert.All(transactions.Except([troublesome]), transaction => Assert.Equal(1, wallet.KeyManager.GetAnonymityPoints(transaction.GetHash())));
		Assert.Equal(2, wallet.Coins.Count(coin => coin.AnonymityPoints == 1));
		Assert.Single(wallet.Coins, coin => coin.AnonymityPoints is null);
	}

	[Fact]
	public async Task NodeThatDidNotAnswerIsAskedAgainAfterAWaitAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		// The third wait is much longer than the test may take: the test only ends if the first two are the short ones.
		TimeSpan[] waits = [TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(10)];
		wallet.AnonymityPointsRetryDelays = waits;

		environment.ProcessCoinjoinWorthOnePoint();
		var questions = environment.FailToAnswer(times: 2);

		using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keeping = wallet.KeepAnonymityPointsKnownAsync(stop.Token);

		await WaitUntilAsync(() => wallet.Coins.All(coin => coin.AnonymityPoints == 1), "The points of the coin were not found out.");
		await stop.CancelAsync();
		await keeping;

		// The node was not asked again at once, and each wait was at least as long as it is meant to be.
		var moments = questions.ToArray();
		var tolerance = TimeSpan.FromMilliseconds(50);

		Assert.Equal(3, moments.Length);
		Assert.True(Stopwatch.GetElapsedTime(moments[0], moments[1]) >= waits[0] - tolerance, "The node was asked again too early.");
		Assert.True(Stopwatch.GetElapsedTime(moments[1], moments[2]) >= waits[1] - tolerance, "The second wait was not longer than the first one.");
	}

	[Fact]
	public async Task ChangedCoinsAreLookedAtWithoutWaitingAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		// After a question without an answer the wallet would wait much longer than the test may take.
		wallet.AnonymityPointsRetryDelays = [TimeSpan.FromMinutes(10)];

		environment.ProcessCoinjoinWorthOnePoint();
		var questions = environment.FailToAnswer(times: 1);

		using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keeping = wallet.KeepAnonymityPointsKnownAsync(stop.Token);

		await WaitUntilAsync(() => questions.Count == 1, "The node was not asked.");
		Assert.Null(Assert.Single(wallet.Coins).AnonymityPoints);

		// Another coin arrives: the wallet looks at its coins again at once, and the node answers this time.
		environment.ProcessCoinjoinWorthOnePoint();

		await WaitUntilAsync(() => wallet.Coins.All(coin => coin.AnonymityPoints == 1), "The points of the coins were not found out.");
		await stop.CancelAsync();
		await keeping;

		Assert.Equal(2, wallet.Coins.Count());
		Assert.Equal(3, questions.Count);
	}

	[Fact]
	public async Task PointsOfACoinAreForgottenOnceItIsSpentAsync()
	{
		using var environment = new WalletWithNode();
		var wallet = environment.Wallet;

		var coinjoin = environment.CreateForeignTransaction(0.5m, 0.5m);
		var transaction = environment.CreateTransaction(
			[new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 1)],
			ownAmounts: [0.2m],
			foreignAmounts: [0.2m, 0.5999m]);

		environment.Process(transaction);
		var coin = Assert.Single(wallet.Coins);

		// Two inputs out of a pair are worth a point together, and so is the pair of outputs.
		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, coin.AnonymityPoints);
		Assert.Equal(1, wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));

		// The coin is spent. Its points do not go anywhere: the payment that it funds has no matched outputs.
		var payment = environment.CreateTransaction([coin.Outpoint], ownAmounts: [0.05m], foreignAmounts: [0.1499m]);
		environment.Process(payment);

		Assert.True(await wallet.FindOutAnonymityPointsAsync(TestContext.Current.CancellationToken));

		Assert.Equal(0, Assert.Single(wallet.Coins).AnonymityPoints);
		Assert.Null(wallet.KeyManager.GetAnonymityPoints(transaction.GetHash()));
	}

	private static async Task WaitUntilAsync(Func<bool> condition, string failure)
	{
		var waited = Stopwatch.StartNew();

		while (!condition())
		{
			Assert.True(waited.Elapsed < TimeSpan.FromSeconds(30), failure);
			await Task.Delay(20, TestContext.Current.CancellationToken);
		}
	}

	/// <summary>A wallet and a node that has an index of all transactions and knows the ones that the test made.</summary>
	private sealed class WalletWithNode : IDisposable
	{
		private readonly AllTransactionStore _transactionStore;
		private readonly MailboxProcessor<CpfpInfoMessage> _cpfpInfoUpdater;
		private readonly List<Transaction> _transactionsOfNode = [];
		private readonly IDisposable _subscription;
		private int _announcedChanges;
		private uint _height = 100;

		public WalletWithNode()
		{
			var network = Network.Main;
			var eventBus = new EventBus();

			Node = new MockRpcClient
			{
				OnGetRawTransactionAsync = (txid, _) => Task.FromResult(_transactionsOfNode.SingleOrDefault(tx => tx.GetHash() == txid)!)
			};

			_transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, network);
			_cpfpInfoUpdater = Workers.Spawn($"CpfpInfoProvider_{Guid.NewGuid():N}", Workers.EventDriven(Unit.Instance, CpfpInfoUpdater.Create(new MockRpcClient(), eventBus)));
			var walletFactory = WalletWasabi.Wallets.Wallet.CreateFactory(network, Node, _transactionStore, new ServiceConfiguration(), eventBus, new CpfpInfoProvider(_cpfpInfoUpdater));

			Wallet = walletFactory(KeyManager.CreateNew(out _, "password", network));
			_subscription = eventBus.Subscribe<AnonymityPointsChanged>(_ => Interlocked.Increment(ref _announcedChanges));
		}

		public WalletWasabi.Wallets.Wallet Wallet { get; }

		public MockRpcClient Node { get; }

		/// <summary>How often the wallet told that it found out points of coins.</summary>
		public int AnnouncedChanges => Volatile.Read(ref _announcedChanges);

		/// <summary>A transaction of somebody else that the node knows and the wallet does not.</summary>
		public Transaction CreateForeignTransaction(params decimal[] amounts)
		{
			var transaction = CreateTransaction([BitcoinFactory.CreateOutPoint()], ownAmounts: [], foreignAmounts: amounts);
			_transactionsOfNode.Add(transaction);
			return transaction;
		}

		public Transaction CreateTransaction(IEnumerable<OutPoint> inputs, decimal[] ownAmounts, decimal[] foreignAmounts)
		{
			var transaction = Network.Main.CreateTransaction();

			foreach (var input in inputs)
			{
				transaction.Inputs.Add(input);
			}

			foreach (var amount in ownAmounts)
			{
				transaction.Outputs.Add(Money.Coins(amount), Wallet.KeyManager.GetNextReceiveKey().P2Taproot);
			}

			foreach (var amount in foreignAmounts)
			{
				transaction.Outputs.Add(Money.Coins(amount), BitcoinFactory.CreateScript());
			}

			return transaction;
		}

		/// <summary>
		/// The wallet learns of a coinjoin that made a coin for it. The input of the coinjoin came out of a set of three
		/// equal outputs of a transaction that only the node knows, and three of its outputs are equal: it is worth one point.
		/// </summary>
		public Transaction ProcessCoinjoinWorthOnePoint()
		{
			var earlierCoinjoin = CreateForeignTransaction(0.5m, 0.5m, 0.5m);
			var coinjoin = CreateTransaction([new OutPoint(earlierCoinjoin, 0)], ownAmounts: [0.2m], foreignAmounts: [0.2m, 0.2m, 0.0999m]);
			Process(coinjoin);

			return coinjoin;
		}

		/// <summary>
		/// The node cannot be reached the given number of times that it is asked for a transaction, and answers after that.
		/// </summary>
		/// <returns>The moments at which the node was asked.</returns>
		public ConcurrentQueue<long> FailToAnswer(int times)
		{
			var knownTransactions = Node.OnGetRawTransactionAsync!;
			var questions = new ConcurrentQueue<long>();

			Node.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
			{
				questions.Enqueue(Stopwatch.GetTimestamp());

				return questions.Count <= times
					? throw new HttpRequestException("Connection refused")
					: knownTransactions(txid, throwIfNotFound);
			};

			return questions;
		}

		/// <summary>The wallet learns of the transaction as a confirmed one.</summary>
		public void Process(Transaction transaction) =>
			Wallet.TransactionProcessor.Process(new SmartTransaction(transaction, new Height.ChainHeight(_height++), new uint256(_height)));

		public void Dispose()
		{
			_subscription.Dispose();
			_cpfpInfoUpdater.Dispose();
			_transactionStore.Dispose();
		}
	}
}
