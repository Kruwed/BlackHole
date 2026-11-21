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
using WalletWasabi.Models;
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
	public async Task Wallet_RedeemsBondOnlyOnceTheChainReachedItsHeight()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		var tip = await GetHeightAsync(env);

		// A bond that unlocks three blocks from now, and one that stays locked.
		var soon = tip + 3;
		var late = tip + 500;

		await env.FundAddressAsync(keyManager.GetFidelityBondAddress(soon), Money.Coins(0.5m), confirmations: 0);
		await env.FundAddressAsync(keyManager.GetFidelityBondAddress(late), Money.Coins(0.3m), confirmations: 1);

		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			// The wallet finds both bonds in the block. They are no coins of it, but what they hold is the wallet's.
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count(bond => bond.IsConfirmed) == 2, TimeSpan.FromSeconds(30));
			Assert.Empty(wallet.Coins);
			Assert.Equal(Money.Coins(0.8m), wallet.GetFidelityBondBalance());

			var soonBond = Assert.Single(wallet.GetFidelityBonds(), bond => bond.UnlockHeight == soon);
			var lateBond = Assert.Single(wallet.GetFidelityBonds(), bond => bond.UnlockHeight == late);
			Assert.Equal(Money.Coins(0.5m), soonBond.Amount);
			Assert.Equal(Money.Coins(0.3m), lateBond.Amount);
			Assert.Equal(new Height.ChainHeight(tip + 1), soonBond.Transaction.Height);

			// The bonds are Taproot outputs like any other, as far as the chain shows.
			Assert.True(soonBond.TxOut.ScriptPubKey.IsScriptType(ScriptType.Taproot));

			// In the history they are money that came in, and that is locked.
			var history = await wallet.BuildHistorySummaryAsync(cancellationToken: TestContext.Current.CancellationToken);
			Assert.Equal([Money.Coins(0.3m), Money.Coins(0.5m)], history.Select(x => x.Amount).Order());
			Assert.All(history, summary => Assert.Equal(summary.Amount, summary.LockedInBonds));

			// The value of a bond is counted from the block that confirmed it.
			Assert.True(BondValue.Calculate(lateBond.Amount, tip + 1, late, tip + 1) > 0);

			// The chain is two blocks short of the height. The wallet does not redeem the bond.
			Assert.False(soonBond.IsRedeemable(tip + 1));
			Assert.Throws<InvalidOperationException>(() => wallet.BuildFidelityBondRedemption(soonBond, RegTestEnvironment.DefaultPassword, FeeRate, chainHeight: tip + 1));

			// And if it did, with a wrong idea of how far the chain is, the node would not take the transaction.
			var early = wallet.BuildFidelityBondRedemption(soonBond, RegTestEnvironment.DefaultPassword, FeeRate, chainHeight: soon);
			var refusal = await Assert.ThrowsAsync<RPCException>(() => env.RpcClient.SendRawTransactionAsync(early.Transaction));
			Assert.Contains("non-final", refusal.Message);

			// One block short, the same.
			await env.RpcClient.GenerateAsync(1);
			Assert.Equal(soon - 1, await GetHeightAsync(env));
			refusal = await Assert.ThrowsAsync<RPCException>(() => env.RpcClient.SendRawTransactionAsync(early.Transaction));
			Assert.Contains("non-final", refusal.Message);

			// With the block of the unlock height the bond goes back to the wallet.
			await env.RpcClient.GenerateAsync(1);
			var chainHeight = await GetHeightAsync(env);
			Assert.Equal(soon, chainHeight);
			Assert.True(soonBond.IsRedeemable(chainHeight));

			var redemption = wallet.BuildFidelityBondRedemption(soonBond, RegTestEnvironment.DefaultPassword, FeeRate, chainHeight);
			var broadcaster = new TransactionBroadcaster(env.RpcClient, env.TransactionStore, env.EventBus);
			await broadcaster.SendTransactionAsync(redemption);

			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Any(bond => bond.UnlockHeight == soon && bond.IsSpent), TimeSpan.FromSeconds(30));
			var coin = Assert.Single(wallet.Coins);
			Assert.Equal(redemption.Transaction.Outputs[0].Value, coin.Amount);
			Assert.True(coin.Amount < Money.Coins(0.5m));
			Assert.True(coin.Amount > Money.Coins(0.4999m));

			// What the wallet owns went down by the mining fee, and the history tells it as the unlocking of the bond.
			var fee = Money.Coins(0.5m) - coin.Amount;
			Assert.Equal(Money.Coins(0.3m), wallet.GetFidelityBondBalance());
			history = await wallet.BuildHistorySummaryAsync(cancellationToken: TestContext.Current.CancellationToken);
			var unlock = Assert.Single(history, summary => summary.GetHash() == redemption.GetHash());
			Assert.Equal(-fee, unlock.Amount);
			Assert.Equal(Money.Coins(0.5m), unlock.UnlockedFromBonds);
			Assert.Equal(Money.Zero, unlock.LockedInBonds);
			Assert.Equal(fee, unlock.GetFee());
			Assert.NotNull(unlock.FeeRate());
			Assert.Equal(Money.Coins(0.8m) - fee, Money.Satoshis(history.Sum(x => x.Amount.Satoshi)));

			// It confirms like any other transaction, and the output of the bond is gone from the node's set of unspent outputs.
			await env.RpcClient.GenerateAsync(1);
			await env.WaitForConditionAsync(() => wallet.Coins.Any(c => c.Confirmed), TimeSpan.FromSeconds(30));
			Assert.Null(await env.RpcClient.GetTxOutAsync(soonBond.OutPoint.Hash, (int)soonBond.OutPoint.N, includeMempool: false));
			Assert.NotNull(await env.RpcClient.GetTxOutAsync(lateBond.OutPoint.Hash, (int)lateBond.OutPoint.N, includeMempool: false));

			// The other bond is still there, this one is redeemed for good.
			Assert.False(Assert.Single(wallet.GetFidelityBonds(), bond => bond.UnlockHeight == late).IsSpent);
			Assert.True(Assert.Single(wallet.GetFidelityBonds(), bond => bond.UnlockHeight == soon).SpenderTransaction!.Confirmed);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	[Fact(Timeout = 120_000)]
	public async Task Wallet_LocksItsCoinsInABond()
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
			var unlockHeight = await GetHeightAsync(env) + 1_000;
			var bondAddress = keyManager.GetFidelityBondAddress(unlockHeight);
			var funding = wallet.BuildTransaction(
				RegTestEnvironment.DefaultPassword,
				new PaymentIntent(bondAddress.ScriptPubKey, Money.Coins(0.6m)),
				FeeStrategy.CreateFromConfirmationTarget(6),
				allowUnconfirmed: false);

			await new TransactionBroadcaster(env.RpcClient, env.TransactionStore, env.EventBus).SendTransactionAsync(funding.Transaction);

			// The bond is there at once, unconfirmed, and what the wallet can spend is the change.
			await env.WaitForConditionAsync(() => wallet.GetFidelityBonds().Count == 1, TimeSpan.FromSeconds(30));
			var bond = Assert.Single(wallet.GetFidelityBonds());
			Assert.Equal(Money.Coins(0.6m), bond.Amount);
			Assert.Equal(unlockHeight, bond.UnlockHeight);
			Assert.False(bond.IsConfirmed);
			Assert.Equal(Money.Coins(0.4m) - funding.Fee, wallet.Coins.TotalAmount());

			// What the wallet owns went down by the mining fee only: the history tells a lock, not a payment.
			Assert.Equal(Money.Coins(0.6m), wallet.GetFidelityBondBalance());
			var history = await wallet.BuildHistorySummaryAsync(cancellationToken: TestContext.Current.CancellationToken);
			var lockSummary = Assert.Single(history, summary => summary.GetHash() == funding.Transaction.GetHash());
			Assert.Equal(-funding.Fee, lockSummary.Amount);
			Assert.Equal(Money.Coins(0.6m), lockSummary.LockedInBonds);
			Assert.Equal(funding.Fee, lockSummary.GetFee());
			Assert.Equal(Money.Coins(1m) - funding.Fee, Money.Satoshis(history.Sum(x => x.Amount.Satoshi)));

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

	[Fact(Timeout = 120_000)]
	public async Task RecoveredWallet_FindsBondsWhoseHeightsItDoesNotKnow()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var tip = await GetHeightAsync(env);

		// A wallet locks coins until a height, and then its wallet file is lost.
		var lost = KeyManager.CreateNew(out var shares, RegTestEnvironment.DefaultPassword, env.Network);
		var unlockHeight = tip + 700;
		await env.FundAddressAsync(lost.GetFidelityBondAddress(unlockHeight), Money.Coins(0.25m), confirmations: 1);
		await env.FundAddressAsync(lost.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.1m), confirmations: 1);

		// The backup brings the keys back, but not the height.
		var recovered = KeyManager.Recover(
			shares.Take(WalletGenerator.DefaultShamirThreshold).ToArray(),
			RegTestEnvironment.DefaultPassword,
			env.Network,
			birthHeight: new Height.ChainHeight(tip));
		Assert.Empty(recovered.FidelityBonds.KnownHeights);
		Assert.Equal(tip, recovered.GetFidelityBondSearchStart());

		var wallet = env.CreateWallet(recovered);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			// Going through the chain, the wallet finds the coin and the bond.
			await env.WaitForWalletToCatchUpAsync(wallet, TimeSpan.FromSeconds(60));
			await env.WaitForConditionAsync(() => recovered.GetFidelityBondSearchStart() is null, TimeSpan.FromSeconds(30));

			Assert.Equal(Money.Coins(0.1m), wallet.Coins.TotalAmount());
			var bond = Assert.Single(wallet.GetFidelityBonds());
			Assert.Equal(unlockHeight, bond.UnlockHeight);
			Assert.Equal(Money.Coins(0.25m), bond.Amount);
			Assert.Equal([unlockHeight], recovered.FidelityBonds.KnownHeights);
			Assert.False(recovered.FidelityBonds.IsSearching);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	private static async Task<uint> GetHeightAsync(RegTestEnvironment env) =>
		(uint)await env.RpcClient.GetBlockCountAsync().ConfigureAwait(false);
}
