using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Analysis;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Models;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// The anonymity points of a transaction depend on the transactions that created its inputs, and the wallet asks the
/// node for those. These tests ask a real node, one with an index of all transactions and one without.
/// </summary>
[Collection("Integration tests")]
public class AnonymityPointsTests
{
	private static readonly Money Fee = Money.Coins(0.0001m);

	private readonly IntegrationTestFixture _fixture;

	public AnonymityPointsTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 180_000)]
	public async Task NodeWithTransactionIndex_TellsWhatTheInputsWere()
	{
		await AssertPointsAreFoundOutAsync(_fixture.WalletRpcClient, hasTransactionIndex: true, TestContext.Current.CancellationToken);
	}

	/// <summary>
	/// A node without the index does not find a confirmed transaction by its identifier alone. The blocks of the inputs
	/// are found through the transaction that spends them.
	/// </summary>
	[Fact(Timeout = 300_000)]
	public async Task NodeWithoutTransactionIndex_TellsWhatTheInputsWere()
	{
		var node = await TestNodeBuilder.CreateAsync(callerFilePath: "IntegrationTests", callerMemberName: "BitcoinCoreDataWithoutTransactionIndex", txIndex: 0);

		try
		{
			var rpc = new RpcClientBase(await node.RpcClient.CreateWalletAsync(walletNameOrPath: "integration_test_wallet"));
			await rpc.GenerateAsync(110);

			await AssertPointsAreFoundOutAsync(rpc, hasTransactionIndex: false, TestContext.Current.CancellationToken);
		}
		finally
		{
			await node.TryStopAsync();
		}
	}

	[Fact(Timeout = 180_000)]
	public async Task Wallet_FindsOutThePointsOfItsMatchedCoin()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		var rpc = env.RpcClient;

		// Two equal outputs and change, and then a transaction that spends the equal ones into two equal outputs again,
		// one of which belongs to the wallet, and pays the wallet change too.
		var first = await SendTransactionWithTwoEqualOutputsAsync(rpc);
		await rpc.GenerateAsync(1);

		var matchedScript = keyManager.GetNextReceiveKey().P2Taproot;
		var changeScript = keyManager.GetNextReceiveKey().P2Taproot;
		var second = await SendAsync(
			rpc,
			[new OutPoint(first, 0), new OutPoint(first, 1)],
			[
				new TxOut(Money.Coins(0.9m), matchedScript),
				new TxOut(Money.Coins(0.9m), (await rpc.GetNewAddressAsync()).ScriptPubKey),
				new TxOut(Money.Coins(0.2m) - Fee, changeScript)
			]);
		await rpc.GenerateAsync(1);

		await wallet.StartAsync(TestContext.Current.CancellationToken);

		try
		{
			await env.WaitForConditionAsync(
				() => wallet.Coins.Count() == 2 && wallet.Coins.All(coin => coin.AnonymityPoints is not null),
				TimeSpan.FromSeconds(60));

			// Both inputs came out of a pair, which makes them worth one point together, and the two equal outputs are
			// a pair too, worth one point: one point for the matched coin, none for the change.
			Assert.Equal(1, Assert.Single(wallet.Coins, coin => coin.ScriptPubKey == matchedScript).AnonymityPoints);
			Assert.Equal(0, Assert.Single(wallet.Coins, coin => coin.ScriptPubKey == changeScript).AnonymityPoints);
			Assert.Equal(1, keyManager.GetAnonymityPoints(second.GetHash()));
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	private static async Task AssertPointsAreFoundOutAsync(IRPCClient rpc, bool hasTransactionIndex, CancellationToken cancellationToken)
	{
		var first = await SendTransactionWithTwoEqualOutputsAsync(rpc);
		await rpc.GenerateAsync(1, cancellationToken);

		// Whether the node finds a confirmed transaction that is not told a block is what the index is about.
		var foundByIdentifier = await rpc.GetRawTransactionAsync(first.GetHash(), throwIfNotFound: false, cancellationToken);
		Assert.Equal(hasTransactionIndex, foundByIdentifier is not null);

		// The second transaction spends all three outputs of the first: two of its inputs came out of a pair, which
		// makes them worth half a point each, and the change was worth nothing. Its own three equal outputs are worth
		// three points. One point.
		var second = await SendAsync(
			rpc,
			[new OutPoint(first, 0), new OutPoint(first, 1), new OutPoint(first, 2)],
			[
				new TxOut(Money.Coins(0.5m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.5m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.5m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(first.Outputs.Sum(output => output.Value) - Money.Coins(1.5m) - Fee, (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey)
			]);

		// While the second transaction is unconfirmed, the outputs that it spends are still in the set of unspent outputs.
		var unconfirmed = new SmartTransaction(second, Height.Mempool);
		Assert.Equal(1, await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(unconfirmed, cancellationToken));

		// Once it is confirmed they are spent, and the node tells where they came from out of the data of the block.
		var blockHash = (await rpc.GenerateAsync(1, cancellationToken)).Single();
		var height = (uint)await rpc.GetBlockCountAsync(cancellationToken);
		var confirmed = new SmartTransaction(second, new Height.ChainHeight(height), blockHash);
		Assert.Equal(1, await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(confirmed, cancellationToken));

		// A transaction that spends one matched output and has no equal outputs is worth nothing.
		var third = await SendAsync(
			rpc,
			[new OutPoint(second, 0)],
			[
				new TxOut(Money.Coins(0.3m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.2m) - Fee, (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey)
			]);
		Assert.Equal(0, await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(new SmartTransaction(third, Height.Mempool), cancellationToken));

		// One that spends two outputs of the set of three, which are worth a point each, and the unmatched output of
		// the third transaction into two equal outputs is worth one point: a pair of outputs is worth no more.
		await rpc.GenerateAsync(1, cancellationToken);
		var fourth = await SendAsync(
			rpc,
			[new OutPoint(second, 1), new OutPoint(second, 2), new OutPoint(third, 0)],
			[
				new TxOut(Money.Coins(0.6m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.6m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.1m) - Fee, (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey)
			]);
		blockHash = (await rpc.GenerateAsync(1, cancellationToken)).Single();
		height = (uint)await rpc.GetBlockCountAsync(cancellationToken);
		Assert.Equal(
			1,
			await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(new SmartTransaction(fourth, new Height.ChainHeight(height), blockHash), cancellationToken));

		// One that spends an output of that pair into three equal outputs is worth half a point: its input counts half.
		var fifth = await SendAsync(
			rpc,
			[new OutPoint(fourth, 0)],
			[
				new TxOut(Money.Coins(0.15m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.15m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.15m), (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey),
				new TxOut(Money.Coins(0.15m) - Fee, (await rpc.GetNewAddressAsync(cancellationToken)).ScriptPubKey)
			]);
		Assert.Equal(0.5m, await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(new SmartTransaction(fifth, Height.Mempool), cancellationToken));

		blockHash = (await rpc.GenerateAsync(1, cancellationToken)).Single();
		height = (uint)await rpc.GetBlockCountAsync(cancellationToken);
		Assert.Equal(
			0.5m,
			await new AnonymityPointsCalculator(rpc, new NoTransactions()).CalculateAsync(new SmartTransaction(fifth, new Height.ChainHeight(height), blockHash), cancellationToken));
	}

	/// <summary>Sends a transaction of the node's wallet with two outputs of 1 BTC and a third one for the rest.</summary>
	private static async Task<Transaction> SendTransactionWithTwoEqualOutputsAsync(IRPCClient rpc)
	{
		var coin = (await rpc.ListUnspentAsync()).First(unspent => unspent.Amount > Money.Coins(3m));

		return await SendAsync(
			rpc,
			[coin.OutPoint],
			[
				new TxOut(Money.Coins(1m), (await rpc.GetNewAddressAsync()).ScriptPubKey),
				new TxOut(Money.Coins(1m), (await rpc.GetNewAddressAsync()).ScriptPubKey),
				new TxOut(coin.Amount - Money.Coins(2m) - Fee, (await rpc.GetNewAddressAsync()).ScriptPubKey)
			]);
	}

	/// <summary>Lets the wallet of the node sign a transaction that spends its outputs, and sends it.</summary>
	private static async Task<Transaction> SendAsync(IRPCClient rpc, IEnumerable<OutPoint> inputs, IEnumerable<TxOut> outputs)
	{
		var transaction = rpc.Network.CreateTransaction();

		foreach (var input in inputs)
		{
			transaction.Inputs.Add(input);
		}

		transaction.Outputs.AddRange(outputs);

		var signed = await rpc.SignRawTransactionWithWalletAsync(new SignRawTransactionRequest { Transaction = transaction });
		Assert.True(signed.Complete);

		await rpc.SendRawTransactionAsync(signed.SignedTransaction);

		return signed.SignedTransaction;
	}

	/// <summary>A wallet that has none of the transactions: every input has to be asked for.</summary>
	private sealed class NoTransactions : ITransactionStore
	{
		public bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? sameStx)
		{
			sameStx = null;
			return false;
		}
	}
}
