using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Services;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// The wallet with a node that broadcasts privately, which is what the wallet takes the node of its user to be: a
/// Bitcoin Core with <c>privatebroadcast=1</c>. Such a node sends a transaction that it is handed over short-lived
/// connections through its proxy, and takes it into its own mempool only when it hears it back from the network.
/// </summary>
[Collection("Integration tests")]
public class PrivateBroadcastTests : IClassFixture<PrivateBroadcastTestFixture>
{
	private static readonly FeeRate FeeRate = new(20m);

	private readonly PrivateBroadcastTestFixture _fixture;

	public PrivateBroadcastTests(PrivateBroadcastTestFixture fixture)
	{
		_fixture = fixture;
	}

	/// <summary>
	/// A payment, and right after it a payment from the change of the first one. The node refuses the second one for
	/// as long as it has not heard the first one back, which takes it seconds, so the second one waits for that.
	/// </summary>
	[Fact(Timeout = 240_000)]
	public async Task PaymentFromTheChangeOfAPaymentWaitsUntilTheNodeHeardThatOneBackAsync()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var wallet = await CreateFundedWalletAsync(env, TestContext.Current.CancellationToken);

		try
		{
			// The network is there, and the node knows of places in it that it did not try yet.
			_fixture.Proxy.IsRefusing = false;
			await _fixture.AddPeerAddressesAsync();

			var node = _fixture.NodeRpcClient;
			var broadcaster = new TransactionBroadcaster(env.RpcClient, env.TransactionStore, env.EventBus);

			var first = BuildPayment(wallet, Money.Coins(0.5m));
			await broadcaster.SendTransactionAsync(first);

			// The node took it and is sending it. It does not have it in its mempool yet.
			Assert.Contains(first.GetHash(), await node.GetPrivateBroadcastQueueAsync());
			Assert.DoesNotContain(first.GetHash(), await node.GetRawMempoolAsync());

			var change = await WaitForChangeAsync(env, wallet, first);
			var second = BuildPayment(wallet, Money.Coins(0.2m), change);
			await broadcaster.SendTransactionAsync(second);

			// The node took the second one, so it has heard the first one back: it is in its mempool, not on its way.
			Assert.Contains(first.GetHash(), await node.GetRawMempoolAsync());
			Assert.DoesNotContain(first.GetHash(), await node.GetPrivateBroadcastQueueAsync());

			// The second one goes the same way.
			await WaitUntilAsync(async () => (await node.GetRawMempoolAsync()).Contains(second.GetHash()), TimeSpan.FromSeconds(90));
			Assert.Empty(await node.GetPrivateBroadcastQueueAsync());
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// A node that reaches nothing through its proxy does not get a transaction out: it keeps it in its queue. A
	/// payment from the change of that transaction gives up waiting and tells why. A cancellation takes the place of
	/// the transaction, and the node is told to stop sending the one that was cancelled.
	/// </summary>
	[Fact(Timeout = 240_000)]
	public async Task NodeThatGetsNothingOutKeepsTheTransactionUntilItIsReplacedAsync()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var wallet = await CreateFundedWalletAsync(env, TestContext.Current.CancellationToken);

		try
		{
			_fixture.Proxy.IsRefusing = true;
			var node = _fixture.NodeRpcClient;
			var broadcaster = new TransactionBroadcaster(env.RpcClient, env.TransactionStore, env.EventBus)
			{
				ParentWaitLimit = TimeSpan.FromSeconds(3),
				ParentPollPeriod = TimeSpan.FromMilliseconds(200)
			};

			var payment = BuildPayment(wallet, Money.Coins(0.5m));
			await broadcaster.SendTransactionAsync(payment);

			Assert.Equal([payment.GetHash()], await node.GetPrivateBroadcastQueueAsync());
			Assert.DoesNotContain(payment.GetHash(), await node.GetRawMempoolAsync());

			// What spends from it waits, and after a while the wallet says what the matter is.
			var change = await WaitForChangeAsync(env, wallet, payment);
			var child = BuildPayment(wallet, Money.Coins(0.2m), change);

			var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => broadcaster.SendTransactionAsync(child));
			Assert.StartsWith($"The Bitcoin node did not accept the transaction: it spends from transaction {payment.GetHash()}, which the node is still broadcasting", exception.Message);

			// The cancellation spends the coin of the payment again. The node takes it, and lets go of the payment.
			var cancellation = wallet.CancelTransaction(payment).Transaction;
			await broadcaster.SendTransactionAsync(cancellation);

			Assert.Equal([cancellation.GetHash()], await node.GetPrivateBroadcastQueueAsync());

			// The node tells whether it was broadcasting what it is told to stop broadcasting.
			Assert.False(await node.AbortPrivateBroadcastAsync(payment.GetHash()));
			Assert.True(await node.AbortPrivateBroadcastAsync(cancellation.GetHash()));
			Assert.Empty(await node.GetPrivateBroadcastQueueAsync());
		}
		finally
		{
			_fixture.Proxy.IsRefusing = false;
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>A wallet that is synchronized with the node and has one confirmed coin of 2 BTC.</summary>
	private static async Task<Wallet> CreateFundedWalletAsync(RegTestEnvironment env, CancellationToken cancellationToken)
	{
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(2m), confirmations: 1);
		await wallet.StartAsync(cancellationToken);
		await env.WaitForConditionAsync(() => wallet.Coins.Any(coin => coin.Confirmed), TimeSpan.FromSeconds(30));

		return wallet;
	}

	/// <summary>A payment of the wallet to somebody else, from the given coin or from any confirmed one.</summary>
	private static SmartTransaction BuildPayment(Wallet wallet, Money amount, SmartCoin? coin = null)
	{
		using var destinationKey = new Key();
		var destination = destinationKey.PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, wallet.Network);

		return wallet.BuildTransaction(
			RegTestEnvironment.DefaultPassword,
			new PaymentIntent(destination.ScriptPubKey, amount),
			FeeStrategy.CreateFromFeeRate(FeeRate),
			allowUnconfirmed: coin is not null,
			allowedInputs: coin is null ? null : [coin.Outpoint]).Transaction;
	}

	/// <summary>The coin that the transaction gave back to the wallet, once the wallet has processed the transaction.</summary>
	private static async Task<SmartCoin> WaitForChangeAsync(RegTestEnvironment env, Wallet wallet, SmartTransaction transaction)
	{
		await env.WaitForConditionAsync(() => wallet.Coins.Any(coin => coin.TransactionId == transaction.GetHash()), TimeSpan.FromSeconds(30));

		return wallet.Coins.Single(coin => coin.TransactionId == transaction.GetHash());
	}

	private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;

		while (!await condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException("Condition was not met within the timeout period.");
			}

			await Task.Delay(250);
		}
	}
}
