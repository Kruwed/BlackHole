using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionProcessor.ProcessBlock"/>: the wallet goes through every block of the chain
/// and picks the transactions that concern it.
/// </summary>
public class TransactionProcessorBlockTests
{
	private static readonly Network Network = Network.RegTest;

	[Fact]
	public async Task PicksTheTransactionsThatConcernTheWalletAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var payment = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(1));
		var foreignTransactions = Enumerable.Range(0, 50).Select(_ => CreatePayment(CreateForeignScript(), Money.Coins(1))).ToArray();
		var block = node.MineBlock([.. foreignTransactions[..20], payment, .. foreignTransactions[20..]]);

		// Nothing in the first block concerns the wallet.
		Assert.Empty(fixture.Processor.ProcessBlock(node.MineBlock(foreignTransactions[..5]), new Height.ChainHeight(1)));
		Assert.Empty(fixture.Processor.Coins);

		var result = Assert.Single(fixture.Processor.ProcessBlock(block, new Height.ChainHeight(2)));

		Assert.Equal(payment.GetHash(), result.Transaction.GetHash());
		Assert.Equal(new Height.ChainHeight(2), result.Transaction.Height);
		Assert.Equal(block.GetHash(), result.Transaction.BlockHash);
		Assert.Equal(21, result.Transaction.BlockIndex);
		Assert.Equal(block.Header.BlockTime, result.Transaction.FirstSeen);

		var coin = Assert.Single(fixture.Processor.Coins);
		Assert.Equal(Money.Coins(1), coin.Amount);
		Assert.True(coin.Confirmed);

		// The wallet was told about it, and it keeps nothing but its own transaction.
		Assert.Equal(payment.GetHash(), Assert.Single(fixture.RelevantTransactions).Transaction.GetHash());
		Assert.Equal(payment.GetHash(), Assert.Single(fixture.TransactionStore.GetTransactions()).GetHash());

		foreach (var foreignTransaction in foreignTransactions)
		{
			Assert.False(fixture.Processor.ConcernsWallet(foreignTransaction));
		}
	}

	[Fact]
	public async Task FindsTheTransactionsThatSpendFromTheWalletAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var payment = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(1));
		fixture.Processor.ProcessBlock(node.MineBlock(payment), new Height.ChainHeight(1));

		// It pays to somebody else, so only its input tells that it is a transaction of the wallet.
		var spending = CreateSpending(payment, 0, CreateForeignScript());
		Assert.True(fixture.Processor.ConcernsWallet(spending));

		var result = Assert.Single(fixture.Processor.ProcessBlock(node.MineBlock(CreatePayment(CreateForeignScript(), Money.Coins(3)), spending), new Height.ChainHeight(2)));

		Assert.Equal(spending.GetHash(), result.Transaction.GetHash());
		Assert.Single(result.NewlySpentCoins);
		Assert.Empty(fixture.Processor.Coins);

		var spentCoin = Assert.Single(fixture.Processor.Coins.AsAllCoinsView());
		Assert.Equal(spending.GetHash(), spentCoin.SpenderTransaction?.GetHash());
	}

	[Fact]
	public async Task FindsTheCoinbaseThatPaysToTheWalletAsync()
	{
		using var fixture = await Fixture.CreateAsync();

		var block = Network.Consensus.ConsensusFactory.CreateBlock();
		var coinbase = Network.CreateTransaction();
		coinbase.Inputs.Add(new TxIn(new OutPoint(), new Script(Op.GetPushOp(1))));
		coinbase.Outputs.Add(Money.Coins(50), fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey());
		block.AddTransaction(coinbase);
		block.UpdateMerkleRoot();

		Assert.True(coinbase.IsCoinBase);
		Assert.True(fixture.Processor.ConcernsWallet(coinbase));

		Assert.Single(fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1)));
		Assert.Equal(Money.Coins(50), Assert.Single(fixture.Processor.Coins).Amount);
	}

	[Fact]
	public async Task FindsThePaymentsToTheKeysThatTurnUpWhileTheBlockIsProcessedAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);
		var keyManager = fixture.KeyManager;

		// The wallet watches a gap of keys beyond the last one that was used. The first key beyond the gap is not watched yet.
		var watchedKeys = keyManager.GetKeys(isInternal: false).OrderBy(key => key.Index).ToArray();
		var lastWatchedKey = watchedKeys[^1];
		var keyBeyondTheGap = keyManager.TaprootExtPubKey.Derive(0).Derive((uint)lastWatchedKey.Index + 5).PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		var paymentBeyondTheGap = CreatePayment(keyBeyondTheGap, Money.Coins(1));
		var paymentToTheLastKey = CreatePayment(lastWatchedKey.GetAssumedScriptPubKey(), Money.Coins(2));
		Assert.False(fixture.Processor.ConcernsWallet(paymentBeyondTheGap));

		// The payment that makes the wallet watch more keys comes after the payment to one of those keys.
		var block = node.MineBlock(paymentBeyondTheGap, CreatePayment(CreateForeignScript(), Money.Coins(3)), paymentToTheLastKey);
		var results = fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1));

		// Both are found, each of them once.
		Assert.Equal(
			new[] { paymentToTheLastKey.GetHash(), paymentBeyondTheGap.GetHash() },
			results.Select(result => result.Transaction.GetHash()));
		Assert.Equal(Money.Coins(3), fixture.Processor.Coins.TotalAmount());
		Assert.Equal(2, fixture.RelevantTransactions.Count);
		Assert.True(keyManager.GetKeys(isInternal: false).Length > watchedKeys.Length);
	}

	[Fact]
	public async Task NoticesThatACoinThatTurnsUpLateIsSpentInTheSameBlockAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);
		var keyManager = fixture.KeyManager;

		var watchedKeys = keyManager.GetKeys(isInternal: false).OrderBy(key => key.Index).ToArray();
		var lastWatchedKey = watchedKeys[^1];
		var keyBeyondTheGap = keyManager.TaprootExtPubKey.Derive(0).Derive((uint)lastWatchedKey.Index + 5).PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

		// The first transaction pays to a key that the wallet does not watch yet. The second one spends that coin right away
		// and pays to a key that the wallet does watch, which makes it watch the key of the first one too.
		var paymentBeyondTheGap = CreatePayment(keyBeyondTheGap, Money.Coins(1));
		var spending = CreateSpending(paymentBeyondTheGap, 0, lastWatchedKey.GetAssumedScriptPubKey());

		var block = node.MineBlock(paymentBeyondTheGap, spending);
		fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1));

		// The coin of the first transaction is known and it is known to be spent.
		var allCoins = fixture.Processor.Coins.AsAllCoinsView().ToDictionary(coin => coin.TransactionId);
		Assert.Equal(2, allCoins.Count);
		Assert.Equal(spending.GetHash(), allCoins[paymentBeyondTheGap.GetHash()].SpenderTransaction?.GetHash());

		var unspentCoin = Assert.Single(fixture.Processor.Coins);
		Assert.Equal(spending.GetHash(), unspentCoin.TransactionId);
	}

	[Fact]
	public async Task ProcessingABlockAgainChangesNothingAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var payment = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(1));
		var spending = CreateSpending(payment, 0, fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey());
		var block = node.MineBlock(payment, spending);

		fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1));
		var keyCount = fixture.KeyManager.GetKeyCount();

		// E.g. when a wallet is resynchronized from an earlier height.
		var results = fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1));

		Assert.Equal(2, results.Count);
		Assert.Equal(keyCount, fixture.KeyManager.GetKeyCount());
		Assert.Equal(2, fixture.Processor.Coins.AsAllCoinsView().Count());
		Assert.Equal(spending.GetHash(), Assert.Single(fixture.Processor.Coins).TransactionId);
		Assert.Equal(2, fixture.TransactionStore.GetTransactions().Count());
	}

	private static Transaction CreatePayment(Script destination, Money amount)
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(amount, destination);
		return tx;
	}

	private static Transaction CreateSpending(Transaction funding, uint outputIndex, Script destination)
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(funding, outputIndex), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(funding.Outputs[outputIndex].Value - Money.Satoshis(1_000), destination);
		return tx;
	}

	/// <summary>A script that the wallet has nothing to do with.</summary>
	private static Script CreateForeignScript()
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
	}

	private sealed class Fixture : IDisposable
	{
		private readonly IDisposable _subscription;

		private Fixture(AllTransactionStore transactionStore)
		{
			TransactionStore = transactionStore;
			KeyManager = KeyManager.CreateNew(out _, "", Network);

			var eventBus = new EventBus();
			_subscription = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e => RelevantTransactions.Add(e.Result));
			Processor = new TransactionProcessor(transactionStore, KeyManager, eventBus);
		}

		public KeyManager KeyManager { get; }
		public AllTransactionStore TransactionStore { get; }
		public TransactionProcessor Processor { get; }

		/// <summary>What the wallet was told about its transactions.</summary>
		public List<ProcessedResult> RelevantTransactions { get; } = [];

		public static async Task<Fixture> CreateAsync()
		{
			var transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, Network);
			await transactionStore.InitializeAsync();
			return new Fixture(transactionStore);
		}

		public void Dispose()
		{
			_subscription.Dispose();
			TransactionStore.Dispose();
		}
	}
}
