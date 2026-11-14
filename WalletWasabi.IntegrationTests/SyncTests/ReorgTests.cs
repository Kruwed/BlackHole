using WalletWasabi.Blockchain.Keys;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.IntegrationTests.Infrastructure;
using Xunit;

namespace WalletWasabi.IntegrationTests.SyncTests;

/// <summary>
/// Integration tests for blockchain reorganization handling.
/// Tests how the wallet follows its node through chain reorgs.
/// </summary>
[Collection("Integration tests")]
public class ReorgTests
{
	private readonly IntegrationTestFixture _fixture;

	public ReorgTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task Reorg_ShallowReorg_WalletFollowsTheNodeAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		// Mine some blocks and synchronize
		await env.RpcClient.GenerateAsync(5);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		await env.WaitForWalletToCatchUpAsync(wallet);

		var (preHeight, preBlockHashes) = keyManager.GetProcessedBlocks();
		Assert.NotNull(preHeight);
		var preBlockHash = preBlockHashes[^1];
		Assert.Equal(preBlockHash, await env.RpcClient.GetBlockHashAsync((int)preHeight.Height));

		// Act - Invalidate the last block to simulate a reorg
		await env.RpcClient.InvalidateBlockAsync(preBlockHash);

		// Mine different blocks (creates a reorg)
		await env.RpcClient.GenerateAsync(2);

		// The wallet detects the reorg and follows the node
		await env.WaitForWalletToCatchUpAsync(wallet);

		// Assert
		var (postHeight, postBlockHashes) = keyManager.GetProcessedBlocks();
		Assert.NotNull(postHeight);
		Assert.Equal(preHeight.Height + 1, postHeight.Height);

		// The block that was dropped is not among the processed blocks any more, the one that took its place is.
		Assert.DoesNotContain(preBlockHash, postBlockHashes);
		Assert.Equal(await env.RpcClient.GetBlockHashAsync((int)preHeight.Height), postBlockHashes[^2]);
		Assert.Equal(await env.RpcClient.GetBlockHashAsync((int)postHeight.Height), postBlockHashes[^1]);

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task Reorg_WalletHandlesReorg_CoinsStateUpdatedAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		var receiveKey = keyManager.GetNextReceiveKey();
		var receiveAddress = receiveKey.GetAddress(env.Network);

		// Fund the wallet and confirm
		await env.FundAddressAsync(receiveAddress, Money.Coins(1m), confirmations: 1);

		// Get the block hash we'll invalidate later
		var tipHeight = await env.RpcClient.GetBlockCountAsync();
		var blockToInvalidate = await env.RpcClient.GetBlockHashAsync(tipHeight);

		await wallet.StartAsync(TestContext.Current.CancellationToken);

		await env.WaitForConditionAsync(
			() => wallet.Coins.Any(c => c.Confirmed),
			TimeSpan.FromSeconds(30));

		// Verify we have a confirmed coin
		Assert.Single(wallet.Coins);
		var coin = wallet.Coins.First();
		Assert.True(coin.Confirmed);
		Assert.Equal(blockToInvalidate, coin.Transaction.BlockHash);

		// Act - Invalidate the block containing our transaction. The node does not have a block at that height any more.
		await env.RpcClient.InvalidateBlockAsync(blockToInvalidate);

		// The wallet shows what the node has: the transaction is unconfirmed again.
		await env.WaitForConditionAsync(
			() => wallet.Coins.All(c => !c.Confirmed),
			TimeSpan.FromSeconds(30));

		Assert.False(Assert.Single(wallet.Coins).Confirmed);

		// Another block confirms the transaction again: Bitcoin Core puts the transactions of the dropped block back into its mempool.
		await env.RpcClient.GenerateAsync(1);

		await env.WaitForConditionAsync(
			() => wallet.Coins.All(c => c.Confirmed),
			TimeSpan.FromSeconds(30));

		coin = Assert.Single(wallet.Coins);
		Assert.Equal(Money.Coins(1m), coin.Amount);
		Assert.NotNull(coin.Transaction.BlockHash);
		Assert.NotEqual(blockToInvalidate, coin.Transaction.BlockHash);
		Assert.Equal(await env.RpcClient.GetBlockHashAsync(tipHeight), coin.Transaction.BlockHash);

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task Reorg_DeepReorg_HandledGracefullyAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		// Mine initial blocks
		await env.RpcClient.GenerateAsync(10);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		await env.WaitForWalletToCatchUpAsync(wallet);

		var (initialHeight, initialBlockHashes) = keyManager.GetProcessedBlocks();
		Assert.NotNull(initialHeight);

		// Get a block 5 blocks back to invalidate
		var reorgDepth = 5;
		var heightToInvalidate = (int)initialHeight.Height - reorgDepth + 1;
		var blockToInvalidate = await env.RpcClient.GetBlockHashAsync(heightToInvalidate);

		// Act - Create a deep reorg
		await env.RpcClient.InvalidateBlockAsync(blockToInvalidate);

		// Mine more blocks than we invalidated to create a longer chain
		await env.RpcClient.GenerateAsync(reorgDepth + 2);

		await env.WaitForWalletToCatchUpAsync(wallet);

		// Assert
		var (newHeight, newBlockHashes) = keyManager.GetProcessedBlocks();
		Assert.NotNull(newHeight);

		// The new chain is longer
		Assert.Equal(initialHeight.Height + 2, newHeight.Height);

		// None of the dropped blocks is left among the processed blocks, and the wallet has the blocks of the node.
		var droppedBlockHashes = initialBlockHashes.TakeLast(reorgDepth).ToArray();
		Assert.Empty(newBlockHashes.Intersect(droppedBlockHashes));

		for (int i = 0; i < reorgDepth + 2; i++)
		{
			var height = (int)newHeight.Height - i;
			Assert.Equal(await env.RpcClient.GetBlockHashAsync(height), newBlockHashes[^(i + 1)]);
		}

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact(Timeout = 180_000)] // 3 minute timeout
	public async Task Reorg_DeepReorg_WalletUtxoSetCorrectlyUpdatedAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		// Mine some initial blocks to have a stable base
		await env.RpcClient.GenerateAsync(10);
		var baseHeight = await env.RpcClient.GetBlockCountAsync();

		// Fund the wallet with coins in separate blocks (5 coins, one per block)
		const int NumberOfCoins = 5;
		var originalBlockHashes = new List<uint256>();

		for (int i = 0; i < NumberOfCoins; i++)
		{
			var receiveKey = keyManager.GetNextReceiveKey();
			var address = receiveKey.GetAddress(env.Network);

			// Send coins and mine a block to confirm each one
			await env.RpcClient.SendToAddressAsync(address, Money.Coins(1m));
			var blockHashes = await env.RpcClient.GenerateAsync(1);
			originalBlockHashes.Add(blockHashes[0]);
		}

		// Start the wallet: it synchronizes with the node
		await wallet.StartAsync(TestContext.Current.CancellationToken);

		// Wait for all coins to be discovered and confirmed
		await env.WaitForConditionAsync(
			() => wallet.Coins.Count() >= NumberOfCoins && wallet.Coins.All(c => c.Confirmed),
			TimeSpan.FromSeconds(60));

		// Verify pre-reorg state: wallet has exactly N confirmed coins
		Assert.Equal(NumberOfCoins, wallet.Coins.Count());
		Assert.All(wallet.Coins, c => Assert.True(c.Confirmed, "All coins should be confirmed before reorg"));

		var preReorgTotalBalance = wallet.Coins.Sum(c => c.Amount.Satoshi);
		Assert.Equal(Money.Coins(NumberOfCoins).Satoshi, preReorgTotalBalance);

		// Record the original block hashes where each coin was confirmed
		var preReorgCoinBlockHashes = wallet.Coins
			.ToDictionary(c => c.Outpoint, c => c.Transaction.BlockHash);

		// Get the first block to invalidate (the one containing our first funding tx)
		var blockToInvalidate = originalBlockHashes[0];

		// Act - Create a deep reorg that invalidates ALL funding blocks
		await env.RpcClient.InvalidateBlockAsync(blockToInvalidate);

		// Mine new blocks - these will include the funding transactions from the mempool
		// (Bitcoin Core puts reorged transactions back in the mempool)
		await env.RpcClient.GenerateAsync(NumberOfCoins + 3);

		// Wait for the wallet to follow the node through the reorg
		await env.WaitForWalletToCatchUpAsync(wallet);

		// The coins will either:
		// 1. Be re-confirmed at different block hashes (if re-mined), OR
		// 2. Remain unconfirmed (if not re-mined)
		await env.WaitForConditionAsync(
			() =>
			{
				// Check that the wallet has processed the reorg by verifying
				// that coins are no longer in the orphaned blocks
				return wallet.Coins.All(c =>
				{
					if (!c.Confirmed)
					{
						// Coin is unconfirmed - reorg was processed
						return true;
					}
					// Coin is confirmed - verify it's NOT in an orphaned block
					return c.Transaction.BlockHash is not null &&
						   !originalBlockHashes.Contains(c.Transaction.BlockHash);
				});
			},
			TimeSpan.FromSeconds(60));

		// Verify the new chain is longer and different
		var newHeight = keyManager.GetBestHeight();
		Assert.NotNull(newHeight);
		Assert.True(newHeight.Height > baseHeight + NumberOfCoins);

		// Verify that the chain at the first funding block height is now different
		// (proving the reorg happened and created a new chain)
		var newBlockAtFundingHeight = await env.RpcClient.GetBlockHashAsync(baseHeight + 1);
		Assert.NotEqual(originalBlockHashes[0], newBlockAtFundingHeight);

		// Verify all coins are no longer in their ORIGINAL blocks (they were reorged out)
		foreach (var coin in wallet.Coins)
		{
			var originalBlockHash = preReorgCoinBlockHashes[coin.Outpoint];

			if (coin.Confirmed)
			{
				// If re-confirmed, must be in a DIFFERENT block
				Assert.NotNull(coin.Transaction.BlockHash);
				Assert.NotEqual(originalBlockHash, coin.Transaction.BlockHash);
			}
			// If unconfirmed, that's also valid - the transaction wasn't re-mined
		}

		// The wallet should still have all the coins (either confirmed or unconfirmed)
		Assert.Equal(NumberOfCoins, wallet.Coins.Count());

		// Total balance should be preserved (coins exist, just in different state)
		var totalBalance = wallet.Coins.Sum(c => c.Amount.Satoshi);
		Assert.Equal(Money.Coins(NumberOfCoins).Satoshi, totalBalance);

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}
}
