using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

/// <summary>
/// Tests for <see cref="WalletSynchronizer"/>: the wallet learns everything about the chain from the RPC interface of the user's node.
/// </summary>
public class WalletSynchronizerTests
{
	private static readonly Network Network = Network.RegTest;

	[Fact]
	public async Task NewWalletStartsAtTheTipOfTheChainAsync()
	{
		var node = new FakeBitcoinNode(Network);
		node.MineBlocks(50);

		// The wallet was created before the node could tell the height of the chain.
		using var wallet = await TestWallet.CreateAsync(node, KeyManager.CreateNew(out _, "", Network));
		Assert.Null(wallet.KeyManager.GetBestHeight());

		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		// Nothing concerns it in the blocks that existed already, so they are not even asked for.
		Assert.Equal(new Height.ChainHeight(50), wallet.KeyManager.GetBestHeight());
		Assert.Equal(new Height.ChainHeight(50), wallet.KeyManager.GetBirthHeight());
		Assert.Empty(node.RequestedBlocks);
		Assert.Equal(new WalletSyncStatus.Synchronized(50), wallet.Synchronizer.Status);
		Assert.True(wallet.Synchronizer.InitialSynchronizationFinished.IsCompletedSuccessfully);

		// It follows the chain from there.
		var payment = CreatePayment(wallet.KeyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(payment);
		node.MineBlock();

		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Equal(new WalletSyncStatus.Synchronized(52), wallet.Synchronizer.Status);
		Assert.Equal([node.GetBlockHash(50), node.GetBlockHash(51), node.GetBlockHash(52)], wallet.KeyManager.GetProcessedBlocks().BlockHashes);
		var coin = Assert.Single(wallet.Coins);
		Assert.Equal(payment.GetHash(), coin.TransactionId);
		Assert.Equal(new Height.ChainHeight(51), coin.Height);
		Assert.Equal([node.GetBlockHash(51), node.GetBlockHash(52)], node.RequestedBlocks);
	}

	[Fact]
	public async Task NewWalletStartsWhereTheHeadersOfTheNodeEndAsync()
	{
		// The node is still downloading the chain: it knows about 500 blocks and has 40 of them.
		var node = new FakeBitcoinNode(Network) { Headers = 500 };
		node.MineBlocks(40);

		using var wallet = await TestWallet.CreateAsync(node, KeyManager.CreateNew(out _, "", Network));

		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		// The blocks that the node is going to download are too old to concern a wallet that was created now.
		Assert.Equal(new Height.ChainHeight(500), wallet.KeyManager.GetBestHeight());
		Assert.Equal(new WalletSyncStatus.NodeSynchronizing(40, 500), wallet.Synchronizer.Status);
		Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);
		Assert.Empty(node.RequestedBlocks);
	}

	[Fact]
	public async Task ProcessesTheBlocksThatTheWalletDidNotSeeAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();

		// A payment to the wallet in block 12, which is spent in block 20. Another payment in block 25.
		node.MineBlocks(11);
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(CreateForeignTransaction(), payment, CreateForeignTransaction());
		node.MineBlocks(7);
		var spending = CreateSpending(payment, 0, CreateForeignScript());
		node.MineBlock(spending);
		node.MineBlocks(4);
		var secondPayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(2));
		node.MineBlock(secondPayment);
		node.MineBlocks(5);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		Assert.Equal(new Height.ChainHeight(0), keyManager.GetBestHeight());

		// The first round processes the blocks, the next one finds nothing new.
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);
		Assert.Equal(new WalletSyncStatus.Scanning(30, 30), wallet.Synchronizer.Status);
		Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);

		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);
		Assert.Equal(new WalletSyncStatus.Synchronized(30), wallet.Synchronizer.Status);
		Assert.True(wallet.Synchronizer.InitialSynchronizationFinished.IsCompletedSuccessfully);

		// Every block was asked for once, in the order of the chain.
		Assert.Equal(Enumerable.Range(1, 30).Select(height => node.GetBlockHash((uint)height)), node.RequestedBlocks);

		var allCoins = wallet.Processor.Coins.AsAllCoinsView().ToArray();
		Assert.Equal(2, allCoins.Length);

		var spentCoin = Assert.Single(allCoins, coin => coin.TransactionId == payment.GetHash());
		Assert.Equal(new Height.ChainHeight(12), spentCoin.Height);
		Assert.Equal(spending.GetHash(), spentCoin.SpenderTransaction?.GetHash());
		Assert.Equal(new Height.ChainHeight(20), spentCoin.SpenderTransaction?.Height);
		Assert.Equal(node.GetBlockHash(20), spentCoin.SpenderTransaction?.BlockHash);

		var unspentCoin = Assert.Single(wallet.Coins);
		Assert.Equal(secondPayment.GetHash(), unspentCoin.TransactionId);
		Assert.Equal(Money.Coins(2), unspentCoin.Amount);
		Assert.Equal(new Height.ChainHeight(25), unspentCoin.Height);

		// Only the transactions of the wallet are kept.
		Assert.Equal(3, wallet.TransactionStore.GetTransactions().Count());

		// The wallet file knows how far the wallet got, and the last blocks that it processed on top of the block that it started at.
		var (savedHeight, savedBlockHashes) = KeyManager.FromFile(keyManager.FilePath!).GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(30), savedHeight);
		Assert.Equal(Enumerable.Range(0, 31).Select(height => node.GetBlockHash((uint)height)), savedBlockHashes);

		// The height of the wallet was announced, and the height that it reached at the end for sure.
		Assert.Equal(30u, wallet.AnnouncedHeights[^1]);
		Assert.IsType<WalletSyncStatus.Synchronized>(wallet.AnnouncedStatuses[^1]);
	}

	[Fact]
	public async Task FollowsAReorganizationOfTheChainAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();

		node.MineBlocks(10);
		var oldPayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(oldPayment);
		node.MineBlocks(3);
		var reorganizedPayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(2));
		var droppedPayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(3));
		node.MineBlock(reorganizedPayment);
		node.MineBlock(droppedPayment);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(16), wallet.Synchronizer.Status);
		Assert.Equal(3, wallet.Coins.Count());
		Assert.All(wallet.Coins, coin => Assert.True(coin.Confirmed));

		// The last two blocks are replaced by three others. One of the two payments is in the new blocks again, the other one is not.
		var staleBlocks = node.DisconnectBlocks(2);
		node.MineBlock();
		node.MineBlock(reorganizedPayment);
		node.MineBlock();
		var requestsBefore = node.RequestedBlocks.Count;

		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(17), wallet.Synchronizer.Status);
		Assert.Equal(new Height.ChainHeight(17), keyManager.GetBestHeight());

		// Only the new blocks were fetched.
		Assert.Equal([node.GetBlockHash(15), node.GetBlockHash(16), node.GetBlockHash(17)], node.RequestedBlocks.Skip(requestsBefore));

		var coins = wallet.Coins.ToDictionary(coin => coin.TransactionId);
		Assert.Equal(3, coins.Count);

		// Untouched by the reorganization.
		Assert.Equal(new Height.ChainHeight(11), coins[oldPayment.GetHash()].Height);

		// Confirmed again, in another block.
		Assert.Equal(new Height.ChainHeight(16), coins[reorganizedPayment.GetHash()].Height);
		Assert.Equal(node.GetBlockHash(16), coins[reorganizedPayment.GetHash()].Transaction.BlockHash);
		Assert.NotEqual(staleBlocks[1].GetHash(), coins[reorganizedPayment.GetHash()].Transaction.BlockHash);

		// Not confirmed any more.
		Assert.Equal(Height.Mempool, coins[droppedPayment.GetHash()].Height);
		Assert.False(coins[droppedPayment.GetHash()].Transaction.Confirmed);

		// The remembered blocks are those of the chain of the node.
		var (_, blockHashes) = keyManager.GetProcessedBlocks();
		Assert.Equal(Enumerable.Range(0, 18).Select(height => node.GetBlockHash((uint)height)), blockHashes);
	}

	[Fact]
	public async Task UndoesTheSpentCoinsOfReorganizedBlocksTooAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();

		// A payment to the wallet, which the wallet spends to itself in the next block.
		node.MineBlocks(10);
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		var spending = CreateSpending(payment, 0, keyManager.GetNextReceiveKey().GetAssumedScriptPubKey());
		node.MineBlock(payment);
		node.MineBlock(spending);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		var allCoins = wallet.Processor.Coins.AsAllCoinsView().ToDictionary(coin => coin.TransactionId);
		Assert.Equal(new Height.ChainHeight(11), allCoins[payment.GetHash()].Height);
		Assert.Equal(new Height.ChainHeight(12), allCoins[spending.GetHash()].Height);

		// Both blocks are replaced by blocks without the transactions.
		node.DisconnectBlocks(2);
		node.MineBlocks(3);
		await SynchronizeFullyAsync(wallet);

		// Nothing of the two transactions is confirmed any more: neither the coin that is left of them, nor the one that is spent.
		allCoins = wallet.Processor.Coins.AsAllCoinsView().ToDictionary(coin => coin.TransactionId);
		Assert.Equal(2, allCoins.Count);
		Assert.All(allCoins.Values, coin => Assert.Equal(Height.Mempool, coin.Height));
		Assert.All(allCoins.Values, coin => Assert.False(coin.Confirmed));
		Assert.All(wallet.TransactionStore.GetTransactions(), tx => Assert.False(tx.Confirmed));
		Assert.Equal(spending.GetHash(), Assert.Single(wallet.Coins).TransactionId);

		// They are confirmed again later.
		node.MineBlock(payment, spending);
		await SynchronizeFullyAsync(wallet);

		allCoins = wallet.Processor.Coins.AsAllCoinsView().ToDictionary(coin => coin.TransactionId);
		Assert.All(allCoins.Values, coin => Assert.Equal(new Height.ChainHeight(14), coin.Height));
		Assert.All(wallet.TransactionStore.GetTransactions(), tx => Assert.Equal(node.GetBlockHash(14), tx.BlockHash));
	}

	[Fact]
	public async Task FollowsAReorganizationThatHappenedWhileTheWalletWasNotRunningAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();

		node.MineBlocks(10);
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(payment);
		node.MineBlocks(2);

		using (var wallet = await TestWallet.CreateAsync(node, keyManager))
		{
			await SynchronizeFullyAsync(wallet);
			Assert.Equal(new Height.ChainHeight(11), Assert.Single(wallet.Coins).Height);
		}

		// The block of the payment and the ones after it are replaced, while the wallet is not running. The payment moves two blocks up.
		node.DisconnectBlocks(3);
		node.MineBlocks(2);
		node.MineBlock(payment);

		// The wallet starts again with what its files tell.
		var reloadedKeyManager = KeyManager.FromFile(keyManager.FilePath!);
		Assert.Equal(new Height.ChainHeight(13), reloadedKeyManager.GetBestHeight());

		using var restartedWallet = await TestWallet.CreateAsync(node, reloadedKeyManager);
		Assert.Equal(new Height.ChainHeight(11), Assert.Single(restartedWallet.Coins).Height);

		await SynchronizeFullyAsync(restartedWallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(13), restartedWallet.Synchronizer.Status);
		var coin = Assert.Single(restartedWallet.Coins);
		Assert.Equal(new Height.ChainHeight(13), coin.Height);
		Assert.Equal(node.GetBlockHash(13), coin.Transaction.BlockHash);
	}

	[Fact]
	public async Task GoesBackToTheNodeThatIsBehindOnTheSameChainAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();

		node.MineBlocks(10);
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(payment);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);
		Assert.True(Assert.Single(wallet.Coins).Confirmed);

		// The node lost its newest block, and nothing took its place yet. The wallet shows what the node has.
		node.DisconnectBlocks(1);
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Equal(new Height.ChainHeight(10), keyManager.GetBestHeight());
		Assert.Equal(new WalletSyncStatus.Synchronized(10), wallet.Synchronizer.Status);
		Assert.False(Assert.Single(wallet.Coins).Confirmed);
	}

	[Fact]
	public async Task NewWalletNoticesThatItsFirstBlockIsDroppedAsync()
	{
		var node = new FakeBitcoinNode(Network);
		node.MineBlocks(10);

		// A new wallet starts at the tip of the chain. The very next block pays to it.
		using var wallet = await TestWallet.CreateAsync(node, KeyManager.CreateNew(out _, "", Network));
		await SynchronizeFullyAsync(wallet);

		// The wallet did not process block 10, but it knows which block it stands on.
		var (startHeight, startBlockHashes) = wallet.KeyManager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(10), startHeight);
		Assert.Equal([node.GetBlockHash(10)], startBlockHashes);
		Assert.Empty(node.RequestedBlocks);

		var payment = CreatePayment(wallet.KeyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(payment);
		await SynchronizeFullyAsync(wallet);
		Assert.True(Assert.Single(wallet.Coins).Confirmed);

		// The block is dropped and nothing takes its place for now.
		node.DisconnectBlocks(1);
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new Height.ChainHeight(10), wallet.KeyManager.GetBestHeight());
		Assert.False(Assert.Single(wallet.Coins).Confirmed);

		// Another block confirms the payment.
		node.MineBlock();
		node.MineBlock(payment);
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new Height.ChainHeight(12), Assert.Single(wallet.Coins).Height);
	}

	[Fact]
	public async Task WalletThatWasToldWhereToStartTakesTheBlockOfTheNodeAsync()
	{
		var node = new FakeBitcoinNode(Network);
		node.MineBlocks(20);

		// E.g. a resync from block 16, or a wallet file that tells a height and no blocks.
		var keyManager = RecoverWallet();
		keyManager.SetResyncParameters(16, keyManager.MinGapLimit);
		Assert.Empty(keyManager.GetProcessedBlocks().BlockHashes);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		var (height, blockHashes) = keyManager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(20), height);
		Assert.Equal(Enumerable.Range(15, 6).Select(h => node.GetBlockHash((uint)h)), blockHashes);
		Assert.Equal(Enumerable.Range(16, 5).Select(h => node.GetBlockHash((uint)h)), node.RequestedBlocks);
	}

	[Fact]
	public async Task WaitsForTheNodeThatIsFarBehindAsync()
	{
		var node = new FakeBitcoinNode(Network);
		node.MineBlocks(30);

		// The wallet knows nothing about the blocks that the node has: it got far ahead of them with another node.
		var keyManager = RecoverWallet();
		keyManager.SetResyncParameters(1_001, keyManager.MinGapLimit);
		keyManager.SetBestBlock(1_001, uint256.One);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Equal(new WalletSyncStatus.NodeSynchronizing(30, 30), wallet.Synchronizer.Status);
		Assert.Equal(new Height.ChainHeight(1_001), keyManager.GetBestHeight());
		Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);
		Assert.Empty(node.RequestedBlocks);
	}

	[Fact]
	public async Task IsNotSynchronizedWhileTheNodeDownloadsTheChainAsync()
	{
		// The node knows about 100 blocks and has 20 of them.
		var node = new FakeBitcoinNode(Network) { Headers = 100 };
		var keyManager = RecoverWallet();
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlocks(9);
		node.MineBlock(payment);
		node.MineBlocks(10);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet, untilSynchronized: false);

		// The wallet has what the node has, but that is not the whole chain yet.
		Assert.Equal(new Height.ChainHeight(20), keyManager.GetBestHeight());
		Assert.Single(wallet.Coins);
		Assert.Equal(new WalletSyncStatus.NodeSynchronizing(20, 100), wallet.Synchronizer.Status);
		Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);

		// The node caught up. The block of the newest header is always on its way for a moment.
		node.MineBlocks(79);
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(99), wallet.Synchronizer.Status);
		Assert.True(wallet.Synchronizer.InitialSynchronizationFinished.IsCompletedSuccessfully);
	}

	[Fact]
	public async Task RefusesANodeOfAnotherNetworkAsync()
	{
		var node = new FakeBitcoinNode(Network.Main);
		var payment = CreatePayment(RecoverWallet().GetNextReceiveKey(), Money.Coins(1));
		node.MineBlock(payment);

		using var wallet = await TestWallet.CreateAsync(node, RecoverWallet());

		var exception = await Assert.ThrowsAnyAsync<Exception>(() => wallet.Synchronizer.SynchronizeAsync(CancellationToken.None));

		Assert.Contains("RegTest", exception.Message);
		Assert.Empty(node.RequestedBlocks);
		Assert.Equal(new Height.ChainHeight(0), wallet.KeyManager.GetBestHeight());
	}

	[Fact]
	public async Task TellsWhenAPrunedNodeDoesNotHaveABlockAnyMoreAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(40);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);
		Assert.Equal(new WalletSyncStatus.Synchronized(40), wallet.Synchronizer.Status);

		// The wallet does not run for a while, and the node prunes blocks that the wallet did not see.
		node.MineBlocks(60);
		node.PrunedBelow = 50;

		var delay = await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Equal(new WalletSyncStatus.BlockPruned(41), wallet.Synchronizer.Status);
		Assert.Contains("pruned", wallet.Synchronizer.Status.ToUserFriendlyString());
		Assert.True(delay > TimeSpan.Zero);
		Assert.Equal(new Height.ChainHeight(40), keyManager.GetBestHeight());
		Assert.Empty(wallet.Coins);

		// A node that has the blocks gets the wallet going again.
		node.PrunedBelow = 0;
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(100), wallet.Synchronizer.Status);
	}

	[Fact]
	public async Task KeepsWhatWasProcessedWhenTheNodeFailsAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(20);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		// Blocks 21 to 25 can be fetched, the rest of them cannot at the moment.
		node.MineBlocks(10);
		node.RpcClient.OnGetBlockAsync = WithFailureAbove(node, node.RpcClient.OnGetBlockAsync!, lastGoodHeight: 25);

		await Assert.ThrowsAsync<HttpRequestException>(() => wallet.Synchronizer.SynchronizeAsync(CancellationToken.None));

		// What was processed until then is saved, so it is not done again.
		Assert.Equal(new Height.ChainHeight(25), keyManager.GetBestHeight());
		Assert.Equal(new Height.ChainHeight(25), KeyManager.FromFile(keyManager.FilePath!).GetBestHeight());
	}

	[Fact]
	public async Task WatchesTheMempoolOfTheNodeAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(5);

		// These are in the mempool before the wallet starts.
		var earlyPayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.AddToMempool(earlyPayment);
		node.AddToMempool(CreateForeignTransaction());
		node.AddToMempool(CreateForeignTransaction());

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		var coin = Assert.Single(wallet.Coins);
		Assert.Equal(earlyPayment.GetHash(), coin.TransactionId);
		Assert.Equal(Height.Mempool, coin.Height);
		Assert.Equal(3, node.RequestedMempoolTransactionCount);

		// Only the transactions of the wallet are kept.
		Assert.Single(wallet.TransactionStore.GetTransactions());

		// A transaction is fetched once, however long it stays in the mempool.
		wallet.Synchronizer.ForceMempoolPoll();
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);
		Assert.Equal(3, node.RequestedMempoolTransactionCount);

		// New arrivals are noticed.
		var latePayment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(2));
		node.AddToMempool(latePayment);
		node.AddToMempool(CreateForeignTransaction());
		wallet.Synchronizer.ForceMempoolPoll();
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Equal(5, node.RequestedMempoolTransactionCount);
		Assert.Equal(2, wallet.Coins.Count());
		Assert.All(wallet.Coins, c => Assert.Equal(Height.Mempool, c.Height));

		// A block confirms one of them.
		node.MineBlock(latePayment);
		await SynchronizeFullyAsync(wallet);

		var coins = wallet.Coins.ToDictionary(c => c.TransactionId);
		Assert.Equal(new Height.ChainHeight(6), coins[latePayment.GetHash()].Height);
		Assert.Equal(Height.Mempool, coins[earlyPayment.GetHash()].Height);
	}

	[Fact]
	public async Task NoticesWhenAnUnconfirmedPaymentIsReplacedAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(5);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		await SynchronizeFullyAsync(wallet);

		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.AddToMempool(payment);
		wallet.Synchronizer.ForceMempoolPoll();
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);
		Assert.Single(wallet.Coins);

		// The sender replaces the payment by a transaction that spends the same coin and pays somebody else.
		// It does not pay to the wallet and it does not spend from it, but it concerns the wallet.
		var replacement = Network.CreateTransaction();
		replacement.Inputs.Add(payment.Inputs[0].PrevOut, Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		replacement.Outputs.Add(Money.Coins(0.9m), CreateForeignScript());
		node.RemoveFromMempool(payment);
		node.AddToMempool(replacement);
		wallet.Synchronizer.ForceMempoolPoll();
		await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

		Assert.Empty(wallet.Coins);
	}

	[Fact]
	public async Task NoticesWhenABlockSpendsWhatAnUnconfirmedPaymentSpendsAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(5);

		using var wallet = await TestWallet.CreateAsync(node, keyManager, watchMempool: false);
		await SynchronizeFullyAsync(wallet);

		// The wallet knows about an unconfirmed payment.
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		wallet.Processor.Process(new SmartTransaction(payment, Height.Mempool));
		Assert.Single(wallet.Coins);

		// A block confirms another transaction that spends the same coin: the payment can never be confirmed.
		var conflict = Network.CreateTransaction();
		conflict.Inputs.Add(payment.Inputs[0].PrevOut, Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		conflict.Outputs.Add(Money.Coins(0.9m), CreateForeignScript());
		node.MineBlock(CreateForeignTransaction(), conflict);
		await SynchronizeFullyAsync(wallet);

		Assert.Empty(wallet.Coins);
	}

	[Fact]
	public async Task DoesNotLookAtTheMempoolWhenToldSoAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(5);
		node.AddToMempool(CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1)));

		using var wallet = await TestWallet.CreateAsync(node, keyManager, watchMempool: false);
		await SynchronizeFullyAsync(wallet);

		Assert.Equal(new WalletSyncStatus.Synchronized(5), wallet.Synchronizer.Status);
		Assert.Empty(wallet.Coins);
		Assert.Equal(0, node.MempoolRequestCount);
	}

	[Fact]
	public async Task TellsWhenTheBlocksAreProcessedBeforeItLooksAtTheMempoolAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlocks(5);
		node.AddToMempool(CreateForeignTransaction());

		var calls = new List<(uint? WalletHeight, int MempoolRequests)>();
		TestWallet? wallet = null;
		wallet = await TestWallet.CreateAsync(
			node,
			keyManager,
			onSynchronizedFirst: () => calls.Add((keyManager.GetBestHeight()?.Height, node.MempoolRequestCount)));

		using (wallet)
		{
			await SynchronizeFullyAsync(wallet);
			node.MineBlock();
			await SynchronizeFullyAsync(wallet);

			// Once, with every block processed and the mempool not asked yet.
			Assert.Equal([(5u, 0)], calls);
			Assert.True(node.MempoolRequestCount > 0);
		}
	}

	[Fact]
	public async Task KeepsTryingInTheBackgroundAsync()
	{
		var node = new FakeBitcoinNode(Network) { Failure = new HttpRequestException("Connection refused") };
		var keyManager = RecoverWallet();
		var payment = CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1));
		node.MineBlocks(3);
		node.MineBlock(payment);

		using var wallet = await TestWallet.CreateAsync(node, keyManager);
		using var testCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

		await wallet.Synchronizer.StartAsync(testCts.Token);

		try
		{
			// The node does not answer.
			await WaitUntilAsync(() => wallet.Synchronizer.Status is WalletSyncStatus.NodeUnavailable, testCts.Token);
			var status = Assert.IsType<WalletSyncStatus.NodeUnavailable>(wallet.Synchronizer.Status);
			Assert.Contains("Connection refused", status.Reason);
			Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);

			// The node is back.
			node.Failure = null;
			await wallet.Synchronizer.InitialSynchronizationFinished.WaitAsync(testCts.Token);

			Assert.Equal(new WalletSyncStatus.Synchronized(4), wallet.Synchronizer.Status);
			Assert.Single(wallet.Coins);
		}
		finally
		{
			await wallet.Synchronizer.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task TellsFailuresOfTheWalletFromFailuresOfTheNodeAsync()
	{
		var node = new FakeBitcoinNode(Network);
		var keyManager = RecoverWallet();
		node.MineBlock(CreatePayment(keyManager.GetNextReceiveKey(), Money.Coins(1)));

		// Something of the wallet fails when the blocks are processed, and it works the next time.
		var attempts = 0;
		using var allowSecondAttempt = new ManualResetEventSlim();
		using var wallet = await TestWallet.CreateAsync(
			node,
			keyManager,
			onSynchronizedFirst: () =>
			{
				if (Interlocked.Increment(ref attempts) == 1)
				{
					throw new InvalidOperationException("The transaction store is broken.");
				}

				allowSecondAttempt.Wait();
			});
		using var testCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

		await wallet.Synchronizer.StartAsync(testCts.Token);

		try
		{
			await WaitUntilAsync(() => Volatile.Read(ref attempts) >= 2, testCts.Token);

			// The node is fine, and it is not blamed.
			Assert.Equal(new WalletSyncStatus.Failed("The transaction store is broken."), wallet.Synchronizer.Status);
			Assert.Contains("The transaction store is broken.", wallet.Synchronizer.Status.ToUserFriendlyString());
			Assert.False(wallet.Synchronizer.InitialSynchronizationFinished.IsCompleted);

			// It keeps trying.
			allowSecondAttempt.Set();
			await wallet.Synchronizer.InitialSynchronizationFinished.WaitAsync(testCts.Token);

			Assert.Equal(new WalletSyncStatus.Synchronized(1), wallet.Synchronizer.Status);
			Assert.Single(wallet.Coins);
		}
		finally
		{
			allowSecondAttempt.Set();
			await wallet.Synchronizer.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>A wallet that looks for its transactions from the first block of the chain on.</summary>
	private static KeyManager RecoverWallet()
	{
		var shares = WalletGenerator.GenerateShares().Take(WalletGenerator.DefaultShamirThreshold).ToArray();
		return KeyManager.Recover(shares, "", Network);
	}

	private static Transaction CreatePayment(HdPubKey key, Money amount)
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(amount, key.GetAssumedScriptPubKey());
		return tx;
	}

	private static Transaction CreateSpending(Transaction funding, uint outputIndex, Script destination)
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(funding, outputIndex), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(funding.Outputs[outputIndex].Value - Money.Satoshis(1_000), destination);
		return tx;
	}

	/// <summary>A transaction that has nothing to do with the wallet.</summary>
	private static Transaction CreateForeignTransaction()
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 1), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(Money.Coins(0.1m), CreateForeignScript());
		tx.Outputs.Add(Money.Coins(0.2m), CreateForeignScript(ScriptPubKeyType.Segwit));
		return tx;
	}

	/// <summary>A script that the wallet has nothing to do with.</summary>
	private static Script CreateForeignScript(ScriptPubKeyType type = ScriptPubKeyType.TaprootBIP86)
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(type);
	}

	/// <summary>Runs the rounds of the synchronizer until there is nothing left to do at the moment.</summary>
	private static async Task SynchronizeFullyAsync(TestWallet wallet, bool untilSynchronized = true)
	{
		for (int i = 0; i < 20; i++)
		{
			wallet.Synchronizer.ForceMempoolPoll();
			var delay = await wallet.Synchronizer.SynchronizeAsync(CancellationToken.None);

			if (delay > TimeSpan.Zero)
			{
				Assert.True(!untilSynchronized || wallet.Synchronizer.Status is WalletSyncStatus.Synchronized, $"Unexpected status: {wallet.Synchronizer.Status}");
				return;
			}
		}

		Assert.Fail("The synchronizer did not come to rest.");
	}

	private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
	{
		while (!condition())
		{
			await Task.Delay(20, cancellationToken);
		}
	}

	/// <summary>Makes the node fail to deliver the blocks above the given height.</summary>
	private static Func<uint256, Task<Block>> WithFailureAbove(FakeBitcoinNode node, Func<uint256, Task<Block>> getBlockAsync, uint lastGoodHeight)
	{
		var goodBlockHashes = Enumerable.Range(0, (int)lastGoodHeight + 1).Select(height => node.GetBlockHash((uint)height)).ToHashSet();

		return blockHash => goodBlockHashes.Contains(blockHash)
			? getBlockAsync(blockHash)
			: Task.FromException<Block>(new HttpRequestException("Connection reset"));
	}

	/// <summary>
	/// What a wallet is made of as far as synchronization goes: its keys, its transactions and the synchronizer.
	/// </summary>
	private sealed class TestWallet : IDisposable
	{
		private readonly List<IDisposable> _subscriptions = [];

		private TestWallet(FakeBitcoinNode node, KeyManager keyManager, AllTransactionStore transactionStore, bool watchMempool, Action? onSynchronizedFirst)
		{
			KeyManager = keyManager;
			TransactionStore = transactionStore;
			EventBus = new EventBus();
			Processor = new TransactionProcessor(transactionStore, keyManager, EventBus);

			_subscriptions.Add(EventBus.Subscribe<WalletSyncStatusChanged>(e => AnnouncedStatuses.Add(e.Status)));
			_subscriptions.Add(EventBus.Subscribe<ClientTipHeightChanged>(e => AnnouncedHeights.Add(e.Height)));

			// As the wallet does when it starts: what it knows already is processed before anything new.
			Processor.Process(transactionStore.ConfirmedStore.GetTransactions());

			Synchronizer = new WalletSynchronizer(node.RpcClient, keyManager, transactionStore, Processor, EventBus, onSynchronizedFirst, watchMempool)
			{
				// Tests do not wait as long as a wallet does.
				IdlePeriod = TimeSpan.FromMilliseconds(50),
				RetryPeriod = TimeSpan.FromMilliseconds(50)
			};
		}

		public KeyManager KeyManager { get; }
		public AllTransactionStore TransactionStore { get; }
		public EventBus EventBus { get; }
		public TransactionProcessor Processor { get; }
		public WalletSynchronizer Synchronizer { get; }
		public List<WalletSyncStatus> AnnouncedStatuses { get; } = [];
		public List<uint> AnnouncedHeights { get; } = [];

		/// <summary>The unspent coins of the wallet.</summary>
		public CoinsRegistry Coins => Processor.Coins;

		/// <param name="keyManager">A wallet that has a wallet file already is started again: it finds its transactions where it left them.</param>
		public static async Task<TestWallet> CreateAsync(
			FakeBitcoinNode node,
			KeyManager keyManager,
			bool watchMempool = true,
			Action? onSynchronizedFirst = null,
			[CallerMemberName] string callerMemberName = "")
		{
			if (keyManager.FilePath is null)
			{
				var workDir = await Common.GetEmptyWorkDirAsync(callerMemberName: callerMemberName);
				keyManager.SetFilePath(Path.Combine(workDir, "Wallet.json"));
				keyManager.ToFile();
			}

			var transactionStore = new AllTransactionStore(Path.Combine(Path.GetDirectoryName(keyManager.FilePath)!, "Transactions"), Network);
			await transactionStore.InitializeAsync();
			return new TestWallet(node, keyManager, transactionStore, watchMempool, onSynchronizedFirst);
		}

		public void Dispose()
		{
			foreach (var subscription in _subscriptions)
			{
				subscription.Dispose();
			}

			Synchronizer.Dispose();
			TransactionStore.Dispose();
		}
	}
}
