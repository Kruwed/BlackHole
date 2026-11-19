using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.BlockchainAnalysis;

/// <summary>
/// Tests for <see cref="AnonymityPointsCalculator"/>: the points of a transaction depend on the transactions that
/// created its inputs, and the calculator gets those from the wallet and from the node.
/// </summary>
public class AnonymityPointsCalculatorTests
{
	private static readonly uint256 BlockOfSpender = new(1000);

	[Fact]
	public async Task TransactionWithoutMatchedOutputsIsWorthNothingAndTheNodeIsNotAskedAsync()
	{
		var parent = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m);
		var payment = CreateTransaction([new OutPoint(parent, 0)], 0.3m, 0.1999m);

		// A node without any answer: asking it fails the test.
		var calculator = new AnonymityPointsCalculator(new MockRpcClient(), new TransactionsOfWallet());

		Assert.Equal(0, await calculator.CalculateAsync(Confirmed(payment), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task CoinbaseTransactionIsWorthNothingAsync()
	{
		var coinbase = Network.Main.CreateTransaction();
		coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue), new Script(Op.GetPushOp(1), Op.GetPushOp(1))));
		coinbase.Outputs.Add(Money.Coins(1m), BitcoinFactory.CreateScript());
		coinbase.Outputs.Add(Money.Coins(1m), BitcoinFactory.CreateScript());
		Assert.True(coinbase.IsCoinBase);

		var calculator = new AnonymityPointsCalculator(new MockRpcClient(), new TransactionsOfWallet());

		Assert.Equal(0, await calculator.CalculateAsync(Confirmed(coinbase), TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// Three inputs, two of which were matched outputs of the transactions that created them, and three equal outputs
	/// plus two others: two points.
	/// </summary>
	[Fact]
	public async Task LowerOfMatchedInputsAndMatchedOutputsCountsAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.2m);
		var otherCoinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.7m, 0.3m, 0.3m);
		var payment = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.4m, 0.6m);

		var transaction = CreateTransaction(
			[new OutPoint(coinjoin, 1), new OutPoint(otherCoinjoin, 2), new OutPoint(payment, 0)],
			0.1m, 0.1m, 0.1m, 0.45m, 0.6m);

		var node = new MockRpcClient { OnGetRawTransactionAsync = NodeWithIndexOf(coinjoin, otherCoinjoin, payment) };
		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TransactionIsNotWorthMoreThanItsMatchedOutputsAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.5m);

		// All three inputs were matched outputs, and only two outputs are matched.
		var transaction = CreateTransaction(
			[new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 1), new OutPoint(coinjoin, 2)],
			0.7m, 0.7m, 0.0999m);

		var node = new MockRpcClient { OnGetRawTransactionAsync = NodeWithIndexOf(coinjoin) };
		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task FirstCoinjoinOfCoinsThatWereNeverMatchedIsWorthNothingAsync()
	{
		var deposit = CreateTransaction([BitcoinFactory.CreateOutPoint()], 1m, 0.3m);
		var otherDeposit = CreateTransaction([BitcoinFactory.CreateOutPoint()], 1.2m, 0.01m);

		var transaction = CreateTransaction(
			[new OutPoint(deposit, 0), new OutPoint(otherDeposit, 0)],
			0.9m, 0.9m, 0.1m, 0.2999m);

		var node = new MockRpcClient { OnGetRawTransactionAsync = NodeWithIndexOf(deposit, otherDeposit) };
		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(0, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task NodeIsNotAskedForTheTransactionsOfTheWalletAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.2m);
		var transaction = CreateTransaction([new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 2)], 0.3m, 0.3m, 0.0999m);

		// A node without any answer: asking it fails the test.
		var calculator = new AnonymityPointsCalculator(new MockRpcClient(), new TransactionsOfWallet(coinjoin));

		Assert.Equal(1, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// A coinjoin has many inputs, and the transaction of each has to come from the node. Once as many inputs turned out
	/// to be matched outputs as the transaction has matched outputs, the other inputs change nothing any more.
	/// </summary>
	[Fact]
	public async Task NodeIsNotAskedAboutMoreInputsThanItTakesAsync()
	{
		var parents = Enumerable.Range(0, 50)
			.Select(_ => CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m))
			.ToArray();

		var transaction = CreateTransaction(parents.Select(parent => new OutPoint(parent, 0)), 12m, 12m, 0.999m);

		var asked = new HashSet<uint256>();
		var known = NodeWithIndexOf(parents);
		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
			{
				asked.Add(txid);
				return known(txid, throwIfNotFound);
			}
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
		Assert.Equal(AnonymityPointsCalculator.BatchSize, asked.Count);
	}

	/// <summary>
	/// The outputs of one coinjoin are spent by many later transactions. The node is asked for it once.
	/// </summary>
	[Fact]
	public async Task NodeIsAskedOnlyOnceForATransactionThatSeveralTransactionsSpendAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.5m, 0.2m);
		var transaction = CreateTransaction([new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 3)], 0.3m, 0.3m, 0.0999m);
		var otherTransaction = CreateTransaction([new OutPoint(coinjoin, 1), new OutPoint(coinjoin, 2)], 0.4m, 0.4m, 0.1999m);

		var asked = new List<uint256>();
		var known = NodeWithIndexOf(coinjoin);
		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
			{
				asked.Add(txid);
				return known(txid, throwIfNotFound);
			}
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(1, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(otherTransaction), TestContext.Current.CancellationToken));
		Assert.Equal([coinjoin.GetHash()], asked);
	}

	/// <summary>
	/// A node without an index of all transactions only finds a confirmed transaction if it is told the block. For a
	/// spender that is in a block, the node tells from the data of that block where the spent outputs were created.
	/// </summary>
	[Fact]
	public async Task NodeWithoutIndexFindsTheInputsOfAConfirmedTransactionThroughItsBlockAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.2m);
		var payment = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.4m, 0.6m);
		var transaction = CreateTransaction([new OutPoint(coinjoin, 0), new OutPoint(payment, 1)], 0.3m, 0.3m, 0.4999m);

		var blocks = new Dictionary<int, (uint256 Hash, Transaction Transaction)>
		{
			[700] = (new uint256(700), coinjoin),
			[705] = (new uint256(705), payment)
		};

		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = NodeWithIndexOf(),
			OnGetPrevoutHeightsAsync = (txid, blockHash) =>
			{
				Assert.Equal(transaction.GetHash(), txid);
				Assert.Equal(BlockOfSpender, blockHash);
				return Task.FromResult<IReadOnlyList<int?>?>([700, 705]);
			},
			OnGetBlockHashAsync = height => Task.FromResult(blocks[height].Hash),
			OnGetRawTransactionFromBlockAsync = (txid, blockHash) =>
				Task.FromResult(blocks.Values.Where(block => block.Hash == blockHash && block.Transaction.GetHash() == txid).Select(block => block.Transaction).SingleOrDefault())
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(1, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// The outputs that an unconfirmed transaction spends are still unspent in the chain, so the node tells how many
	/// confirmations they have, and with that in which block they were created.
	/// </summary>
	[Fact]
	public async Task NodeWithoutIndexFindsTheInputsOfAnUnconfirmedTransactionThroughTheirConfirmationsAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m, 0.2m);
		var otherCoinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.8m, 0.8m);
		var unconfirmedParent = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.25m, 0.25m);
		var transaction = CreateTransaction(
			[new OutPoint(coinjoin, 1), new OutPoint(otherCoinjoin, 0), new OutPoint(unconfirmedParent, 0)],
			0.3m, 0.3m, 0.3m, 0.3m, 0.3499m);

		const int TipHeight = 800;
		var confirmations = new Dictionary<uint256, int> { [coinjoin.GetHash()] = 1, [otherCoinjoin.GetHash()] = 101 };
		var blocks = new Dictionary<int, (uint256 Hash, Transaction Transaction)>
		{
			[800] = (new uint256(800), coinjoin),
			[700] = (new uint256(700), otherCoinjoin)
		};

		var node = new MockRpcClient
		{
			// The node only has the parent that is in its mempool.
			OnGetRawTransactionAsync = NodeWithIndexOf(unconfirmedParent),
			OnGetBlockCountAsync = () => Task.FromResult(TipHeight),
			OnGetTxOutAsync = (txid, _, includeMempool) =>
			{
				Assert.False(includeMempool);
				return confirmations.TryGetValue(txid, out var count) ? new GetTxOutResponse { Confirmations = count } : null;
			},
			OnGetBlockHashAsync = height => Task.FromResult(blocks[height].Hash),
			OnGetRawTransactionFromBlockAsync = (txid, blockHash) =>
				Task.FromResult(blocks.Values.Where(block => block.Hash == blockHash && block.Transaction.GetHash() == txid).Select(block => block.Transaction).SingleOrDefault())
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());
		var unconfirmed = new SmartTransaction(transaction, Height.Mempool);

		Assert.Equal(3, await calculator.CalculateAsync(unconfirmed, TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// The confirmations of an output count from the last block. A block that arrives while the node is asked makes the
	/// answers count from different blocks, so the calculation is left for later.
	/// </summary>
	[Fact]
	public async Task PointsStayUnknownWhenABlockArrivesWhileTheNodeIsAskedAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m);
		var transaction = CreateTransaction([new OutPoint(coinjoin, 0)], 0.2m, 0.2m, 0.0999m);

		var tipHeight = 800;
		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = NodeWithIndexOf(),
			OnGetBlockCountAsync = () => Task.FromResult(tipHeight++),
			OnGetTxOutAsync = (_, _, _) => new GetTxOutResponse { Confirmations = 3 }
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());
		var unconfirmed = new SmartTransaction(transaction, Height.Mempool);

		Assert.Null(await calculator.CalculateAsync(unconfirmed, TestContext.Current.CancellationToken));
	}

	/// <summary>
	/// A pruned node may not have the block of an input any more. Then the points are not known, and what the node did
	/// tell is not asked for again when the calculation is repeated.
	/// </summary>
	[Fact]
	public async Task PointsStayUnknownUntilTheNodeToldAboutEnoughInputsAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m);
		var oldCoinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.8m, 0.8m);
		var transaction = CreateTransaction([new OutPoint(coinjoin, 0), new OutPoint(oldCoinjoin, 1)], 0.3m, 0.3m, 0.3m, 0.3999m);

		var asked = new List<uint256>();
		var available = new List<Transaction> { coinjoin };
		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = (txid, _) =>
			{
				asked.Add(txid);
				return Task.FromResult(available.SingleOrDefault(tx => tx.GetHash() == txid)!);
			},
			OnGetPrevoutHeightsAsync = (_, _) => Task.FromResult<IReadOnlyList<int?>?>(null)
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		// One input was a matched output, the other one may have been one too: between one point and two.
		Assert.Null(await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
		Assert.Equal([coinjoin.GetHash(), oldCoinjoin.GetHash()], asked);

		available.Add(oldCoinjoin);
		asked.Clear();

		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
		Assert.Equal([oldCoinjoin.GetHash()], asked);
	}

	/// <summary>
	/// Inputs that the node cannot tell about do not matter once the transaction has as many inputs that were matched
	/// outputs as it has matched outputs.
	/// </summary>
	[Fact]
	public async Task PointsAreKnownWithoutTheMissingInputsIfTheOthersAreEnoughAsync()
	{
		var coinjoin = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.5m, 0.5m);
		var missing = CreateTransaction([BitcoinFactory.CreateOutPoint()], 0.8m, 0.8m);
		var transaction = CreateTransaction(
			[new OutPoint(coinjoin, 0), new OutPoint(coinjoin, 1), new OutPoint(missing, 1)],
			0.9m, 0.9m, 0.0999m);

		var node = new MockRpcClient
		{
			OnGetRawTransactionAsync = NodeWithIndexOf(coinjoin),
			OnGetPrevoutHeightsAsync = (_, _) => Task.FromResult<IReadOnlyList<int?>?>(null)
		};

		var calculator = new AnonymityPointsCalculator(node, new TransactionsOfWallet());

		Assert.Equal(2, await calculator.CalculateAsync(Confirmed(transaction), TestContext.Current.CancellationToken));
	}

	private static SmartTransaction Confirmed(Transaction transaction) =>
		new(transaction, new Height.ChainHeight(1000), BlockOfSpender);

	/// <summary>A node that finds exactly the given transactions when it is asked for a transaction without a block.</summary>
	private static Func<uint256, bool, Task<Transaction>> NodeWithIndexOf(params Transaction[] transactions) =>
		(txid, _) => Task.FromResult(transactions.SingleOrDefault(transaction => transaction.GetHash() == txid)!);

	private static Transaction CreateTransaction(IEnumerable<OutPoint> inputs, params decimal[] outputAmounts)
	{
		var transaction = Network.Main.CreateTransaction();

		foreach (var input in inputs)
		{
			transaction.Inputs.Add(input);
		}

		foreach (var amount in outputAmounts)
		{
			transaction.Outputs.Add(Money.Coins(amount), BitcoinFactory.CreateScript());
		}

		return transaction;
	}

	/// <summary>The transactions that the wallet has itself.</summary>
	private sealed class TransactionsOfWallet(params Transaction[] transactions) : ITransactionStore
	{
		public bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? sameStx)
		{
			sameStx = transactions
				.Where(transaction => transaction.GetHash() == hash)
				.Select(transaction => new SmartTransaction(transaction, new Height.ChainHeight(900)))
				.SingleOrDefault();

			return sameStx is not null;
		}
	}
}
