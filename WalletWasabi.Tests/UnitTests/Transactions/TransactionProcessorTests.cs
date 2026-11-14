using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Extensions;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionProcessor"/>.
/// </summary>
public class TransactionProcessorTests
{
	[Fact]
	public async Task TransactionDoesNotContainCoinsForTheWalletAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		// This transaction doesn't have any coin for the wallet. It is not relevant.
		var tx = CreateCreditingTransaction(BitcoinFactory.CreateScript(), Money.Coins(1.0m));

		var relevant = transactionProcessor.Process(tx);

		Assert.False(relevant.IsNews);
		Assert.Empty(transactionProcessor.Coins);
		Assert.True(transactionProcessor.TransactionStore.MempoolStore.IsEmpty());
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
	}

	[Fact]
	public async Task SameWalletLoadedTwiceDoesNotLeakDefaultAnonScoreAsync()
	{
		// Reproduces #7453 / #9843 / #12648. The same wallet loaded twice shares one transaction
		// store, so it shares each SmartTransaction. WalletOutputs is a set keyed by outpoint
		// (SmartCoin equality is outpoint-only), so only the first instance's coin is kept there and
		// gets analyzed. The second instance's HdPubKey for the very same output is never analyzed and
		// would keep the DefaultHighAnonymitySet sentinel (int.MaxValue), wrongly marking the coin
		// fully private and excluding it from coinjoins.
		using var txStore = await CreateTransactionStoreAsync();

		var km1 = KeyManager.CreateNew(out var shares, "pw", Network.Main);
		var km2 = KeyManager.Recover(shares[..WalletGenerator.DefaultShamirThreshold], "pw", Network.Main);

		var eventBus = new EventBus();
		var tp1 = new TransactionProcessor(txStore, null, km1, eventBus);
		var tp2 = new TransactionProcessor(txStore, null, km2, eventBus);

		// A receive to a key both instances of the wallet control (identical derivation).
		var receiveKey = km1.GetNextReceiveKey();
		var tx = CreateCreditingTransaction(receiveKey.P2Taproot, Money.Coins(1.0m), height: 100);

		// The first instance processes it: its coin becomes the analyzed representative (anonset 1).
		tp1.Process(tx);
		// The second instance processes the very same shared transaction.
		tp2.Process(tx);

		var coin1 = Assert.Single(tp1.Coins);
		var coin2 = Assert.Single(tp2.Coins);
		Assert.Equal(1, coin1.AnonymitySet);
		Assert.NotEqual(HdPubKey.DefaultHighAnonymitySet, coin2.AnonymitySet);
		Assert.Equal(1, coin2.AnonymitySet);
	}

	[Fact]
	public async Task SpendToLegacyScriptsAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keys = transactionProcessor.KeyManager.GetKeys();

		// A payment to a key under our control but using P2PKH script (legacy)
		var tx = CreateCreditingTransaction(keys.First().PubKey.GetScriptPubKey(ScriptPubKeyType.Legacy), Money.Coins(1.0m));
		var relevant = transactionProcessor.Process(tx);

		Assert.False(relevant.IsNews);
		Assert.Empty(transactionProcessor.Coins);
		Assert.True(transactionProcessor.TransactionStore.MempoolStore.IsEmpty());
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
	}

	[Fact]
	public async Task UnconfirmedTransactionIsNotSegWitAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		// No segwit transaction. Ignore it.
		using Key key = new();
		var tx = CreateCreditingTransaction(key.PubKey.Hash.ScriptPubKey, Money.Coins(1.0m));

		var relevant = transactionProcessor.Process(tx);

		Assert.False(relevant.IsNews);
		Assert.Empty(transactionProcessor.Coins);
		Assert.True(transactionProcessor.TransactionStore.MempoolStore.IsEmpty());
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
	}

	[Fact]
	public async Task ConfirmedTransactionIsNotSegWitAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		// No segwit transaction. Ignore it.
		using Key key = new();
		var tx = CreateCreditingTransaction(key.PubKey.Hash.ScriptPubKey, Money.Coins(1.0m), height: 54321);

		var relevant = transactionProcessor.Process(tx);

		Assert.False(relevant.IsNews);
		Assert.Empty(transactionProcessor.Coins);
		Assert.True(transactionProcessor.TransactionStore.MempoolStore.IsEmpty());
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
	}

	[Fact]
	public async Task ProcessResultAfterConfirmationCorrectAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		// An unconfirmed segwit transaction for us
		var hdPubKey = transactionProcessor.KeyManager.GetKeys().First();

		var tx1 = CreateCreditingTransaction(hdPubKey.P2Taproot, Money.Coins(1.0m));
		var blockHeight = new Height.ChainHeight.ChainHeight(77551);
		var tx2 = new SmartTransaction(tx1.Transaction, blockHeight);
		var coinToSpend = tx2.Transaction.Outputs.AsCoins().First();
		var tx3 = CreateSpendingTransaction(coinToSpend, BitcoinFactory.CreateScript(), keyManager: transactionProcessor.KeyManager);
		var blockHeight2 = new Height.ChainHeight.ChainHeight(77552);
		var tx4 = new SmartTransaction(tx3.Transaction, blockHeight2);
		var results = transactionProcessor.Process(tx1, tx2, tx3, tx4).ToArray();
		var res1 = results[0];
		var res2 = results[1];
		var res3 = results[2];
		var res4 = results[3];

		Assert.False(res1.IsOwnCoinJoin);
		Assert.Empty(res1.NewlyConfirmedReceivedCoins);
		Assert.Empty(res1.NewlyConfirmedSpentCoins);
		Assert.Single(res1.NewlyReceivedCoins);
		Assert.Empty(res1.NewlySpentCoins);
		Assert.Single(res1.ReceivedCoins);
		Assert.Empty(res1.SpentCoins);
		Assert.Empty(res1.ReplacedCoins);
		Assert.Empty(res1.RestoredCoins);
		Assert.Empty(res1.SuccessfullyDoubleSpentCoins);
		Assert.True(res1.IsNews);
		Assert.NotNull(res1.Transaction);

		Assert.False(res2.IsOwnCoinJoin);
		Assert.Single(res2.NewlyConfirmedReceivedCoins);
		Assert.Empty(res2.NewlyConfirmedSpentCoins);
		Assert.Empty(res2.NewlyReceivedCoins);
		Assert.Empty(res2.NewlySpentCoins);
		Assert.Single(res2.ReceivedCoins);
		Assert.Empty(res2.SpentCoins);
		Assert.Empty(res2.ReplacedCoins);
		Assert.Empty(res2.RestoredCoins);
		Assert.Empty(res2.SuccessfullyDoubleSpentCoins);
		Assert.True(res2.IsNews);
		Assert.NotNull(res2.Transaction);

		Assert.False(res3.IsOwnCoinJoin);
		Assert.Empty(res3.NewlyConfirmedReceivedCoins);
		Assert.Empty(res3.NewlyConfirmedSpentCoins);
		Assert.Empty(res3.NewlyReceivedCoins);
		Assert.Single(res3.NewlySpentCoins);
		Assert.Empty(res3.ReceivedCoins);
		Assert.Single(res3.SpentCoins);
		Assert.Empty(res3.ReplacedCoins);
		Assert.Empty(res3.RestoredCoins);
		Assert.Empty(res3.SuccessfullyDoubleSpentCoins);
		Assert.True(res3.IsNews);
		Assert.NotNull(res3.Transaction);

		Assert.False(res4.IsOwnCoinJoin);
		Assert.Empty(res4.NewlyConfirmedReceivedCoins);
		Assert.Single(res4.NewlyConfirmedSpentCoins);
		Assert.Empty(res4.NewlyReceivedCoins);
		Assert.Single(res4.NewlySpentCoins);
		Assert.Empty(res4.ReceivedCoins);
		Assert.Single(res4.SpentCoins);
		Assert.Empty(res4.ReplacedCoins);
		Assert.Empty(res4.RestoredCoins);
		Assert.Empty(res4.SuccessfullyDoubleSpentCoins);
		Assert.True(res4.IsNews);
		Assert.NotNull(res4.Transaction);
	}

	[Fact]
	public async Task UpdateTransactionHeightAfterConfirmationAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		var hdPubKey = transactionProcessor.KeyManager.GetKeys().First();

		// An unconfirmed segwit transaction for us
		var tx = CreateCreditingTransaction(hdPubKey.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx);

		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
		var cachedTx = Assert.Single(transactionProcessor.TransactionStore.MempoolStore.GetTransactions());
		var coin = Assert.Single(transactionProcessor.Coins);
		Assert.Equal(Height.Mempool, cachedTx.Height);
		Assert.Equal(Height.Mempool, coin.Height);

		// Now it is confirmed
		var blockHeight = new Height.ChainHeight(77551);
		tx = new SmartTransaction(tx.Transaction, blockHeight);
		var relevant = transactionProcessor.Process(tx);

		Assert.True(relevant.IsNews);
		Assert.Single(transactionProcessor.Coins);

		// Transaction store assertions
		Assert.True(transactionProcessor.TransactionStore.MempoolStore.IsEmpty());
		cachedTx = Assert.Single(transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions());
		Assert.Equal(blockHeight, cachedTx.Height);
		coin = Assert.Single(transactionProcessor.Coins);
		Assert.Equal(blockHeight, coin.Height);
		Assert.True(coin.Confirmed);
	}

	/// <summary>
	/// Make sure that coins with <see cref="HdPubKey"/>s are tracked and that we track latest spending heights for <see cref="HdPubKey"/>s as well.
	/// </summary>
	[Fact]
	public async Task RememberLatestSpendingHeightAsync()
	{
		// --tx0---> (A) ----tx1----> (pay to B)

		using AllTransactionStore txStore = await CreateTransactionStoreAsync();
		TransactionProcessor transactionProcessor = CreateTransactionProcessor(txStore);
		HdPubKey hdPubKey = transactionProcessor.NewKey();

		Assert.False(transactionProcessor.Coins.HasUnspentCoin(hdPubKey));

		SmartTransaction tx0 = CreateCreditingTransaction(hdPubKey.P2Taproot, Money.Coins(1.0m), height: 54321);
		transactionProcessor.Process(tx0);

		Assert.True(transactionProcessor.Coins.HasUnspentCoin(hdPubKey));

		SmartCoin coinA = Assert.Single(transactionProcessor.Coins);
		Script changeScript = transactionProcessor.NewKey().P2Taproot;

		using Key key = new();
		SmartTransaction tx1 = CreateSpendingTransaction(new[] { coinA.Coin }, key.PubKey.ScriptPubKey, changeScript, height: 55555);
		transactionProcessor.Process(tx1);

		SmartCoin changeCoinB = Assert.Single(transactionProcessor.Coins);
		Assert.False(transactionProcessor.Coins.HasUnspentCoin(hdPubKey));
	}

	[Fact]
	public async Task IgnoreDoubleSpendAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		var keys = transactionProcessor.KeyManager.GetKeys();

		// An unconfirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keys[0].GetAssumedScriptPubKey(), Money.Coins(1.0m));

		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin (signed to pass signature check)
		var tx1 = CreateSpendingTransaction(createdCoin, keys[1].GetAssumedScriptPubKey(), keyManager: transactionProcessor.KeyManager);

		// Spend the same coin again (signed to pass signature check)
		var tx2 = CreateSpendingTransaction(createdCoin, keys[2].GetAssumedScriptPubKey(), keyManager: transactionProcessor.KeyManager);
		var relevant = transactionProcessor.Process(tx0, tx1, tx2).Last();

		Assert.True(relevant.IsNews);
		Assert.Single(transactionProcessor.Coins, coin => !coin.IsSpent());
		Assert.Single(transactionProcessor.Coins.AsAllCoinsView(), coin => coin.IsSpent());

		// Transaction store assertions
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Equal(2, mempool.Count);
		Assert.Equal(tx0, mempool.First());
		Assert.Equal(tx2, mempool.Last());
	}

	[Fact]
	public async Task ProcessSameTransactionTwiceAsync()
	{
		using var txStore = await CreateTransactionStoreAsync(nameof(ProcessSameTransactionTwiceAsync));
		var transactionProcessor = CreateTransactionProcessor(txStore);

		var keys = transactionProcessor.KeyManager.GetKeys();

		// An unconfirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keys[0].GetAssumedScriptPubKey(), Money.Coins(1.0m));

		// Spend the same coin again
		var relevant = transactionProcessor.Process(tx0, tx0).ToArray();

		Assert.Equal(2, relevant.Length);
		Assert.True(relevant[0].IsNews);
		Assert.False(relevant[1].IsNews);
		Assert.Single(transactionProcessor.Coins, coin => !coin.IsSpent());

		// Transaction store assertions
		Assert.True(transactionProcessor.TransactionStore.ConfirmedStore.IsEmpty());
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Single(mempool);
		Assert.Equal(tx0, mempool.First());
	}

	[Fact]
	public async Task ConfirmedDoubleSpendAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);

		var keys = transactionProcessor.KeyManager.GetKeys();

		int doubleSpendReceived = 0;
		using var _ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e =>
		{
			var doubleSpentCoins = e.Result.SuccessfullyDoubleSpentCoins;
			if (doubleSpentCoins.Count != 0)
			{
				var coin = Assert.Single(doubleSpentCoins);

				// Double spend to ourselves but to a different address. So checking the address.
				Assert.Equal(keys[1].GetAssumedScriptPubKey(), coin.ScriptPubKey);

				doubleSpendReceived++;
			}
		});

		int coinReceivedCalled = 0;

		// The coin with the confirmed tx should win.
		using var __ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e =>
		{
			foreach (var c in e.Result.NewlyReceivedCoins)
			{
				switch (coinReceivedCalled)
				{
					case 0: Assert.Equal(keys[0].GetAssumedScriptPubKey(), c.ScriptPubKey); break;
					case 1: Assert.Equal(keys[1].GetAssumedScriptPubKey(), c.ScriptPubKey); break;
					case 2: Assert.Equal(keys[2].GetAssumedScriptPubKey(), c.ScriptPubKey); break;
					default:
						throw new InvalidOperationException();
				}

				coinReceivedCalled++;
			}
		});

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keys[0].GetAssumedScriptPubKey(), Money.Coins(1.0m), height: 54321);

		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin
		var tx1 = CreateSpendingTransaction(createdCoin, keys[1].GetAssumedScriptPubKey(), height: 54321);

		Assert.Equal(0, doubleSpendReceived);

		// Spend the coin
		var tx2 = CreateSpendingTransaction(createdCoin, keys[2].GetAssumedScriptPubKey(), height: 54321);
		var relevant = transactionProcessor.Process(tx0, tx1, tx2).Last();
		Assert.Equal(1, doubleSpendReceived);

		Assert.True(relevant.IsNews);
		Assert.Single(transactionProcessor.Coins, coin => !coin.IsSpent() && coin.Confirmed);
		Assert.Single(transactionProcessor.Coins.AsAllCoinsView(), coin => coin.IsSpent() && coin.Confirmed);

		// Transaction store assertions
		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions();
		Assert.Equal(tx0, matureTxs.First());
		Assert.Equal(tx2, matureTxs.Last());

		// Unconfirmed transaction must be removed from the mempool because there is confirmed tx now
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Empty(mempool);
	}

	[Fact]
	public async Task HandlesRbfAsync()
	{
		// --tx0---> (A) --tx1 (replaceable)-+--> (B) --tx2---> (D)
		//                  |                |
		//                  |                +--> (C)
		//                  |
		//                  +--tx3 (replacement)---> (E)
		//

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);
		var keyA = transactionProcessor.NewKey();
		var keyB = transactionProcessor.NewKey();
		var keyC = transactionProcessor.NewKey();
		var keyD = transactionProcessor.NewKey();
		var keyE = transactionProcessor.NewKey();

		int replaceTransactionReceivedCalled = 0;
		using var __ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(x =>
		{
			var e = x.Result;
			if (e.ReplacedCoins.Count != 0 || e.RestoredCoins.Count != 0)
			{
				if (e.RestoredCoins.Count != 0)
				{
					// Move the original coin from spent to unspent - so add.
					var originalCoin = Assert.Single(e.RestoredCoins);
					Assert.Equal(Money.Coins(1.0m), originalCoin.Amount);
				}

				// Remove the created coin by the transaction.
				Assert.Equal(3, e.ReplacedCoins.Count);
				Assert.Single(e.ReplacedCoins, coin => coin.HdPubKey == keyB);
				Assert.Single(e.ReplacedCoins, coin => coin.HdPubKey == keyC);
				Assert.Single(e.ReplacedCoins, coin => coin.HdPubKey == keyD);

				replaceTransactionReceivedCalled++;
			}
		});

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m), height: 54321);

		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin
		var tx1 = CreateSpendingTransaction(createdCoin, keyB.P2Taproot);
		tx1.Transaction.Inputs[0].Sequence = Sequence.OptInRBF;
		tx1.Transaction.Outputs[0].Value = Money.Coins(0.95m);
		tx1.Transaction.Outputs.Add(Money.Coins(0.1m), keyC.P2Taproot);
		SignSmartTransaction(tx1, [createdCoin], transactionProcessor.KeyManager);
		var relevant1 = transactionProcessor.Process(tx0, tx1).Last();
		Assert.True(relevant1.IsNews);
		Assert.Equal(0, replaceTransactionReceivedCalled);

		var unconfirmedCoin1 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyB);
		var unconfirmedCoin2 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyC);
		Assert.True(unconfirmedCoin1.Transaction.IsRBF());
		Assert.True(unconfirmedCoin2.Transaction.IsRBF());

		// Spend the received coin
		var tx2 = CreateSpendingTransaction(unconfirmedCoin1.Coin, keyD.P2Taproot);
		tx2.Transaction.Outputs[0].Value = Money.Coins(0.7m);
		SignSmartTransaction(tx2, [unconfirmedCoin1.Coin], transactionProcessor.KeyManager);
		var relevant2 = transactionProcessor.Process(tx2);
		Assert.True(relevant2.IsNews);
		Assert.Equal(0, replaceTransactionReceivedCalled);

		// Spend the coin
		var tx3 = CreateSpendingTransaction(createdCoin, keyE.P2Taproot);
		tx3.Transaction.Outputs[0].Value = Money.Coins(0.9m);
		SignSmartTransaction(tx3, [createdCoin], transactionProcessor.KeyManager);
		var relevant3 = transactionProcessor.Process(tx3);

		Assert.True(relevant3.IsNews);
		Assert.Equal(1, replaceTransactionReceivedCalled);
		var finalCoin = Assert.Single(transactionProcessor.Coins);
		Assert.True(finalCoin.Transaction.IsRBF());
		Assert.Equal(keyE, finalCoin.HdPubKey);

		Assert.DoesNotContain(unconfirmedCoin1, transactionProcessor.Coins.AsAllCoinsView());

		// Transaction store assertions
		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions();
		Assert.Equal(tx0, matureTxs.First());

		// All the replaced transactions tx1 and tx2 have to be removed because tx4 replaced tx1
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		var txInMempool = Assert.Single(mempool);
		Assert.Equal(tx3, txInMempool);
	}

	[Fact]
	public async Task HandlesConfirmedReplacedTransactionAsync()
	{
		// --tx0---> (A) --tx1 (replaceable)-+--> (B) --tx2---> (D)
		//                  |                |
		//                  |                +--> (C)
		//                  |
		//                  +--tx3 (replacement)---> (E)   { after this tx1 is confirmed }
		//
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var keyB = transactionProcessor.NewKey();
		var keyC = transactionProcessor.NewKey();
		var keyD = transactionProcessor.NewKey();
		var keyE = transactionProcessor.NewKey();

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m), height: 54321);
		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();
		transactionProcessor.Process(tx0);

		// Spend the received coin
		var tx1 = CreateSpendingTransaction(createdCoin, keyB.P2Taproot);
		tx1.Transaction.Inputs[0].Sequence = Sequence.OptInRBF;
		tx1.Transaction.Outputs[0].Value = Money.Coins(0.95m);
		tx1.Transaction.Outputs.Add(Money.Coins(0.1m), keyC.P2Taproot);
		SignSmartTransaction(tx1, [createdCoin], transactionProcessor.KeyManager);
		transactionProcessor.Process(tx1);

		var unconfirmedCoin1 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyB);
		var unconfirmedCoin2 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyC);

		// Spend the received coin
		var tx2 = CreateSpendingTransaction(unconfirmedCoin1.Coin, keyD.P2Taproot);
		tx2.Transaction.Outputs[0].Value = Money.Coins(0.7m);
		SignSmartTransaction(tx2, [unconfirmedCoin1.Coin], transactionProcessor.KeyManager);
		transactionProcessor.Process(tx2);

		// Spend the replaceable coin
		var tx3 = CreateSpendingTransaction(createdCoin, keyE.P2Taproot);
		tx3.Transaction.Outputs[0].Value = Money.Coins(0.9m);
		SignSmartTransaction(tx3, [createdCoin], transactionProcessor.KeyManager);
		transactionProcessor.Process(tx3);

		// Now it is confirmed
		var blockHeight = new Height.ChainHeight(77551);
		tx1 = new SmartTransaction(tx1.Transaction, blockHeight);
		var relevant = transactionProcessor.Process(tx1);

		var coin1 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyB);
		var coin2 = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyC);

		Assert.True(coin1.Confirmed);
		Assert.True(coin2.Confirmed);

		Assert.DoesNotContain(transactionProcessor.Coins, coin => coin.HdPubKey == keyE);
		Assert.DoesNotContain(transactionProcessor.Coins, coin => coin.HdPubKey == keyD); // Wasabi forgot about it but that's not a problem.

		// Replacement transaction tx3 has to be removed because tx1 confirmed and then it is invalid.
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.DoesNotContain(tx3, mempool);
	}

	[Fact]
	public async Task RecognizeReplaceableCoinsCorrectlyAsync()
	{
		// --tx0 ---> (A) -(replaceable)--tx1 -+--> (B) --tx2---> (D)
		//                                     |
		//                                     +--> (C)
		//
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var keyB = transactionProcessor.NewKey();
		var keyC = transactionProcessor.NewKey();
		var keyD = transactionProcessor.NewKey();

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();
		transactionProcessor.Process(tx0);

		// Spend the received coin
		var tx1 = CreateSpendingTransaction(createdCoin, keyB.P2Taproot);
		tx1.Transaction.Outputs[0].Value = Money.Coins(0.95m);
		tx1.Transaction.Inputs[0].Sequence = Sequence.OptInRBF;
		tx1.Transaction.Outputs.Add(Money.Coins(0.1m), keyC.P2Taproot);
		SignSmartTransaction(tx1, [createdCoin], transactionProcessor.KeyManager);
		transactionProcessor.Process(tx1);

		var coinB = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyB);
		var coinC = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyC);

		// Spend the received coin
		var tx2 = CreateSpendingTransaction(coinB.Coin, keyD.P2Taproot);
		tx2.Transaction.Outputs[0].Value = Money.Coins(0.7m);
		SignSmartTransaction(tx2, [coinB.Coin], transactionProcessor.KeyManager);
		transactionProcessor.Process(tx2);

		var coinD = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyD);

		Assert.True(coinB.Transaction.IsRBF());
		Assert.True(coinC.Transaction.IsRBF());
		Assert.True(coinD.Transaction.IsRBF());

		// Now it is confirmed
		var blockHeight = new Height.ChainHeight(77551);
		tx1 = new SmartTransaction(tx1.Transaction, blockHeight);
		var relevant = transactionProcessor.Process(tx1);

		coinC = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyC);
		coinD = Assert.Single(transactionProcessor.Coins, coin => coin.HdPubKey == keyD);

		Assert.False(coinC.Transaction.IsRBF());
		Assert.True(coinD.Transaction.IsRBF());
	}

	[Fact]
	public async Task ConfirmTransactionTestAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);

		var keys = transactionProcessor.KeyManager.GetKeys();
		int confirmed = 0;
		using var __ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(x =>
		{
			var e = x.Result;
			if (e.NewlySpentCoins.Count != 0)
			{
				throw new InvalidOperationException("We are not spending the coin.");
			}
			else if (e.NewlyConfirmedSpentCoins.Count != 0)
			{
				confirmed++;
			}
		});

		// A confirmed segwit transaction for us
		var tx1 = CreateCreditingTransaction(keys[0].P2Taproot, Money.Coins(1.0m));
		var results = transactionProcessor.Process(tx1, tx1).ToArray();
		var res1 = results[0];
		var res2a = results[1];

		// Process it again.
		var res2b = transactionProcessor.Process(tx1);

		Assert.True(res1.IsNews);
		Assert.Single(res1.NewlyReceivedCoins);
		Assert.Single(res1.ReceivedCoins);
		Assert.Empty(res1.NewlyConfirmedReceivedCoins);

		foreach (var res2 in new[] { res2a, res2b })
		{
			Assert.False(res2.IsNews);
			Assert.Empty(res2.ReplacedCoins);
			Assert.Empty(res2.RestoredCoins);
			Assert.Empty(res2.SuccessfullyDoubleSpentCoins);
			Assert.Single(res2.ReceivedCoins);
			Assert.Empty(res2.NewlyConfirmedReceivedCoins);
		}

		var coin = Assert.Single(transactionProcessor.Coins);
		Assert.False(coin.Confirmed);

		var tx2 = new SmartTransaction(tx1.Transaction.Clone(), new Height.ChainHeight(54321));

		Assert.Equal(tx1.GetHash(), tx2.GetHash());
		var res3 = transactionProcessor.Process(tx2);
		Assert.True(res3.IsNews);
		Assert.Empty(res3.ReplacedCoins);
		Assert.Empty(res3.RestoredCoins);
		Assert.Empty(res3.SuccessfullyDoubleSpentCoins);
		Assert.Single(res3.ReceivedCoins);
		Assert.Single(res3.NewlyConfirmedReceivedCoins);
		Assert.True(coin.Confirmed);

		Assert.Equal(0, confirmed);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Empty(mempool);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions();
		var confirmedTx = Assert.Single(matureTxs);
		Assert.Equal(tx1, confirmedTx);
		Assert.Equal(tx2, confirmedTx);
	}

	[Fact]
	public async Task HandlesBumpFeeAsync()
	{
		// --tx0---> (A) --+
		//                 +--+-- tx2 (replaceable --+---> (myself)
		// --tx1---> (B) --+  |                      |
		//                    |                      +---> (change myself)
		//                    |
		//                    +--- tx4 (replaces tx2)
		//                    |
		// --tx3---> (C) -----+

		// Replaces a previous RBF transaction by a new one that contains one more input (higher fee)

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		Script NewScript() => transactionProcessor.NewKey().P2Taproot;

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(NewScript(), Money.Coins(1.0m), height: 54321);

		// Another confirmed segwit transaction for us
		var tx1 = CreateCreditingTransaction(NewScript(), Money.Coins(1.0m), height: 54321);
		transactionProcessor.Process(tx0, tx1);

		var createdCoins = transactionProcessor.Coins.Select(x => x.Coin).ToArray(); // 2 coins of 1.0 btc each

		// Spend the received coins
		var destinationScript = NewScript();
		var changeScript = NewScript();
		var tx2 = CreateSpendingTransaction(createdCoins, destinationScript, changeScript); // spends 1.2btc
		tx2.Transaction.Inputs[0].Sequence = Sequence.OptInRBF;
		SignSmartTransaction(tx2, createdCoins, transactionProcessor.KeyManager);

		// Another confirmed segwit transaction for us
		var tx3 = CreateCreditingTransaction(NewScript(), Money.Coins(1.0m), height: 54322);

		var relevant1 = transactionProcessor.Process(tx2, tx3).First();
		Assert.True(relevant1.IsNews);

		// At this moment we have one 1.2btc and one 0.8btc replaceable coins and one 1.0btc final coin
		var latestCreatedCoin = Assert.Single(transactionProcessor.Coins.CreatedBy(tx3.Transaction.GetHash()));
		var coinsToSpend = createdCoins.Concat(new[] { latestCreatedCoin.Coin }).ToArray();

		// Spend them again with a different amount
		var destinationScript2 = BitcoinFactory.CreateScript(); // spend to someone else
		var tx4 = CreateSpendingTransaction(coinsToSpend, destinationScript2, changeScript, keyManager: transactionProcessor.KeyManager);
		var relevant2 = transactionProcessor.Process(tx4);

		Assert.True(relevant2.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins);
		Assert.True(coin.Transaction.IsRBF());

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		var inMempoolTx = Assert.Single(mempool);
		Assert.Equal(tx4, inMempoolTx);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Equal(3, matureTxs.Length);
		Assert.Equal(tx0, matureTxs[0]);
		Assert.Equal(tx1, matureTxs[1]);
		Assert.Equal(tx3, matureTxs[2]);
	}

	[Fact]
	public async Task HandlesRbfWithLessCoinsAsync()
	{
		// --tx0---> (A) --+--+
		//                 +--|-- tx2 (replaceable --+---> (myself)
		// --tx1---> (B) --+  |                      |
		//                    |                      +---> (change myself)
		//                    |
		//                    +--- tx3 (replaces tx2)

		// Replaces a previous RBF transaction by a new one that contains one less input (higher fee)

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		Script NewScript() => transactionProcessor.NewKey().P2Taproot;

		// A confirmed segwit transaction for us
		var tx0 = CreateCreditingTransaction(NewScript(), Money.Coins(1.0m), height: 54321);
		transactionProcessor.Process(tx0);

		// Another confirmed segwit transaction for us
		var tx1 = CreateCreditingTransaction(NewScript(), Money.Coins(1.0m), height: 54321);
		transactionProcessor.Process(tx1);

		var createdCoins = transactionProcessor.Coins.Select(x => x.Coin).ToArray(); // 2 coins of 1.0 btc each

		// Spend the received coins (replaceable)
		var destinationScript = NewScript();
		var changeScript = NewScript();
		var tx2 = CreateSpendingTransaction(createdCoins, destinationScript, changeScript); // spends 1.2btc
		tx2.Transaction.Inputs[0].Sequence = Sequence.OptInRBF;
		SignSmartTransaction(tx2, createdCoins, transactionProcessor.KeyManager);
		var relevant = transactionProcessor.Process(tx2);
		Assert.True(relevant.IsNews);

		// replace previous tx with another one spending only one coin
		destinationScript = BitcoinFactory.CreateScript(); // spend to someone else
		var tx3 = CreateSpendingTransaction(createdCoins.Take(1), destinationScript, changeScript, keyManager: transactionProcessor.KeyManager); // spends 0.6btc
		relevant = transactionProcessor.Process(tx3);

		Assert.True(relevant.IsNews);
		var replaceableCoin = Assert.Single(transactionProcessor.Coins, c => c.Transaction.IsRBF());
		Assert.Equal(tx3.Transaction.GetHash(), replaceableCoin.TransactionId);

		var nonReplaceableCoin = Assert.Single(transactionProcessor.Coins, c => !c.Transaction.IsRBF());
		Assert.Equal(tx1.Transaction.GetHash(), nonReplaceableCoin.TransactionId);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		var inMempoolTx = Assert.Single(mempool);
		Assert.Equal(tx3, inMempoolTx);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Equal(2, matureTxs.Length);
		Assert.Equal(tx0, matureTxs[0]);
		Assert.Equal(tx1, matureTxs[1]);
	}

	[Fact]
	public async Task ReceiveTransactionForWalletAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);
		SmartCoin? receivedCoin = null;

		using var __ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e =>
		receivedCoin = e.Result.NewlyReceivedCoins.Single());
		var keys = transactionProcessor.KeyManager.GetKeys();
		var tx = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(1.0m));

		var relevant = transactionProcessor.Process(tx);

		// It is relevant because is funding the wallet
		Assert.True(relevant.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins);
		Assert.Equal(Money.Coins(1.0m), coin.Amount);
		Assert.NotNull(receivedCoin);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		var inMempoolTx = Assert.Single(mempool);
		Assert.Equal(tx, inMempoolTx);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Empty(matureTxs);
	}

	[Fact]
	public async Task SpendCoinAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);
		SmartCoin? spentCoin = null;

		using var _ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e =>
		{
			if (e.Result.NewlySpentCoins.Count != 0)
			{
				spentCoin = e.Result.NewlySpentCoins.Single();
			}
		});
		var keys = transactionProcessor.KeyManager.GetKeys();
		var tx0 = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin
		using Key key = new();
		var tx1 = CreateSpendingTransaction(createdCoin, key.PubKey.ScriptPubKey, height: 1);
		var relevant = transactionProcessor.Process(tx1);

		Assert.True(relevant.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins.AsAllCoinsView());
		Assert.True(coin.IsSpent());
		Assert.NotNull(spentCoin);
		Assert.Equal(coin, spentCoin);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Single(mempool); // only tx0 in mempool

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Single(matureTxs); // tx1 is confirmed
	}

	[Fact]
	public async Task CorrectSpenderAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore, out var eventBus);
		SmartCoin? spentCoin = null;
		using var _ = eventBus.Subscribe<WalletRelevantTransactionProcessed>(e =>
		{
			if (e.Result.NewlySpentCoins.Count != 0)
			{
				spentCoin = e.Result.NewlySpentCoins.Single();
			}
		});
		var keys = transactionProcessor.KeyManager.GetKeys();
		var tx0 = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin
		using Key key = new();
		var tx1 = CreateSpendingTransaction(createdCoin, key.PubKey.ScriptPubKey, keyManager: transactionProcessor.KeyManager);

		transactionProcessor.Process(tx1);

		var tx2 = new SmartTransaction(tx1.Transaction, tx1.Height, tx1.BlockHash, tx1.BlockIndex, tx1.IsReplacement, tx1.IsSpeedup, tx1.IsCancellation, tx1.FirstSeen);
		var relevant = transactionProcessor.Process(tx2);

		Assert.False(relevant.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins.AsAllCoinsView());
		Assert.True(coin.IsSpent());
		Assert.NotNull(spentCoin);
		Assert.Equal(coin, spentCoin);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Equal(2, mempool.Count);
		Assert.Contains(tx0, mempool);
		Assert.Contains(tx1, mempool);
		Assert.Contains(tx2, mempool);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Empty(matureTxs);

		Assert.Contains(spentCoin, tx1.WalletInputs);
	}

	[Fact]
	public async Task CorrectCoinReferenceAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var tx0 = CreateCreditingTransaction(transactionProcessor.KeyManager.GetKeys().First().P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);
		var createdCoin = tx0.Transaction.Outputs.AsCoins().First();

		// Spend the received coin
		var tx1 = CreateSpendingTransaction(createdCoin, BitcoinFactory.CreateScript(), keyManager: transactionProcessor.KeyManager);

		// Add the transaction to the tx store manually and don't process it.
		transactionProcessor.TransactionStore.AddOrUpdate(tx1);

		// The same transaction, seen earlier.
		var earlierFirstSeen = tx1.FirstSeen - TimeSpan.FromHours(1);
		var tx2 = new SmartTransaction(tx1.Transaction, tx1.Height, tx1.BlockHash, tx1.BlockIndex, tx1.IsReplacement, tx1.IsSpeedup, tx1.IsCancellation, earlierFirstSeen);
		transactionProcessor.Process(tx2);

		// Ensure even if only tx2 was processed, the reference of the registered spender is to tx1
		// and that the data of tx2 was merged into it.
		var txid = tx1.GetHash();
		SmartTransaction? registeredSpender = transactionProcessor.Coins.AsAllCoinsView().SpentBy(txid).Single().SpenderTransaction;
		Assert.NotNull(registeredSpender);

		Assert.Same(tx1, registeredSpender);
		Assert.NotSame(tx2, registeredSpender);
		Assert.Equal(earlierFirstSeen, registeredSpender.FirstSeen);
	}

	/// <summary>
	/// There is no dust attack limit: tiny payments to an already used address are tracked like any other coin.
	/// </summary>
	[Fact]
	public async Task ReceiveSmallPaymentsToUsedAddressAsync()
	{
		static void AssertCoin(ProcessedResult result)
		{
			Assert.NotEmpty(result.ReceivedCoins);
			Assert.NotEmpty(result.NewlyReceivedCoins);
			Assert.True(result.IsNews);
			Assert.Empty(result.NewlyConfirmedReceivedCoins);
		}

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keys = transactionProcessor.KeyManager.GetKeys();
		var tx = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(0.000099m));

		AssertCoin(transactionProcessor.Process(tx));
		Assert.Single(transactionProcessor.Coins);

		// Transaction store assertions
		var mempool = transactionProcessor.TransactionStore.MempoolStore.GetTransactions();
		Assert.Single(mempool);

		var matureTxs = transactionProcessor.TransactionStore.ConfirmedStore.GetTransactions().ToArray();
		Assert.Empty(matureTxs);

		// A second tiny payment from someone else to the same, now used, address.
		var secondTx = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(0.000099m));

		AssertCoin(transactionProcessor.Process(secondTx));
		Assert.Equal(2, transactionProcessor.Coins.Count());
	}

	[Fact]
	public async Task ReceiveManyConsecutiveSmallTransactionsForWalletAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keys = transactionProcessor.KeyManager.GetKeys();

		foreach (var hdPubKey in keys.Take(5))
		{
			transactionProcessor.Process(
				CreateCreditingTransaction(hdPubKey.GetAssumedScriptPubKey(), Money.Coins(0.000099m)));
		}

		// It is relevant even when all the coins can be dust.
		Assert.All(keys.Take(5), key => Assert.Equal(KeyState.Used, key.KeyState));
		Assert.All(keys.Skip(5), key => Assert.Equal(KeyState.Clean, key.KeyState));
	}

	[Fact]
	public async Task ReceiveSmallChangeFromOurselvesAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		var keys = transactionProcessor.KeyManager.GetKeys();
		var creditingTx = CreateCreditingTransaction(keys.First().P2Taproot, Money.Coins(0.0001001m));
		var relevant = transactionProcessor.Process(creditingTx);
		var receivedCoin = Assert.Single(relevant.ReceivedCoins);

		using var destinationKey = new Key();
		var spendingTx = CreateSpendingTransaction(
			Enumerable.Repeat(receivedCoin.Coin, 1),
			destinationKey.GetScriptPubKey(ScriptPubKeyType.Legacy),
			transactionProcessor.KeyManager.GetNextReceiveKey().P2Taproot,
			height: 1);
		relevant = transactionProcessor.Process(spendingTx);
		Assert.True(relevant.IsNews);

		// There is one coin (the small change of the payment).
		var smallChangeCoin = Assert.Single(transactionProcessor.Coins);
		Assert.True(smallChangeCoin.Amount < Money.Coins(0.0001m));
	}

	[Fact]
	public async Task ReceiveCoinJoinTransactionAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keys = transactionProcessor.KeyManager.GetKeys();

		var amount = Money.Coins(0.1m);

		var tx = Network.RegTest.CreateTransaction();
		tx.Version = 1;
		tx.LockTime = LockTime.Zero;
		tx.Outputs.Add(amount, keys.Skip(1).First().P2Taproot);
		tx.Outputs.AddRange(CreateRandomIndistinguishableOutputs(amount, count: 5));
		tx.Inputs.AddRange(CreateRandomInputs(count: 4));
		var relevant = transactionProcessor.Process(new SmartTransaction(tx, Height.Mempool));

		// It is relevant even when all the coins can be dust.
		Assert.True(relevant.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins);
		Assert.Equal(1, coin.AnonymitySet);
		Assert.Equal(amount, coin.Amount);
	}

	[Fact]
	public async Task ReceiveWasabiCoinJoinTransactionAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keys = transactionProcessor.KeyManager.GetKeys();
		var amount = Money.Coins(0.1m);

		var stx = CreateCreditingTransaction(keys.First().P2Taproot, amount);
		transactionProcessor.Process(stx);

		var createdCoin = stx.Transaction.Outputs.AsCoins().First();

		var tx = Network.RegTest.CreateTransaction();
		tx.Version = 1;
		tx.LockTime = LockTime.Zero;
		tx.Outputs.Add(amount, keys.Skip(1).First().P2Taproot);
		tx.Outputs.AddRange(CreateRandomIndistinguishableOutputs(amount, 5));
		tx.Inputs.Add(createdCoin.Outpoint, Script.Empty, WitScript.Empty);
		tx.Inputs.AddRange(CreateRandomInputs(4));

		// Confirmed to bypass signature check
		var relevant = transactionProcessor.Process(new SmartTransaction(tx, new Height.ChainHeight(1)));

		// It is relevant even when all the coins can be dust.
		Assert.True(relevant.IsNews);
		var coin = Assert.Single(transactionProcessor.Coins, c => c.AnonymitySet > 1);
		Assert.Equal(5, coin.AnonymitySet);
		Assert.Equal(amount, coin.Amount);
	}

	[Fact]
	public async Task SimpleDirectClusteringAsync()
	{
		// --tx0---> (A) --tx1-+---> (pay to B)
		//                     |
		//                     +---> (change of B - cluster B, A)  --tx2-+---> (pay to myself C - cluster C, B, A and the change)
		//                                                               |
		//                                                               +---> (change of C - cluster C, B, A and the change)
		//
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var createdCoin = transactionProcessor.Coins.First();
		AssertCluster(createdCoin, keyA);

		// Spend the received coin to someone else B
		var keyB = transactionProcessor.NewKey();
		var changeScript1 = keyB.P2Taproot;
		using Key key = new();
		var tx1 = CreateSpendingTransaction(new[] { createdCoin.Coin }, key.PubKey.ScriptPubKey, changeScript1, height: 1);
		transactionProcessor.Process(tx1);
		createdCoin = transactionProcessor.Coins.First();
		AssertCluster(createdCoin, keyA, keyB);

		// Spend the received coin to myself else C
		var keyC = transactionProcessor.NewKey();
		var myselfScript = keyC.P2Taproot;
		var changeKey2 = transactionProcessor.NewKey();
		var changeScript2 = changeKey2.P2Taproot;
		var tx2 = CreateSpendingTransaction(new[] { createdCoin.Coin }, myselfScript, changeScript2, height: 2);
		transactionProcessor.Process(tx2);

		Assert.Equal(2, transactionProcessor.Coins.Count());
		createdCoin = transactionProcessor.Coins.First();
		AssertCluster(createdCoin, keyA, keyB, keyC, changeKey2);

		var createdChangeCoin = transactionProcessor.Coins.Last();
		AssertCluster(createdChangeCoin, keyA, keyB, keyC, changeKey2);
	}

	[Fact]
	public async Task MultipleDirectClusteringAsync()
	{
		// --tx0---> (A) --+
		//                 |
		// --tx1---> (B) --+---tx3-+--> (pay to D)
		//                 |       |
		// --tx2---> (C) --+       +--> (change of D - cluster D, A, B, C)---+
		//                                                                   |
		//                                                                   +---tx5--+-->(pay to F)
		//                                                                   |        |
		// --tx4---> (E) ----------------------------------------------------+        +-->(change of F - cluster F, D, A, B, C, E)

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var keyB = transactionProcessor.NewKey();
		var tx1 = CreateCreditingTransaction(keyB.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx1);

		var keyC = transactionProcessor.NewKey();
		var tx2 = CreateCreditingTransaction(keyC.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx2);

		var smartCoinA = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyA);
		var smartCoinB = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyB);
		var smartCoinC = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyC);

		var keyD = transactionProcessor.NewKey();
		var changeScript = keyD.P2Taproot;
		var coins = new[] { smartCoinA.Coin, smartCoinB.Coin, smartCoinC.Coin };
		using Key key1 = new();
		var tx3 = CreateSpendingTransaction(coins, key1.PubKey.ScriptPubKey, changeScript, height: 1);
		transactionProcessor.Process(tx3);

		var changeCoinD = Assert.Single(transactionProcessor.Coins);
		AssertCluster(changeCoinD, keyA, keyB, keyC, keyD);

		var keyE = transactionProcessor.NewKey();
		var tx4 = CreateCreditingTransaction(keyE.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx4);
		var smartCoinE = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyE);

		var keyF = transactionProcessor.NewKey();
		changeScript = keyF.P2Taproot;
		coins = new[] { changeCoinD.Coin, smartCoinE.Coin };
		using Key key2 = new();
		var tx5 = CreateSpendingTransaction(coins, key2.PubKey.ScriptPubKey, changeScript, height: 2);
		transactionProcessor.Process(tx5);

		var changeCoin = Assert.Single(transactionProcessor.Coins);
		AssertCluster(changeCoin, keyA, keyB, keyC, keyD, keyE, keyF);
	}

	[Fact]
	public async Task SameScriptClusteringAsync()
	{
		// --tx0---> (A) --+
		//                 |
		// --tx1---> (B) --+---tx3-+--> (pay to D - myself - cluster (D, A, B, C))
		//                 |
		// --tx2---> (C) --+
		//
		// --tx4---> (D - reuse - cluster (D, A, B, C))

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var keyB = transactionProcessor.NewKey();
		var tx1 = CreateCreditingTransaction(keyB.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx1);

		var keyC = transactionProcessor.NewKey();
		var tx2 = CreateCreditingTransaction(keyC.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx2);

		var smartCoinA = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyA);
		var smartCoinB = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyB);
		var smartCoinC = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyC);

		var keyD = transactionProcessor.NewKey();
		var myself = keyD.P2Taproot;
		var changeKey = transactionProcessor.NewKey();
		var changeScript = changeKey.P2Taproot;
		var coins = new[] { smartCoinA.Coin, smartCoinB.Coin, smartCoinC.Coin };
		var tx3 = CreateSpendingTransaction(coins, myself, changeScript, height: 1);
		transactionProcessor.Process(tx3);

		var paymentCoin = Assert.Single(transactionProcessor.Coins, c => c.ScriptPubKey == myself);
		AssertCluster(paymentCoin, keyA, keyB, keyC, keyD, changeKey);

		var tx4 = CreateCreditingTransaction(myself, Money.Coins(7.0m));
		transactionProcessor.Process(tx4);
		Assert.Equal(2, transactionProcessor.Coins.Count(c => c.ScriptPubKey == myself));
		var newPaymentCoin = Assert.Single(transactionProcessor.Coins, c => c.Amount == Money.Coins(7.0m));
		AssertCluster(newPaymentCoin, keyA, keyB, keyC, keyD, changeKey);
	}

	[Fact]
	public async Task SameScriptClusterAfterSpendingAsync()
	{
		// If there are two coins with the same scriptPubKey after we spend one of them,
		// both have to share the same cluster.
		//
		// --tx0---> (A) -->
		//
		// --tx1---> (A) ---tx2---> (B - cluster (B, A))

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var tx1 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		var result = transactionProcessor.Process(tx1);

		var keyB = transactionProcessor.NewKey();
		var tx2 = CreateSpendingTransaction(result.ReceivedCoins[0].Coin, keyB.P2Taproot, height: 1);
		transactionProcessor.Process(tx2);

		var coins = transactionProcessor.Coins;
		Assert.Equal(coins.First().HdPubKey.Cluster, coins.Last().HdPubKey.Cluster);
		AssertCluster(coins.First(), keyA, keyB);
	}

	[Fact]
	public async Task SameClusterAfterReplacedByFeeAsync()
	{
		// --tx0---> (A) --+
		//                 |
		// --tx1---> (B) --+---tx3 (replaceable)---> (pay to D - myself - cluster (D, A, B, C))
		//                 |    |
		// --tx2---> (C) --+    +----tx4 (replacement)---> (pay to D - coins is different order - cluster (D, A, B, C))
		//

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var keyB = transactionProcessor.NewKey();
		var tx1 = CreateCreditingTransaction(keyB.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx1);

		var keyC = transactionProcessor.NewKey();
		var tx2 = CreateCreditingTransaction(keyC.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx2);

		var smartCoinA = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyA);
		var smartCoinB = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyB);
		var smartCoinC = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyC);

		var keyD = transactionProcessor.NewKey();
		var myself = keyD.P2Taproot;
		var changeKey = transactionProcessor.NewKey();
		var changeScript = changeKey.P2Taproot;
		var coins = new[] { smartCoinA.Coin, smartCoinB.Coin, smartCoinC.Coin };
		var tx3 = CreateSpendingTransaction(coins, myself, changeScript, replaceable: true, keyManager: transactionProcessor.KeyManager);
		transactionProcessor.Process(tx3);

		var paymentCoin = Assert.Single(transactionProcessor.Coins, c => c.ScriptPubKey == myself);
		AssertCluster(paymentCoin, keyA, keyB, keyC, keyD, changeKey);

		coins = new[] { smartCoinB.Coin, smartCoinC.Coin, smartCoinA.Coin };
		var tx4 = CreateSpendingTransaction(coins, myself, changeScript, keyManager: transactionProcessor.KeyManager);
		transactionProcessor.Process(tx4);

		paymentCoin = Assert.Single(transactionProcessor.Coins, c => c.ScriptPubKey == myself);
		AssertCluster(paymentCoin, keyA, keyB, keyC, keyD, changeKey);
	}

	[Fact]
	public async Task UpdateClusterAfterReplacedByFeeWithNewCoinsAsync()
	{
		// --tx0---> (A) --+
		//                 |
		// --tx1---> (B) --+---tx3 (replaceable)---> (pay to D - myself - cluster (D, A, B, C))
		//                 |    |
		// --tx2---> (C) --+    +----tx5 (replacement)---> (pay to D - coins is different order - cluster (D, A, B, C, X))
		//                      |
		// --tx4---> (X) -------+

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var keyB = transactionProcessor.NewKey();
		var tx1 = CreateCreditingTransaction(keyB.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx1);

		var keyC = transactionProcessor.NewKey();
		var tx2 = CreateCreditingTransaction(keyC.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx2);

		var smartCoinA = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyA);
		var smartCoinB = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyB);
		var smartCoinC = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyC);

		var keyD = transactionProcessor.NewKey();
		var myself = keyD.P2Taproot;
		var changeKey = transactionProcessor.NewKey();
		var changeScript = changeKey.P2Taproot;
		var coins = new[] { smartCoinA.Coin, smartCoinB.Coin, smartCoinC.Coin };
		var tx3 = CreateSpendingTransaction(coins, myself, changeScript, replaceable: true, keyManager: transactionProcessor.KeyManager);
		transactionProcessor.Process(tx3);

		var paymentCoin = Assert.Single(transactionProcessor.Coins, c => c.ScriptPubKey == myself);
		AssertCluster(paymentCoin, keyA, keyB, keyC, keyD, changeKey);

		var keyX = transactionProcessor.NewKey();
		var tx4 = CreateCreditingTransaction(keyX.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx4);
		var smartCoinX = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyX);

		coins = new[] { smartCoinB.Coin, smartCoinX.Coin, smartCoinC.Coin, smartCoinA.Coin };
		var tx5 = CreateSpendingTransaction(coins, myself, changeScript, keyManager: transactionProcessor.KeyManager);
		transactionProcessor.Process(tx5);

		paymentCoin = Assert.Single(transactionProcessor.Coins, c => c.ScriptPubKey == myself);
		AssertCluster(paymentCoin, keyA, keyB, keyC, keyD, keyX, changeKey);
	}

	[Fact]
	public async Task RememberClusteringAfterReorgAsync()
	{
		// --tx0---> (A) --+
		//                 |
		// --tx1---> (B) --+---tx3----> (pay to D)
		//                 |    ^
		// --tx2---> (C) --+    |
		//                      +---- The block is reorg and tx3 is removed
		//

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m), height: 54321);
		transactionProcessor.Process(tx0);

		var keyB = transactionProcessor.NewKey();
		var tx1 = CreateCreditingTransaction(keyB.P2Taproot, Money.Coins(1.0m), height: 54322);
		transactionProcessor.Process(tx1);

		var keyC = transactionProcessor.NewKey();
		var tx2 = CreateCreditingTransaction(keyC.P2Taproot, Money.Coins(1.0m), height: 54323);
		transactionProcessor.Process(tx2);

		var smartCoinA = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyA);
		var smartCoinB = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyB);
		var smartCoinC = Assert.Single(transactionProcessor.Coins, c => c.HdPubKey == keyC);

		var keyD = transactionProcessor.NewKey();
		var changeScript = keyD.P2Taproot;
		var coins = new[] { smartCoinA.Coin, smartCoinB.Coin, smartCoinC.Coin };
		using Key key = new();
		var tx3 = CreateSpendingTransaction(coins, key.PubKey.ScriptPubKey, changeScript, height: 55555);
		transactionProcessor.Process(tx3);

		var changeCoinD = Assert.Single(transactionProcessor.Coins);
		AssertCluster(changeCoinD, keyA, keyB, keyC, keyD);
		Assert.Equal(smartCoinA.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);
		Assert.Equal(smartCoinB.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);
		Assert.Equal(smartCoinC.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);

		// reorg
		Assert.True(changeCoinD.Confirmed);
		transactionProcessor.UndoBlock((Height.ChainHeight)tx3.Height); // FIXME: a confirmed tx can have a Height other than chain!?
		Assert.False(changeCoinD.Confirmed);

		AssertCluster(changeCoinD, keyA, keyB, keyC, keyD);
		Assert.Equal(smartCoinA.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);
		Assert.Equal(smartCoinB.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);
		Assert.Equal(smartCoinC.HdPubKey.Cluster, changeCoinD.HdPubKey.Cluster);
	}

	[Fact]
	public async Task EnoughAnonymitySetClusteringAsync()
	{
		// --tx0---> (A) --tx1---> (empty)
		//
		// Note: tx1 is a coinjoin transaction

		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyA = transactionProcessor.NewKey();
		var tx0 = CreateCreditingTransaction(keyA.P2Taproot, Money.Coins(1.0m));
		transactionProcessor.Process(tx0);

		var receivedCoin = Assert.Single(transactionProcessor.Coins);

		// build coinjoin transaction
		var coinjoinTransaction = Network.RegTest.CreateTransaction();

		// A Taproot signature commits to every output that the transaction spends, so the wallet can only sign
		// its own input if it knows the outputs that the other participants spend.
		var othersCoins = new List<Coin>();
		for (var i = 0; i < 100; i++)
		{
			using var othersKey = new Key();
			var othersCoin = new Coin(GetRandomOutPoint(), new TxOut(Money.Coins(0.1m), othersKey.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
			othersCoins.Add(othersCoin);
			coinjoinTransaction.Inputs.Add(othersCoin.Outpoint, Script.Empty, WitScript.Empty);
		}
		for (var i = 0; i < 100; i++)
		{
			coinjoinTransaction.Outputs.Add(Money.Coins(0.1m), new Key());
		}
		coinjoinTransaction.Inputs.Add(receivedCoin.Outpoint, Script.Empty, WitScript.Empty);
		coinjoinTransaction.Outputs.Add(Money.Coins(0.1m), transactionProcessor.NewKey().P2Taproot);
		coinjoinTransaction.Outputs.Add(Money.Coins(0.9m), transactionProcessor.NewKey().P2Taproot);

		// Sign our input in the coinjoin
		SignTransaction(coinjoinTransaction, [receivedCoin.Coin], transactionProcessor.KeyManager, othersCoins);
		var tx1 = new SmartTransaction(coinjoinTransaction, Height.Mempool);

		transactionProcessor.Process(tx1);
		var anonymousCoin = Assert.Single(transactionProcessor.Coins, c => c.Amount == Money.Coins(0.1m));
		var changeCoin = Assert.Single(transactionProcessor.Coins, c => c.Amount == Money.Coins(0.9m));

		// The anonymous coin is alone in its cluster, the change is still linked to the coin that was spent.
		AssertCluster(anonymousCoin, anonymousCoin.HdPubKey);
		AssertCluster(changeCoin, keyA, changeCoin.HdPubKey);
	}

	[Fact]
	public async Task GetCoinGroupsAsync()
	{
		int targetAnonSet = 60;
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);

		for (var i = 0; i < 3; i++)
		{
			transactionProcessor.Process(CreateCreditingTransaction(transactionProcessor.NewKey().P2Taproot, Money.Coins(1.0m)));
		}

		var notYetPrivateCoin = transactionProcessor.NewKey();
		transactionProcessor.Process(CreateCreditingTransaction(notYetPrivateCoin.P2Taproot, Money.Coins(1.0m)));
		notYetPrivateCoin.SetAnonymitySet(targetAnonSet - 1, 0);

		var privateCoin1 = transactionProcessor.NewKey();
		transactionProcessor.Process(CreateCreditingTransaction(privateCoin1.P2Taproot, Money.Coins(1.0m)));
		privateCoin1.SetAnonymitySet(targetAnonSet, 0);

		var privateCoin2 = transactionProcessor.NewKey();
		transactionProcessor.Process(CreateCreditingTransaction(privateCoin2.P2Taproot, Money.Coins(1.0m)));
		privateCoin2.SetAnonymitySet(targetAnonSet, 0);

		var groups = transactionProcessor.Coins.GetCoinGroups(targetAnonSet).ToArray();

		// The most private coins come first.
		Assert.Equal([PrivacyLevel.Private, PrivacyLevel.SemiPrivate, PrivacyLevel.NonPrivate], groups.Select(x => x.PrivacyLevel));

		Assert.Equal(2, groups[0].Coins.Count());
		Assert.Equal(Money.Coins(2.0m), groups[0].Amount);
		Assert.Equal(notYetPrivateCoin, Assert.Single(groups[1].Coins).HdPubKey);
		Assert.Equal(3, groups[2].Coins.Count());
		Assert.Equal(Money.Coins(3.0m), groups[2].Amount);
	}

	[Fact]
	public async Task GetCoinGroupsLeavesOutEmptyGroupsAsync()
	{
		int targetAnonSet = 10;
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		transactionProcessor.Process(CreateCreditingTransaction(transactionProcessor.NewKey().P2Taproot, Money.Coins(0.0000_1000m)));
		transactionProcessor.Process(CreateCreditingTransaction(transactionProcessor.NewKey().P2Taproot, Money.Coins(0.0000_0670m)));

		var group = Assert.Single(transactionProcessor.Coins.GetCoinGroups(targetAnonSet));

		Assert.Equal(PrivacyLevel.NonPrivate, group.PrivacyLevel);
		Assert.Equal(2, group.Coins.Count());
	}

	/// <summary>Asserts that the cluster of the coin consists of exactly the given keys.</summary>
	private static void AssertCluster(SmartCoin coin, params HdPubKey[] keys) =>
		Assert.Equal(new Cluster([.. keys]), coin.HdPubKey.Cluster);

	private static SmartTransaction CreateSpendingTransaction(Coin coin, Script? scriptPubKey = null, uint height = 0, KeyManager? keyManager = null)
	{
		var tx = Network.RegTest.CreateTransaction();
		tx.Inputs.Add(coin.Outpoint, Script.Empty, WitScript.Empty);
		tx.Outputs.Add(coin.Amount, scriptPubKey ?? Script.Empty);

		if (keyManager is not null)
		{
			tx = SignTransaction(tx, [coin], keyManager);
		}

		return new SmartTransaction(tx, height == 0 ? Height.Mempool : new Height.ChainHeight.ChainHeight(height));
	}

	private static SmartTransaction CreateSpendingTransaction(IEnumerable<Coin> coins, Script scriptPubKey, Script scriptPubKeyChange, bool replaceable = false, uint height = 0, KeyManager? keyManager = null)
	{
		var tx = Network.RegTest.CreateTransaction();
		var coinArray = coins.ToArray();
		var amount = Money.Zero;
		foreach (var coin in coinArray)
		{
			tx.Inputs.Add(coin.Outpoint, Script.Empty, WitScript.Empty, replaceable ? Sequence.MAX_BIP125_RBF_SEQUENCE : Sequence.SEQUENCE_FINAL);
			amount += coin.Amount;
		}
		tx.Outputs.Add(amount.Percentage(60), scriptPubKey ?? Script.Empty);
		tx.Outputs.Add(amount.Percentage(40), scriptPubKeyChange);

		if (keyManager is not null)
		{
			tx = SignTransaction(tx, coinArray, keyManager);
		}

		return new SmartTransaction(tx, height == 0 ? Height.Mempool : new Height.ChainHeight.ChainHeight(height));
	}

	/// <param name="coins">The coins of the wallet that the transaction spends. Their inputs get signed.</param>
	/// <param name="othersCoins">The coins that others spend in the transaction, if any. A Taproot signature commits to them too.</param>
	private static Transaction SignTransaction(Transaction tx, Coin[] coins, KeyManager keyManager, IEnumerable<Coin>? othersCoins = null)
	{
		var builder = Network.RegTest.CreateTransactionBuilder();
		foreach (var coin in coins)
		{
			var secrets = keyManager.GetSecrets("password", coin.ScriptPubKey);
			foreach (var secret in secrets)
			{
				builder.AddKeys(secret);
			}
			builder.AddCoins(coin);
		}

		if (othersCoins is not null)
		{
			builder.AddCoins(othersCoins);
		}

		builder.SignTransactionInPlace(tx);
		return tx;
	}

	/// <summary>
	/// Signs a SmartTransaction in place and returns it. Use this for transactions
	/// that need to be modified after creation (e.g., RBF tests that modify sequence/outputs).
	/// </summary>
	private static SmartTransaction SignSmartTransaction(SmartTransaction stx, Coin[] coins, KeyManager keyManager)
	{
		SignTransaction(stx.Transaction, coins, keyManager);
		return stx;
	}

	private static SmartTransaction CreateCreditingTransaction(Script scriptPubKey, Money amount, uint height = 0)
	{
		var tx = Network.RegTest.CreateTransaction();
		tx.Version = 1;
		tx.LockTime = LockTime.Zero;
		tx.Inputs.Add(GetRandomOutPoint(), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
		tx.Inputs.Add(GetRandomOutPoint(), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
		tx.Outputs.Add(amount, scriptPubKey);
		return new SmartTransaction(tx, height == 0 ? Height.Mempool : new Height.ChainHeight.ChainHeight(height));
	}

	private static OutPoint GetRandomOutPoint()
	{
		return new OutPoint(RandomUtils.GetUInt256(), 0);
	}

	private async Task<AllTransactionStore> CreateTransactionStoreAsync([CallerFilePath] string callerFilePath = "", [CallerMemberName] string callerMemberName = "")
	{
		string dir = Path.Combine(Common.GetWorkDir(callerFilePath, callerMemberName), "TransactionStore");
		await IoHelpers.TryDeleteDirectoryAsync(dir);
		AllTransactionStore txStore = new(dir, Network.RegTest);
		await txStore.InitializeAsync();
		return txStore;
	}

	private TransactionProcessor CreateTransactionProcessor(AllTransactionStore transactionStore) =>
		CreateTransactionProcessor(transactionStore, out _);

	private TransactionProcessor CreateTransactionProcessor(AllTransactionStore transactionStore, out EventBus eventBus)
	{
		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		eventBus = new EventBus();
		return new TransactionProcessor(
			transactionStore,
			null,
			keyManager,
			eventBus);
	}

	private List<TxOut> CreateRandomIndistinguishableOutputs(long amount, int count)
	{
		List<TxOut> outputs = new(capacity: count);

		for (int i = 0; i < count; i++)
		{
			outputs.Add(new TxOut(amount, BitcoinFactory.CreateScript()));
		}

		return outputs;
	}

	private List<TxIn> CreateRandomInputs(int count)
	{
		List<TxIn> inputs = new(capacity: count);

		for (int i = 0; i < count; i++)
		{
			inputs.Add(new TxIn(GetRandomOutPoint(), Script.Empty));
		}

		return inputs;
	}

	/// <summary>
	/// Regression test for the Electrum GHSA-6jp4-8v48-cc5v attack:
	/// a forged unconfirmed transaction with invalid signatures that spends our coins must be rejected.
	/// </summary>
	[Fact]
	public async Task ForgedMempoolTransaction_WithInvalidSignatures_IsRejectedAsync()
	{
		using var txStore = await CreateTransactionStoreAsync();
		var transactionProcessor = CreateTransactionProcessor(txStore);
		var keyChain = new WalletWasabi.WabiSabi.Client.KeyChain(transactionProcessor.KeyManager, "password");

		// 1. Fund the wallet with a confirmed coin
		var hdPubKey = transactionProcessor.NewKey();
		var ourScript = hdPubKey.P2Taproot;
		var fundingTx = CreateCreditingTransaction(ourScript, Money.Coins(1.0m), height: 100);
		transactionProcessor.Process(fundingTx);

		var fundedCoin = Assert.Single(transactionProcessor.Coins);
		Assert.True(fundedCoin.Confirmed);

		// 2. Build a legitimate spending transaction (tx1) with valid signatures
		using var recipientKey = new Key();
		var recipientScript = recipientKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);
		var changeHdPubKey = transactionProcessor.NewKey();
		var changeScript = changeHdPubKey.P2Taproot;

		var spentCoin = new Coin(fundingTx.Transaction.GetHash(), 0, Money.Coins(1.0m), ourScript);
		var tx1 = Network.RegTest.CreateTransaction();
		tx1.Inputs.Add(new TxIn(spentCoin.Outpoint));
		tx1.Outputs.Add(Money.Coins(0.5m), recipientScript);
		tx1.Outputs.Add(Money.Coins(0.4999m), changeScript);

		// Sign tx1 with the correct key using KeyChain
		tx1 = keyChain.Sign(tx1, spentCoin, tx1.PrecomputeTransactionData([spentCoin]));

		var smartTx1 = new SmartTransaction(tx1, Height.Mempool);
		var result1 = transactionProcessor.Process(smartTx1);

		// tx1 should be accepted
		Assert.True(result1.IsNews);
		var walletSpentCoin = Assert.Single(transactionProcessor.Coins.AsAllCoinsView(), c => c.IsSpent());
		Assert.Equal(smartTx1.GetHash(), walletSpentCoin.SpenderTransaction?.GetHash());

		// 3. Create forged tx2: same inputs, but recipient swapped for attacker, witness data copied (invalid)
		using var attackerKey = new Key();
		var attackerScript = attackerKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);

		var tx2 = tx1.Clone();
		// Swap the recipient output to pay the attacker instead
		tx2.Outputs[0].ScriptPubKey = attackerScript;
		// The witness data is still from tx1, which signed different outputs - so it's invalid

		Assert.NotEqual(tx1.GetHash(), tx2.GetHash()); // Different txids due to different outputs

		// 4. Process the forged tx2 - it should be silently rejected
		var smartTx2 = new SmartTransaction(tx2, Height.Mempool);
		var result2 = transactionProcessor.Process(smartTx2);

		// tx2 should NOT be news (rejected)
		Assert.False(result2.IsNews);

		// The original tx1 should still be the spender
		walletSpentCoin = Assert.Single(transactionProcessor.Coins.AsAllCoinsView(), c => c.IsSpent());
		Assert.Equal(smartTx1.GetHash(), walletSpentCoin.SpenderTransaction?.GetHash());
		Assert.NotEqual(smartTx2.GetHash(), walletSpentCoin.SpenderTransaction?.GetHash());

		// tx2 should NOT be in the transaction store
		Assert.False(transactionProcessor.TransactionStore.TryGetTransaction(smartTx2.GetHash(), out _));
		Assert.True(transactionProcessor.TransactionStore.TryGetTransaction(smartTx1.GetHash(), out _));
	}
}
