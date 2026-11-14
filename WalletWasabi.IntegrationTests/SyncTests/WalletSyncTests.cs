using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.IntegrationTests.SyncTests;

/// <summary>
/// Integration tests for the synchronization of the wallet with a Bitcoin Core node.
/// The wallet learns about blocks and unconfirmed transactions from the RPC interface of the node and from nowhere else.
/// </summary>
[Collection("Integration tests")]
public class WalletSyncTests
{
	private readonly IntegrationTestFixture _fixture;

	public WalletSyncTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task WalletSync_NewBlocksMined_WalletFollowsAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);

		// Act - Starting the wallet synchronizes it with the node
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		await env.WaitForWalletToCatchUpAsync(wallet);

		var initialHeight = keyManager.GetBestHeight();
		Assert.NotNull(initialHeight);
		Assert.Equal((uint)await env.RpcClient.GetBlockCountAsync(), initialHeight.Height);

		// Mine multiple blocks
		int blocksToMine = 10;
		await env.RpcClient.GenerateAsync(blocksToMine);
		await env.WaitForWalletToCatchUpAsync(wallet);

		// Assert - The wallet advanced with the node and remembers the blocks of the node
		var (newHeight, blockHashes) = keyManager.GetProcessedBlocks();
		Assert.NotNull(newHeight);
		Assert.Equal(initialHeight.Height + (uint)blocksToMine, newHeight.Height);
		Assert.Equal(await env.RpcClient.GetBestBlockHashAsync(), blockHashes[^1]);

		await env.WaitForConditionAsync(
			() => wallet.Synchronizer.Status is WalletSyncStatus.Synchronized,
			TimeSpan.FromSeconds(30));

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task WalletSync_UnconfirmedFunds_NoticedInTheMempoolOfTheNodeAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		await wallet.StartAsync(TestContext.Current.CancellationToken);

		// Act - The node receives a transaction that pays to the wallet. Nobody tells the wallet about it.
		var receiveKey = keyManager.GetNextReceiveKey();
		var txid = await env.FundAddressAsync(receiveKey.GetAddress(env.Network), Money.Coins(0.7m), confirmations: 0);

		await env.WaitForConditionAsync(
			() => wallet.Coins.Any(),
			TimeSpan.FromSeconds(30));

		// Assert - The wallet found it in the mempool of the node
		var coin = Assert.Single(wallet.Coins);
		Assert.Equal(txid, coin.TransactionId);
		Assert.Equal(Money.Coins(0.7m), coin.Amount);
		Assert.False(coin.Confirmed);

		// A block confirms it
		await env.RpcClient.GenerateAsync(1);

		await env.WaitForConditionAsync(
			() => wallet.Coins.All(c => c.Confirmed),
			TimeSpan.FromSeconds(30));

		coin = Assert.Single(wallet.Coins);
		Assert.Equal(await env.RpcClient.GetBestBlockHashAsync(), coin.Transaction.BlockHash);

		await wallet.StopAsync(TestContext.Current.CancellationToken);
	}

	[Fact(Timeout = 120_000)] // 2 minute timeout
	public async Task WalletSync_WalletStartedAgain_ContinuesWhereItStoppedAsync()
	{
		// Arrange
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);

		var keyManager = env.CreateKeyManager();
		keyManager.SetFilePath(Path.Combine(env.WorkDir, "Wallet.json"));
		keyManager.ToFile();

		var wallet = env.CreateWallet(keyManager);

		var firstKey = keyManager.GetNextReceiveKey();
		var secondKey = keyManager.GetNextReceiveKey();
		await env.FundAddressAsync(firstKey.GetAddress(env.Network), Money.Coins(1m), confirmations: 1);

		await wallet.StartAsync(TestContext.Current.CancellationToken);
		await env.WaitForConditionAsync(() => wallet.Coins.Any(c => c.Confirmed), TimeSpan.FromSeconds(30));
		await env.WaitForWalletToCatchUpAsync(wallet);
		await wallet.StopAsync(TestContext.Current.CancellationToken);

		var heightWhenStopped = keyManager.GetBestHeight();
		Assert.NotNull(heightWhenStopped);

		// Act - The chain goes on while the wallet is not running
		await env.RpcClient.GenerateAsync(3);
		await env.FundAddressAsync(secondKey.GetAddress(env.Network), Money.Coins(2m), confirmations: 1);
		await env.RpcClient.GenerateAsync(2);

		// The wallet starts again with what its file tells
		var reloadedKeyManager = KeyManager.FromFile(keyManager.FilePath!);
		Assert.Equal(heightWhenStopped, reloadedKeyManager.GetBestHeight());

		var restartedWallet = env.CreateWallet(reloadedKeyManager);
		await restartedWallet.StartAsync(TestContext.Current.CancellationToken);

		// Assert - Starting it processed the blocks that it missed
		Assert.Equal(heightWhenStopped.Height + 6, reloadedKeyManager.GetBestHeight()?.Height);
		Assert.Equal(2, restartedWallet.Coins.Count());
		Assert.All(restartedWallet.Coins, c => Assert.True(c.Confirmed));
		Assert.Equal(Money.Coins(3m), restartedWallet.Coins.TotalAmount());

		await restartedWallet.StopAsync(TestContext.Current.CancellationToken);
	}
}
