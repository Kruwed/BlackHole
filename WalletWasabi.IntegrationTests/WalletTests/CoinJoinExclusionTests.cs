using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.IntegrationTests.Infrastructure;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// The user decides coin by coin what may be submitted into coinjoin rounds. The wallet keeps out the coins that it is
/// told to keep out, lets in the ones that it is told to let in, and leaves every other coin as it is.
/// </summary>
[Collection("Integration tests")]
public class CoinJoinExclusionTests
{
	private readonly IntegrationTestFixture _fixture;

	public CoinJoinExclusionTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)]
	public async Task Wallet_KeepsTheNamedCoinsOutOfCoinJoinsAndLeavesTheOthersAlone()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.1m), confirmations: 0);
		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.2m), confirmations: 0);
		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.3m), confirmations: 1);

		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			await env.WaitForConditionAsync(() => wallet.Coins.Count() == 3, TimeSpan.FromSeconds(30));

			var coins = wallet.Coins.OrderBy(coin => coin.Amount).ToArray();
			var (small, medium, large) = (coins[0], coins[1], coins[2]);

			// Nothing is kept out to begin with.
			Assert.DoesNotContain(coins, coin => coin.IsExcludedFromCoinJoin);
			Assert.Empty(keyManager.ExcludedCoinsFromCoinJoin);

			// One coin is kept out. The others were not named and stay in.
			wallet.ExcludeCoinsFromCoinJoin(new Dictionary<OutPoint, bool> { [small.Outpoint] = true });

			Assert.True(small.IsExcludedFromCoinJoin);
			Assert.False(medium.IsExcludedFromCoinJoin);
			Assert.False(large.IsExcludedFromCoinJoin);
			Assert.Equal([small.Outpoint], keyManager.ExcludedCoinsFromCoinJoin);

			// Several coins at once, one let in again and one kept out. The third one is not named and stays out, as it was put by itself.
			wallet.ExcludeCoinFromCoinJoin(large.Outpoint);
			wallet.ExcludeCoinsFromCoinJoin(new Dictionary<OutPoint, bool> { [small.Outpoint] = false, [medium.Outpoint] = true });

			Assert.False(small.IsExcludedFromCoinJoin);
			Assert.True(medium.IsExcludedFromCoinJoin);
			Assert.True(large.IsExcludedFromCoinJoin);
			Assert.Equal(
				new[] { medium.Outpoint, large.Outpoint }.OrderBy(x => x.ToString()),
				keyManager.ExcludedCoinsFromCoinJoin.OrderBy(x => x.ToString()));

			// A coin that the wallet does not have is nothing to it: a coin can be spent between the tick and the telling.
			wallet.ExcludeCoinsFromCoinJoin(new Dictionary<OutPoint, bool> { [new OutPoint(uint256.One, 7)] = true });

			Assert.Equal(2, keyManager.ExcludedCoinsFromCoinJoin.Count);

			// Naming no coin changes nothing.
			wallet.ExcludeCoinsFromCoinJoin(new Dictionary<OutPoint, bool>());

			Assert.Equal(2, keyManager.ExcludedCoinsFromCoinJoin.Count);
			Assert.False(small.IsExcludedFromCoinJoin);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}
}
