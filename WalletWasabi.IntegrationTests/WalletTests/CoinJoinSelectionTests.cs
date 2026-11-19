using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Threading;
using NBitcoin;
using NBitcoin.DataEncoders;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.IntegrationTests.Infrastructure;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// The user decides coin by coin what is submitted into coinjoin rounds. No coin is chosen before the user chooses it.
/// The wallet chooses the coins that it is told to choose, takes out the ones that it is told to take out, leaves every
/// other coin as it is, and remembers the choices in its file.
/// </summary>
[Collection("Integration tests")]
public class CoinJoinSelectionTests
{
	private readonly IntegrationTestFixture _fixture;

	public CoinJoinSelectionTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)]
	public async Task Wallet_ChoosesTheNamedCoinsForCoinJoinsAndLeavesTheOthersAlone()
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

			// Nothing is chosen to begin with.
			Assert.DoesNotContain(coins, coin => coin.IsSelectedForCoinJoin);
			Assert.Empty(keyManager.CoinsSelectedForCoinJoin);

			// One coin is chosen. The others were not named and stay out.
			wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [small.Outpoint] = true });

			Assert.True(small.IsSelectedForCoinJoin);
			Assert.False(medium.IsSelectedForCoinJoin);
			Assert.False(large.IsSelectedForCoinJoin);
			Assert.Equal([small.Outpoint], keyManager.CoinsSelectedForCoinJoin);

			// Several coins at once, one taken out again and one chosen. The third one is not named and stays in, as it was put by itself.
			wallet.SelectCoinForCoinJoin(large.Outpoint);
			wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [small.Outpoint] = false, [medium.Outpoint] = true });

			Assert.False(small.IsSelectedForCoinJoin);
			Assert.True(medium.IsSelectedForCoinJoin);
			Assert.True(large.IsSelectedForCoinJoin);
			Assert.Equal(
				new[] { medium.Outpoint, large.Outpoint }.OrderBy(x => x.ToString()),
				keyManager.CoinsSelectedForCoinJoin.OrderBy(x => x.ToString()));

			// A coin that the wallet does not have is nothing to it: a coin can be spent between the tick and the telling.
			wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [new OutPoint(uint256.One, 7)] = true });
			Assert.Throws<InvalidOperationException>(() => wallet.SelectCoinForCoinJoin(new OutPoint(uint256.One, 7)));

			Assert.Equal(2, keyManager.CoinsSelectedForCoinJoin.Count);

			// Naming no coin changes nothing.
			wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool>());

			Assert.Equal(2, keyManager.CoinsSelectedForCoinJoin.Count);
			Assert.False(small.IsSelectedForCoinJoin);

			// A coin that arrives later is not chosen either, and it does not change what was chosen.
			await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.4m), confirmations: 0);
			await env.WaitForConditionAsync(() => wallet.Coins.Count() == 4, TimeSpan.FromSeconds(30));

			var arrived = Assert.Single(wallet.Coins, coin => coin.Amount == Money.Coins(0.4m));
			Assert.False(arrived.IsSelectedForCoinJoin);
			Assert.Equal(2, wallet.Coins.Count(coin => coin.IsSelectedForCoinJoin));
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// The choices are in the wallet file: a wallet that is opened again has the same coins chosen, and has forgotten
	/// the choice of a coin that it does not have any more.
	/// </summary>
	[Fact(Timeout = 120_000)]
	public async Task Wallet_RemembersTheChosenCoinsWhenItIsOpenedAgain()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();
		keyManager.SetFilePath(Path.Combine(env.WorkDir, "Wallet.json"));
		keyManager.ToFile();

		var wallet = env.CreateWallet(keyManager);
		wallet.Password = RegTestEnvironment.DefaultPassword;

		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.1m), confirmations: 1);
		await env.FundAddressAsync(keyManager.GetNextReceiveKey().GetAddress(env.Network), Money.Coins(0.2m), confirmations: 1);

		OutPoint chosen;

		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			await env.WaitForConditionAsync(() => wallet.Coins.Count() == 2, TimeSpan.FromSeconds(30));
			await env.WaitForWalletToCatchUpAsync(wallet);

			chosen = wallet.Coins.Single(coin => coin.Amount == Money.Coins(0.2m)).Outpoint;
			wallet.SelectCoinForCoinJoin(chosen);
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}

		// The file also names a coin that the wallet does not have: it was spent, and the wallet was not told to take it out.
		var gone = new OutPoint(uint256.One, 7);
		var json = JsonNode.Parse(File.ReadAllText(keyManager.FilePath!))!;
		json["CoinsSelectedForCoinJoin"]!.AsArray().Add(Encoders.Hex.EncodeData(gone.ToBytes()));
		File.WriteAllText(keyManager.FilePath!, json.ToJsonString());

		var reopenedKeyManager = KeyManager.FromFile(keyManager.FilePath!);
		Assert.Equal(new[] { chosen, gone }.OrderBy(x => x.ToString()), reopenedKeyManager.CoinsSelectedForCoinJoin.OrderBy(x => x.ToString()));

		var reopenedWallet = env.CreateWallet(reopenedKeyManager);
		reopenedWallet.Password = RegTestEnvironment.DefaultPassword;

		await reopenedWallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			Assert.Equal(2, reopenedWallet.Coins.Count());
			Assert.True(reopenedWallet.Coins.Single(coin => coin.Outpoint == chosen).IsSelectedForCoinJoin);
			Assert.False(reopenedWallet.Coins.Single(coin => coin.Outpoint != chosen).IsSelectedForCoinJoin);

			// The choice whose coin is gone is forgotten, in the file as well.
			Assert.Equal([chosen], reopenedKeyManager.CoinsSelectedForCoinJoin);
			Assert.Equal([chosen], KeyManager.FromFile(keyManager.FilePath!).CoinsSelectedForCoinJoin);
		}
		finally
		{
			await reopenedWallet.StopAsync(CancellationToken.None);
		}
	}
}
