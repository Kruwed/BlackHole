using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.RPC;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBroadcasting;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.FidelityBonds;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Services;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// Fidelity bonds against a real node: it is the node that decides whether the transaction that redeems a bond is valid.
/// </summary>
[Collection("Integration tests")]
public class FidelityBondTests
{
	private static readonly FeeRate FeeRate = new(5m);
	private readonly IntegrationTestFixture _fixture;

	public FidelityBondTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)]
	public async Task Wallet_RedeemsBondOnlyAfterItsLocktime()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		var medianTimePast = await GetMedianTimePastAsync(env);

		// A bond whose locktime is over, by a month at least, and one that stays locked for a year and more.
		var next = BondLocktime.FirstAfter(medianTimePast)!.Value;
		var unlocked = BondLocktime.FromIndex(next.Index - 2);
		var locked = BondLocktime.FromIndex(next.Index + 12);

		await env.FundAddressAsync(keyManager.FidelityBonds.GetAddress(unlocked, env.Network), Money.Coins(0.5m), confirmations: 0);
		await env.FundAddressAsync(keyManager.FidelityBonds.GetAddress(locked, env.Network), Money.Coins(0.3m), confirmations: 1);

		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			// The wallet finds both bonds in the block. They are no coins of it.
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count(bond => bond.IsConfirmed) == 2, TimeSpan.FromSeconds(30));
			Assert.Empty(wallet.Coins);

			var unlockedBond = Assert.Single(wallet.GetFidelityBonds(), bond => bond.Locktime == unlocked);
			var lockedBond = Assert.Single(wallet.GetFidelityBonds(), bond => bond.Locktime == locked);
			Assert.Equal(Money.Coins(0.5m), unlockedBond.Amount);
			Assert.Equal(Money.Coins(0.3m), lockedBond.Amount);
			Assert.True(unlockedBond.IsRedeemable(medianTimePast));
			Assert.False(lockedBond.IsRedeemable(medianTimePast));

			// The value of a bond is counted from the time of the block that confirmed it.
			var confirmationTime = await wallet.GetBlockTimeAsync(lockedBond.Transaction.BlockHash!, TestContext.Current.CancellationToken);
			Assert.InRange(confirmationTime, medianTimePast.AddHours(-3), medianTimePast.AddHours(3));
			Assert.True(BondValue.Calculate(lockedBond.Amount, confirmationTime, locked, medianTimePast) > 0);
			Assert.Equal(0, BondValue.Calculate(unlockedBond.Amount, confirmationTime, unlocked, medianTimePast));

			// The wallet does not redeem the locked bond.
			Assert.Throws<InvalidOperationException>(() => wallet.BuildFidelityBondRedemption(lockedBond, RegTestEnvironment.DefaultPassword, FeeRate, medianTimePast));

			// And if it did, with a clock that runs ahead, the node would not take the transaction.
			var early = wallet.BuildFidelityBondRedemption(lockedBond, RegTestEnvironment.DefaultPassword, FeeRate, locked.Date.AddDays(1));
			var refusal = await Assert.ThrowsAsync<RPCException>(() => env.RpcClient.SendRawTransactionAsync(early.Transaction));
			Assert.Contains("non-final", refusal.Message);

			// The bond whose locktime is over goes back to the wallet.
			var redemption = wallet.BuildFidelityBondRedemption(unlockedBond, RegTestEnvironment.DefaultPassword, FeeRate, medianTimePast);
			var broadcaster = new TransactionBroadcaster(env.RpcClient, env.EventBus);
			await broadcaster.SendTransactionAsync(redemption);

			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Any(bond => bond.Locktime == unlocked && bond.IsSpent), TimeSpan.FromSeconds(30));
			var coin = Assert.Single(wallet.Coins);
			Assert.Equal(redemption.Transaction.Outputs[0].Value, coin.Amount);
			Assert.True(coin.Amount < Money.Coins(0.5m));
			Assert.True(coin.Amount > Money.Coins(0.4999m));

			// It confirms like any other transaction, and the output of the bond is gone from the node's set of unspent outputs.
			await env.RpcClient.GenerateAsync(1);
			await env.WaitForConditionAsync(() => wallet.Coins.Any(c => c.Confirmed), TimeSpan.FromSeconds(30));
			Assert.Null(await env.RpcClient.GetTxOutAsync(unlockedBond.OutPoint.Hash, (int)unlockedBond.OutPoint.N, includeMempool: false));
			Assert.NotNull(await env.RpcClient.GetTxOutAsync(lockedBond.OutPoint.Hash, (int)lockedBond.OutPoint.N, includeMempool: false));

			// The locked bond is still there, the other one is redeemed for good.
			Assert.False(Assert.Single(wallet.GetFidelityBonds(), bond => bond.Locktime == locked).IsSpent);
			Assert.True(Assert.Single(wallet.GetFidelityBonds(), bond => bond.Locktime == unlocked).SpenderTransaction!.Confirmed);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	[Fact(Timeout = 120_000)]
	public async Task Wallet_FundsBondFromItsCoins()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(1m), confirmations: 1);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			await env.WaitForConditionAsync(() => wallet.Coins.Any(c => c.Confirmed), TimeSpan.FromSeconds(30));

			env.EventBus.Publish(new MiningFeeRatesChanged(new FeeRateEstimations(new Dictionary<int, FeeRate> { { 2, new FeeRate(10m) }, { 6, new FeeRate(5m) }, { 1008, new FeeRate(1m) } })));
			await Task.Delay(100, TestContext.Current.CancellationToken);

			// Locking coins is a payment to the address of the bond.
			var locktime = BondLocktime.FromIndex(BondLocktime.FirstAfter(await GetMedianTimePastAsync(env))!.Value.Index + 6);
			var bondAddress = keyManager.FidelityBonds.GetAddress(locktime, env.Network);
			var funding = wallet.BuildTransaction(
				RegTestEnvironment.DefaultPassword,
				new PaymentIntent(bondAddress.ScriptPubKey, Money.Coins(0.6m)),
				FeeStrategy.CreateFromConfirmationTarget(6),
				allowUnconfirmed: false);

			await env.RpcClient.GenerateAsync(1);
			await new TransactionBroadcaster(env.RpcClient, env.EventBus).SendTransactionAsync(funding.Transaction);

			// The bond is there at once, unconfirmed, and what the wallet can spend is the change.
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count == 1, TimeSpan.FromSeconds(30));
			var bond = Assert.Single(wallet.GetFidelityBonds());
			Assert.Equal(Money.Coins(0.6m), bond.Amount);
			Assert.Equal(locktime, bond.Locktime);
			Assert.False(bond.IsConfirmed);
			Assert.Equal(Money.Coins(0.4m) - funding.Fee, wallet.Coins.TotalAmount());

			await env.RpcClient.GenerateAsync(1);
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Single().IsConfirmed, TimeSpan.FromSeconds(30));

			// The node holds the coins at the script of the bond.
			var output = await env.RpcClient.GetTxOutAsync(bond.OutPoint.Hash, (int)bond.OutPoint.N, includeMempool: false);
			Assert.NotNull(output);
			Assert.Equal(Money.Coins(0.6m), output.TxOut.Value);
			Assert.Equal(bondAddress.ScriptPubKey, output.TxOut.ScriptPubKey);

			// Nothing of the wallet spends the bond: its coins are the change, and that is all it can send.
			Assert.DoesNotContain(wallet.Coins, c => c.Outpoint == bond.OutPoint);
			Assert.Throws<WalletWasabi.Exceptions.InsufficientBalanceException>(() => wallet.BuildTransaction(
				RegTestEnvironment.DefaultPassword,
				new PaymentIntent(bondAddress.ScriptPubKey, Money.Coins(0.5m)),
				FeeStrategy.CreateFromConfirmationTarget(6),
				allowUnconfirmed: true));
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	private static async Task<DateTimeOffset> GetMedianTimePastAsync(RegTestEnvironment env)
	{
		var info = await env.RpcClient.GetBlockchainInfoAsync();
		return DateTimeOffset.FromUnixTimeSeconds((long)info.MedianTime);
	}
}
