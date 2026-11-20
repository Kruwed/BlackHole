using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.IntegrationTests.Infrastructure;
using WalletWasabi.Models;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.IntegrationTests.WalletTests;

/// <summary>
/// A coinjoin round gets addresses that no round had, and a round that fails leaves its addresses unused. However many
/// rounds fail, the wallet stays within its gap limit, so that a wallet that is recovered from the backup finds what was
/// paid afterwards.
/// </summary>
[Collection("Integration tests")]
public class CoinJoinAddressTests
{
	private readonly IntegrationTestFixture _fixture;

	public CoinJoinAddressTests(IntegrationTestFixture fixture)
	{
		_fixture = fixture;
	}

	[Fact(Timeout = 120_000)]
	public async Task RecoveredWallet_FindsWhatWasPaidAfterManyRoundsFailed()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var tip = (uint)await env.RpcClient.GetBlockCountAsync();

		var lost = KeyManager.CreateNew(out var shares, RegTestEnvironment.DefaultPassword, env.Network);

		// Thirty rounds fail one after the other, each with ten outputs of the wallet. That is more addresses than the wallet
		// has within its gap limit.
		for (var round = 0; round < 30; round++)
		{
			lost.GetNextCoinJoinKeys(10);
		}

		// Then a round is broadcast. It pays to the addresses that it got.
		var paid = lost.GetNextCoinJoinKeys(10);
		foreach (var key in paid)
		{
			await env.FundAddressAsync(key.GetAddress(env.Network), Money.Coins(0.01m), confirmations: 0);
		}
		lost.MarkAsPaid(paid.Select(key => key.P2Taproot));
		await env.RpcClient.GenerateAsync(1);

		// A payment follows, whose change goes to the next change address. The wallet learns from the transaction that the
		// address was paid to.
		var changeKey = lost.GetNextChangeKey();
		await env.FundAddressAsync(changeKey.GetAddress(env.Network), Money.Coins(0.02m), confirmations: 1);
		lost.SetKeyState(KeyState.Used, changeKey);

		// And another round is broadcast.
		var laterKey = lost.GetNextCoinJoinKeys(1).Single();
		await env.FundAddressAsync(laterKey.GetAddress(env.Network), Money.Coins(0.03m), confirmations: 1);
		Assert.Equal([210, 211], new[] { changeKey.Index, laterKey.Index });

		// The wallet file is lost. The backup brings the keys back, and the wallet looks for its coins with its gap limit.
		var recovered = KeyManager.Recover(
			shares.Take(WalletGenerator.DefaultShamirThreshold).ToArray(),
			RegTestEnvironment.DefaultPassword,
			env.Network,
			birthHeight: new Height.ChainHeight(tip));
		Assert.Equal(KeyManager.AbsoluteMinGapLimit, recovered.MinGapLimit);

		var wallet = env.CreateWallet(recovered);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			await env.WaitForWalletToCatchUpAsync(wallet, TimeSpan.FromSeconds(60));

			Assert.Equal(12, wallet.Coins.Count());
			Assert.Equal(Money.Coins(0.15m), wallet.Coins.TotalAmount());
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// The wallet counts its gap limit from the last change address that was paid to. A wallet file may call addresses paid
	/// to that nothing was paid to: the version before this one did so with the addresses of every round. The wallet puts
	/// that right when it has its transactions.
	/// </summary>
	[Fact(Timeout = 120_000)]
	public async Task Wallet_CountsAChangeAddressAsPaidToOnlyIfATransactionPaysToIt()
	{
		await using var env = await RegTestEnvironment.CreateAsync(_fixture);
		var keyManager = env.CreateKeyManager();

		var changeKeys = keyManager.GetKeys(KeyState.Clean, isInternal: true).Take(12).ToArray();

		// The second of twelve change addresses is paid to. The wallet file calls all of them used.
		await env.FundAddressAsync(changeKeys[1].GetAddress(env.Network), Money.Coins(0.1m), confirmations: 1);
		foreach (var key in changeKeys)
		{
			keyManager.SetKeyState(KeyState.Used, key);
		}

		var wallet = env.CreateWallet(keyManager);
		await wallet.StartAsync(TestContext.Current.CancellationToken);
		try
		{
			await env.WaitForConditionAsync(() => wallet.Coins.Count() == 1, TimeSpan.FromSeconds(30));

			Assert.Equal(KeyState.Used, changeKeys[1].KeyState);
			Assert.All(changeKeys.Where(key => key.Index != 1), key => Assert.Equal(KeyState.Locked, key.KeyState));

			// None of them is given out again, and the next change address is the one after them.
			Assert.Equal(12, keyManager.GetNextChangeKey().Index);
			Assert.Equal([12, 13], keyManager.GetNextCoinJoinKeys(2).Select(key => key.Index));
		}
		finally
		{
			await wallet.StopAsync(CancellationToken.None);
		}
	}
}
