using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Services;
using WalletWasabi.Tests.UnitTests.WabiSabi.Integration;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.ZeroLink;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ZeroLink;

/// <summary>
/// Whole ZeroLink rounds: wallets with their coinjoin clients, and a coordinator that they reach through its interface.
/// </summary>
/// <seealso cref="XunitConfiguration.SerialCollectionDefinition"/>
[Collection("Serial unit tests collection")]
public class ZeroLinkRoundTests : IClassFixture<WabiSabiApiApplicationFactory<Startup>>
{
	private static readonly Money Denomination = Money.Coins(0.1m);

	private readonly WabiSabiApiApplicationFactory<Startup> _apiApplicationFactory;

	public ZeroLinkRoundTests(WabiSabiApiApplicationFactory<Startup> apiApplicationFactory)
	{
		_apiApplicationFactory = apiApplicationFactory;
	}

	/// <summary>
	/// Three wallets, each with a coin of 0.1 BTC and another coin that it chose too. The transaction of the round has
	/// the three coins of 0.1 BTC and three outputs of 0.1 BTC less the fee, and nothing else.
	/// </summary>
	[Fact]
	public async Task RoundHasOneInputAndOneOutputOfEveryWalletAsync()
	{
		const int Participants = 3;
		var transactionCompleted = new TaskCompletionSource<Transaction>();

		var wallets = Enumerable.Range(0, Participants).Select(i => KeyManager.CreateNew(out _, password: "", Network.Main)).ToArray();
		var coinsOfWallets = wallets.Select(wallet => CreateCoins(wallet, Denomination, Money.Coins(0.37m))).ToArray();

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(
				coinsOfWallets.SelectMany(x => x).ToImmutableList(),
				rpc => rpc.OnSendRawTransactionAsync = tx =>
				{
					transactionCompleted.SetResult(tx);
					return tx.GetHash();
				})
			.ConfigureServices(services =>
				// The registration keeps its default length and ends as soon as the three wallets registered.
				services.AddSingleton(s => new WabiSabiConfig
				{
					MaxInputCountByRound = Participants,
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20), // This phase always lasts its whole time frame.
					ZeroLinkDenominations = [Denomination]
				})));

		using var httpClient = coordinatorApp.CreateClient();
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		cts.Token.Register(() => transactionCompleted.TrySetCanceled(), useSynchronizationContext: false);

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		// What a participant pays follows from the fee rate that the coordinator gave the round.
		var feeRate = (await GetZeroLinkRoundsAsync(apiClient, count: 1, cts.Token)).Single().CoinjoinState.Parameters.MiningFeeRate;
		var participantFee = ZeroLinkRules.GetFee(feeRate);

		var results = await Task.WhenAll(wallets.Select((wallet, i) =>
			WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, wallet, roundStateProvider).StartZeroLinkAsync(() => coinsOfWallets[i], cts.Token)));

		var transaction = await transactionCompleted.Task;

		// Every wallet had its coin of the denomination in the round, and no other coin.
		var denominationCoins = coinsOfWallets.Select(coins => coins.Single(coin => coin.Amount == Denomination)).ToArray();
		Assert.Equal(
			denominationCoins.Select(coin => coin.Outpoint).OrderBy(x => x.ToString()),
			transaction.Inputs.Select(input => input.PrevOut).OrderBy(x => x.ToString()));

		// Every output holds the denomination less the fee, and there is one for every input: no change, and no output of the coordinator.
		Assert.Equal(Participants, transaction.Outputs.Count);
		Assert.All(transaction.Outputs, output => Assert.Equal(Denomination - participantFee, output.Value));
		Assert.All(transaction.Outputs, output => Assert.True(output.ScriptPubKey.IsScriptType(ScriptType.Taproot)));

		// Every input is spent the same way, by one signature of 64 bytes, so the transaction has the size that the rules count.
		Assert.All(transaction.Inputs, input => Assert.True(ZeroLinkRules.IsInputWitness(input.WitScript)));
		Assert.Equal(ZeroLinkRules.GetTransactionVsize(Participants, Participants), transaction.GetVirtualSize());

		// The transaction pays the fee rate of the round and a little more: every wallet paid for the overhead of a transaction of its own.
		var fee = (Participants * Denomination.Satoshi) - transaction.Outputs.Sum(output => output.Value.Satoshi);
		Assert.Equal(Participants * participantFee.Satoshi, fee);
		Assert.True(fee * 1000 >= feeRate.FeePerK.Satoshi * ZeroLinkRules.GetTransactionVsize(Participants, Participants));

		for (var i = 0; i < Participants; i++)
		{
			var success = Assert.IsType<SuccessfulCoinJoinResult>(results[i]);

			Assert.Equal(denominationCoins[i].Outpoint, Assert.Single(success.Coins).Outpoint);

			// The output of the wallet is on an address of its own, one that it took for this round.
			var script = Assert.Single(success.OutputScripts);
			Assert.Single(transaction.Outputs, output => output.ScriptPubKey == script);
			Assert.True(wallets[i].TryGetKeyForScriptPubKey(script, out var key));
			Assert.True(key.IsInternal);
		}
	}

	/// <summary>A wallet without a coin of a standard denomination among the chosen ones is told so at once.</summary>
	[Fact]
	public async Task WalletWithoutACoinOfAStandardDenominationHasNoRoundAsync()
	{
		var wallet = KeyManager.CreateNew(out _, password: "", Network.Main);
		var coins = CreateCoins(wallet, Money.Coins(0.37m), Money.Satoshis(9_999_445));

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(coins, _ => { })
			.ConfigureServices(services => services.AddSingleton(s => new WabiSabiConfig { ZeroLinkDenominations = [Denomination] })));

		using var httpClient = coordinatorApp.CreateClient();
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, wallet, new RoundStateProvider(roundStateUpdater));

		var exception = await Assert.ThrowsAsync<CoinJoinClientException>(() => coinJoinClient.StartZeroLinkAsync(() => coins, cts.Token));

		Assert.Equal(CoinjoinError.NoStandardDenominationCoins, exception.CoinjoinError);
	}

	/// <summary>
	/// A coinjoin of the other kind, which takes any amounts, leaves the ZeroLink rounds of the coordinator alone, also
	/// with a coin that a ZeroLink round would take.
	/// </summary>
	[Fact]
	public async Task CoinJoinOfAnyAmountsLeavesTheZeroLinkRoundsAloneAsync()
	{
		var transactionCompleted = new TaskCompletionSource<Transaction>();

		var wallet = KeyManager.CreateNew(out _, password: "", Network.Main);
		var coins = CreateCoins(wallet, Denomination, Money.Coins(0.2m), Money.Coins(0.37m));

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(
				coins,
				rpc => rpc.OnSendRawTransactionAsync = tx =>
				{
					transactionCompleted.SetResult(tx);
					return tx.GetHash();
				})
			.ConfigureServices(services =>
				services.AddSingleton(s => new WabiSabiConfig
				{
					MaxInputCountByRound = coins.Count,
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20),
					ZeroLinkDenominations = [Denomination, Money.Coins(0.2m)]
				})));

		using var httpClient = coordinatorApp.CreateClient();
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		cts.Token.Register(() => transactionCompleted.TrySetCanceled(), useSynchronizationContext: false);

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, wallet, new RoundStateProvider(roundStateUpdater));

		// The coordinator has both ZeroLink rounds next to the round that the coinjoin goes into.
		await GetZeroLinkRoundsAsync(apiClient, count: 2, cts.Token);

		Assert.IsType<SuccessfulCoinJoinResult>(await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token));

		var transaction = await transactionCompleted.Task;
		Assert.Equal(coins.Count, transaction.Inputs.Count);
	}

	/// <summary>Waits until the coordinator has the given number of ZeroLink rounds, and tells them.</summary>
	private static async Task<RoundState[]> GetZeroLinkRoundsAsync(IWabiSabiApiRequestHandler apiClient, int count, CancellationToken cancellationToken)
	{
		while (true)
		{
			var roundStates = (await apiClient.GetStatusAsync(RoundStateRequest.Empty, cancellationToken)).RoundStates
				.Where(x => ZeroLinkRules.IsZeroLinkRound(x.CoinjoinState.Parameters))
				.ToArray();

			if (roundStates.Length >= count)
			{
				return roundStates;
			}

			await Task.Delay(100, cancellationToken);
		}
	}

	/// <summary>Coins of a wallet on receive addresses of it, one of each amount.</summary>
	private static ImmutableList<SmartCoin> CreateCoins(KeyManager wallet, params Money[] amounts) =>
		wallet.GetKeys(key => !key.IsInternal)
			.Take(amounts.Length)
			.Select((key, i) => BitcoinFactory.CreateSmartCoin(key, amounts[i]))
			.ToImmutableList();
}
