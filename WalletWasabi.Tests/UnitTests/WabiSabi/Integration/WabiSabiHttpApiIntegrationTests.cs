using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NBitcoin.RPC;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Tests.UnitTests.Services;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Coordinator;
using WalletWasabi.WabiSabi.Coordinator.Models;
using WalletWasabi.WabiSabi.Coordinator.Rounds;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.MultipartyTransaction;
using Xunit;
using static WalletWasabi.WabiSabi.Client.CoinJoin.Client.CoinJoinClient;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Integration;

/// <seealso cref="XunitConfiguration.SerialCollectionDefinition"/>
[Collection("Serial unit tests collection")]
public class WabiSabiHttpApiIntegrationTests : IClassFixture<WabiSabiApiApplicationFactory<Startup>>
{
	private readonly WabiSabiApiApplicationFactory<Startup> _apiApplicationFactory;
	private readonly ITestOutputHelper _output;

	public WabiSabiHttpApiIntegrationTests(WabiSabiApiApplicationFactory<Startup> apiApplicationFactory, ITestOutputHelper output)
	{
		_apiApplicationFactory = apiApplicationFactory;
		_output = output;
	}

	[Fact]
	public async Task RegisterSpentOrInNonExistentCoinAsync()
	{
		var httpClient = _apiApplicationFactory.CreateClient();
		await Task.Delay(100);
		var apiClient = await _apiApplicationFactory.CreateArenaClientAsync(httpClient);
		var rounds = (await apiClient.GetStatusAsync(RoundStateRequest.Empty, CancellationToken.None)).RoundStates;
		var round = rounds.First(x => x.CoinjoinState is ConstructionState);

		// If an output is not in the UTXO dataset then it is not unspent, this
		// means that the output is spent or simply doesn't even exist.
		var nonExistingOutPoint = new OutPoint();
		using var signingKey = new Key();
		var ownershipProof = WabiSabiFactory.CreateOwnershipProof(signingKey, round.Id);

		var ex = await Assert.ThrowsAsync<WabiSabiProtocolException>(async () =>
		   await apiClient.RegisterInputAsync(round.Id, nonExistingOutPoint, ownershipProof, CancellationToken.None));

		Assert.Equal(WabiSabiProtocolErrorCode.InputSpent, ex.ErrorCode);
	}

	[Fact]
	public async Task RegisterBannedCoinAsync()
	{
		using CancellationTokenSource timeoutCts = new(TimeSpan.FromMinutes(2));

		using var signingKey = new Key();
		var coin = WabiSabiFactory.CreateCoin(signingKey);
		var bannedOutPoint = coin.Outpoint;

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.ConfigureServices(services =>
			{
				var rpc = BitcoinFactory.GetMockMinimalRpc();

				// Make the coordinator believe that the coins are real and
				// that they exist in the blockchain with many confirmations.
				rpc.OnGetTxOutAsync = (_, _, _) => new()
				{
					Confirmations = 101,
					IsCoinBase = false,
					ScriptPubKeyType = "witness_v0_keyhash",
					TxOut = coin.TxOut
				};
				services.AddSingleton<IRPCClient>(s => rpc);

				var prison = WabiSabiFactory.CreatePrison();
				prison.FailedVerification(bannedOutPoint, uint256.One);
				services.AddSingleton(_ => prison);
			}));

		using var httpClient = coordinatorApp.CreateClient();

		await Task.Delay(100);
		var apiClient = await _apiApplicationFactory.CreateArenaClientAsync(httpClient);
		var rounds = (await apiClient.GetStatusAsync(RoundStateRequest.Empty, timeoutCts.Token)).RoundStates;
		var round = rounds.First(x => x.CoinjoinState is ConstructionState);

		// If an output is not in the UTXO dataset then it is not unspent, this
		// means that the output is spent or simply doesn't even exist.
		var ownershipProof = WabiSabiFactory.CreateOwnershipProof(signingKey, round.Id);

		var ex = await Assert.ThrowsAsync<WabiSabiProtocolException>(async () =>
			await apiClient.RegisterInputAsync(round.Id, bannedOutPoint, ownershipProof, timeoutCts.Token));

		Assert.Equal(WabiSabiProtocolErrorCode.InputBanned, ex.ErrorCode);
		var inputBannedData = Assert.IsType<InputBannedExceptionData>(ex.ExceptionData);
		Assert.True(inputBannedData.BannedUntil > DateTimeOffset.UtcNow);
	}

	[Theory]
	[InlineData(new long[] { 10_000_000, 20_000_000, 30_000_000, 40_000_000, 100_000_000 })]
	public async Task SoloCoinJoinTestAsync(long[] amounts)
	{
		int inputCount = amounts.Length;

		// At the end of the test a coinjoin transaction has to be created and broadcasted.
		var transactionCompleted = new TaskCompletionSource<Transaction>();

		// Create a key manager and use it to create fake coins.
		_output.WriteLine("Creating key manager...");
		KeyManager keyManager = KeyManager.CreateNew(out _, password: "", Network.Main);

		var coins = GenerateSmartCoins(keyManager, amounts, inputCount);

		_output.WriteLine("Coins were created successfully");

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(coins,
				rpc =>

					// Make the coordinator believe that the transaction is being
					// broadcasted using the RPC interface. Once we receive this tx
					// (the `SendRawTransactionAsync` was invoked) we stop waiting
					// and finish the waiting tasks to finish the test successfully.
					rpc.OnSendRawTransactionAsync = (tx) =>
					{
						transactionCompleted.SetResult(tx);
						return tx.GetHash();
					})
			.ConfigureServices(services =>
				// Instruct the coordinator DI container to use these two scoped
				// services to build everything (WabiSabi controller, arena, etc)
				services.AddSingleton(s => new WabiSabiConfig
				{
					MaxInputCountByRound = inputCount - 1,  // Make sure that at least one IR fails for WrongPhase
					StandardInputRegistrationTimeout = TimeSpan.FromSeconds(20),
					ConnectionConfirmationTimeout = TimeSpan.FromSeconds(20),
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20),
					TransactionSigningTimeout = TimeSpan.FromSeconds(20),
				})));

		using var httpClient = coordinatorApp.CreateClient();

		// Create the coinjoin client
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		// Total test timeout.
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(200));
		cts.Token.Register(() => transactionCompleted.TrySetCanceled(), useSynchronizationContext: false);

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, keyManager, roundStateProvider);

		// Run the coinjoin client task.
		var coinjoinResult = await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token);
		Assert.True(coinjoinResult is SuccessfulCoinJoinResult);

		var broadcastedTx = await transactionCompleted.Task; // wait for the transaction to be broadcasted.
		Assert.NotNull(broadcastedTx);
	}

	/// <summary>
	/// A round whose transaction could not be broadcast has shown the addresses of the wallet to the coordinator all the
	/// same. The next coinjoin of the wallet gets other ones.
	/// </summary>
	[Fact]
	public async Task CoinJoinAfterAFailedBroadcastGetsNewAddressesAsync()
	{
		long[] amounts = [10_000_000, 20_000_000, 30_000_000, 40_000_000, 100_000_000];
		int inputCount = amounts.Length;

		KeyManager keyManager = KeyManager.CreateNew(out _, password: "", Network.Main);
		var coins = GenerateSmartCoins(keyManager, amounts, inputCount);

		// The node refuses the first transaction that the coordinator sends to it and takes the second one.
		var sentTransactions = new ConcurrentQueue<Transaction>();

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(coins,
				rpc =>
					rpc.OnSendRawTransactionAsync = (tx) =>
					{
						sentTransactions.Enqueue(tx);
						if (sentTransactions.Count == 1)
						{
							throw new RPCException(RPCErrorCode.RPC_TRANSACTION_REJECTED, "The node refuses the transaction.", null);
						}

						return tx.GetHash();
					})
			.ConfigureServices(services =>
				// The input registration keeps its default length, and ends as soon as the wallet registered its coins. A short
				// one would open a new round on every tick, and the second coinjoin could get into one that is about to expire.
				services.AddSingleton(s => new WabiSabiConfig
				{
					MaxInputCountByRound = inputCount,
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20), // This phase always lasts its whole time frame.
				})));

		using var httpClient = coordinatorApp.CreateClient();
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, keyManager, roundStateProvider);

		Assert.IsType<FailedCoinJoinResult>(await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token));
		Assert.IsType<SuccessfulCoinJoinResult>(await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token));

		Assert.Equal(2, sentTransactions.Count);
		var addressesByRound = sentTransactions
			.Select(tx => tx.Outputs.Select(output => output.ScriptPubKey).Where(script => keyManager.TryGetKeyForScriptPubKey(script, out _)).ToArray())
			.ToArray();

		Assert.NotEmpty(addressesByRound[0]);
		Assert.NotEmpty(addressesByRound[1]);
		Assert.Empty(addressesByRound[0].Intersect(addressesByRound[1]));
	}

	/// <summary>
	/// A coordinator may ask for a smallest output far above dust. The client joins such a round, and what the wallet
	/// gives up in it stays within its dust forfeit limit, although the round could not put up to its smallest output
	/// into an output of its own.
	/// </summary>
	[Theory]
	[InlineData(1_000_000, new long[] { 31_234_567, 47_111_113, 52_999_001, 88_000_077, 123_456_789 })]
	[InlineData(20_000_000, new long[] { 131_234_567, 247_111_113, 352_999_001, 488_000_077, 923_456_789 })]
	public async Task CoinJoinInARoundWithALargeSmallestOutputAsync(long smallestOutput, long[] amounts)
	{
		int inputCount = amounts.Length;
		var transactionCompleted = new TaskCompletionSource<Transaction>();

		KeyManager keyManager = KeyManager.CreateNew(out _, password: "", Network.Main);
		var coins = GenerateSmartCoins(keyManager, amounts, inputCount);

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(coins,
				rpc =>
					rpc.OnSendRawTransactionAsync = (tx) =>
					{
						transactionCompleted.SetResult(tx);
						return tx.GetHash();
					})
			.ConfigureServices(services =>
				services.AddSingleton(s => new WabiSabiConfig
				{
					MinRegistrableAmount = Money.Satoshis(smallestOutput),
					MaxInputCountByRound = inputCount,
					StandardInputRegistrationTimeout = TimeSpan.FromSeconds(20),
					ConnectionConfirmationTimeout = TimeSpan.FromSeconds(20),
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20),
					TransactionSigningTimeout = TimeSpan.FromSeconds(20),
				})));

		using var httpClient = coordinatorApp.CreateClient();
		var apiClient = _apiApplicationFactory.CreateWabiSabiHttpApiClient(httpClient);

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(200));
		cts.Token.Register(() => transactionCompleted.TrySetCanceled(), useSynchronizationContext: false);

		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		// The client keeps the limit of the wallet, as the wallet does.
		var coinJoinClient = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient, keyManager, roundStateProvider);

		var coinjoinResult = await coinJoinClient.StartCoinJoinAsync(() => coins, cts.Token);
		var success = Assert.IsType<SuccessfulCoinJoinResult>(coinjoinResult);

		var broadcastedTx = await transactionCompleted.Task;

		// Every output of the transaction is one that the round allows, so the round was what the test wanted.
		Assert.All(broadcastedTx.Outputs, output => Assert.True(output.Value >= Money.Satoshis(smallestOutput)));

		// What the wallet gave up beyond the mining fee, counted on the transaction.
		Assert.InRange(success.Costs.WastedDust.Satoshi, 0, keyManager.MaxCoinjoinLeftover.Satoshi);

		var spent = broadcastedTx.Inputs.Sum(input => coins.Single(coin => coin.Outpoint == input.PrevOut).Amount.Satoshi);
		var received = broadcastedTx.Outputs.Where(output => success.OutputScripts.Contains(output.ScriptPubKey)).Sum(output => output.Value.Satoshi);
		Assert.Equal((success.Costs.MiningFee + success.Costs.WastedDust).Satoshi, spent - received);
		_output.WriteLine($"Smallest output {smallestOutput}: spent {spent}, received {received}, mining fee {success.Costs.MiningFee.Satoshi}, given up {success.Costs.WastedDust.Satoshi}, outputs {string.Join(", ", broadcastedTx.Outputs.Select(o => o.Value.Satoshi))}.");
	}

	/// <summary>
	/// A participant whose signature request fails for one input must still join the blame round with the
	/// inputs it did sign: the coordinator removes only the unsigned input and whitelists the rest.
	/// </summary>
	[Theory]
	[InlineData(new long[] { 30_000_000, 40_000_000 }, new long[] { 50_000_000, 60_000_000 }, new long[] { 70_000_000, 80_000_000 })]
	public async Task CoinJoinWithPartiallySigningParticipantJoinsBlameRoundTestAsync(long[] satAmounts1, long[] satAmounts2, long[] satAmounts3)
	{
		int inputCount = satAmounts1.Length;

		var broadcastedTxTcs = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);

		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		cts.Token.Register(() => broadcastedTxTcs.TrySetCanceled(), useSynchronizationContext: false);

		KeyManager keyManager1 = KeyManager.CreateNew(out var _, password: "", Network.Main);
		KeyManager keyManager2 = KeyManager.CreateNew(out var _, password: "", Network.Main);
		KeyManager keyManager3 = KeyManager.CreateNew(out var _, password: "", Network.Main);

		// The second participant signs all but one of its inputs.
		var participant1Coins = GenerateSmartCoins(keyManager1, satAmounts1, inputCount);
		var participant2Coins = GenerateSmartCoins(keyManager2, satAmounts2, inputCount);
		var participant3Coins = GenerateSmartCoins(keyManager3, satAmounts3, inputCount);

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(
				Enumerable.Concat(participant1Coins, participant2Coins).Concat(participant3Coins).ToImmutableList(),
				rpc =>
				{
					rpc.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
					{
						var tx = Transaction.Create(Network.Main);
						return Task.FromResult(tx);
					};

					rpc.OnSendRawTransactionAsync = (tx) =>
					{
						broadcastedTxTcs.SetResult(tx);
						return tx.GetHash();
					};
				})
			.ConfigureServices(services =>
				services.AddSingleton(s => new WabiSabiConfig
				{
					AllowP2trInputs = true,
					AllowP2trOutputs = true,
					MaxInputCountByRound = 3 * inputCount,
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20), // This phase always lasts its whole time frame.
					TransactionSigningTimeout = TimeSpan.FromSeconds(4 * inputCount),
				})));

		await Task.Delay(100);

		var apiClient1 = _apiApplicationFactory.CreateWabiSabiHttpApiClient(coordinatorApp.CreateClient());
		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient1, cts.Token);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		using var httpClient = coordinatorApp.CreateClient();

		// Forwards every request except the first transaction signature of the first round, which is lost on the way.
		var signatureRequests = 0;
		using var partiallySigningHttpClientMock = new MockHttpClient()
		{
			BaseAddress = httpClient.BaseAddress,
			OnSendAsync = req =>
			{
				Assert.NotNull(req.RequestUri);

				if (req.RequestUri.ToString().Contains("transaction-signature") && Interlocked.Increment(ref signatureRequests) == 1)
				{
					throw new HttpRequestException("The request was lost.");
				}

				return httpClient.SendAsync(req, CancellationToken.None);
			}
		};

		var apiClient2 = _apiApplicationFactory.CreateWabiSabiHttpApiClient(partiallySigningHttpClientMock);
		var apiClient3 = _apiApplicationFactory.CreateWabiSabiHttpApiClient(coordinatorApp.CreateClient());

		var coinJoinClient1 = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient1, keyManager1, roundStateProvider);
		var coinJoinClient2 = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient2, keyManager2, roundStateProvider);
		var coinJoinClient3 = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient3, keyManager3, roundStateProvider);

		var participant1CoinjoinTask = coinJoinClient1.StartCoinJoinAsync(() => participant1Coins, cts.Token);
		var participant2CoinjoinTask = coinJoinClient2.StartCoinJoinAsync(() => participant2Coins, cts.Token);
		var participant3CoinjoinTask = coinJoinClient3.StartCoinJoinAsync(() => participant3Coins, cts.Token);

		await Task.WhenAll(participant1CoinjoinTask, participant2CoinjoinTask, participant3CoinjoinTask);

		Assert.IsType<SuccessfulCoinJoinResult>(await participant1CoinjoinTask);
		var participant2Result = Assert.IsType<SuccessfulCoinJoinResult>(await participant2CoinjoinTask);
		Assert.IsType<SuccessfulCoinJoinResult>(await participant3CoinjoinTask);

		var broadcastedTx = await broadcastedTxTcs.Task;

		// The second participant is in the blame round with the input it signed, and only with that one.
		var participant2Inputs = participant2Coins.Select(x => x.Coin.Outpoint).ToHashSet();
		var participant2InputsInTx = broadcastedTx.Inputs.Select(x => x.PrevOut).Where(participant2Inputs.Contains).ToList();
		Assert.Single(participant2InputsInTx);
		Assert.Equal(participant2InputsInTx, participant2Result.Coins.Select(x => x.Coin.Outpoint));

		var honestInputs = participant1Coins.Concat(participant3Coins).Select(x => x.Coin.Outpoint);
		Assert.Equal(
			honestInputs.Concat(participant2InputsInTx).Select(x => x.ToString()).Order(),
			broadcastedTx.Inputs.Select(x => x.PrevOut.ToString()).Order());
	}

	/// <summary>
	/// A participant that does not sign makes the round fail, and the others go on in the blame round. There they register
	/// other addresses than in the round that failed: that round has shown theirs to the coordinator and to everybody in it.
	/// </summary>
	[Theory]
	[InlineData(new long[] { 30_000_000, 40_000_000 }, new long[] { 50_000_000, 60_000_000 }, new long[] { 70_000_000, 80_000_000 })]
	public async Task CoinJoinWithBlameRoundTestAsync(long[] satAmounts1, long[] satAmounts2, long[] satAmounts3)
	{
		int inputCount = satAmounts1.Length;

		// At the end of the test a coinjoin transaction has to be created and broadcasted.
		var broadcastedTxTcs = new TaskCompletionSource<Transaction>(TaskCreationOptions.RunContinuationsAsynchronously);

		// Total test timeout.
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		cts.Token.Register(() => broadcastedTxTcs.TrySetCanceled(), useSynchronizationContext: false);

		KeyManager keyManager1 = KeyManager.CreateNew(out var _, password: "", Network.Main);
		KeyManager keyManager2 = KeyManager.CreateNew(out var _, password: "", Network.Main);
		KeyManager keyManager3 = KeyManager.CreateNew(out var _, password: "", Network.Main);

		// There are three participants. The second participant will not sign and will enforce a blame round.
		var participant1Coins = GenerateSmartCoins(keyManager1, satAmounts1, inputCount);
		var participant2CoinsBad = GenerateSmartCoins(keyManager2, satAmounts2, inputCount);
		var participant3Coins = GenerateSmartCoins(keyManager3, satAmounts3, inputCount);

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.AddMockRpcClient(
				Enumerable.Concat(participant1Coins, participant2CoinsBad).Concat(participant3Coins).ToImmutableList(),
				rpc =>
				{
					rpc.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
					{
						var tx = Transaction.Create(Network.Main);
						return Task.FromResult(tx);
					};

					// Make the coordinator believe that the transaction is being
					// broadcasted using the RPC interface. Once we receive this tx
					// (the `SendRawTransactionAsync` was invoked) we stop waiting
					// and finish the waiting tasks to finish the test successfully.
					rpc.OnSendRawTransactionAsync = (tx) =>
					{
						broadcastedTxTcs.SetResult(tx);
						return tx.GetHash();
					};
				})
			.ConfigureServices(services =>

				// Instruct the coordinator DI container to use this scoped services to build everything (WabiSabi controller, arena, etc)
				//
				// The signing phase has to time out, because the second participant never signs. Output registration always lasts its
				// whole time frame, so it is shortened to keep the test fast. The other phases (input registration, connection confirmation)
				// keep their defaults, each ends as soon as every participant has acted. A short window would drop slow participants,
				// and an input registration window below a minute opens a new round on every tick.
				services.AddSingleton(s => new WabiSabiConfig
				{
					AllowP2trInputs = true,
					AllowP2trOutputs = true,
					MaxInputCountByRound = 3 * inputCount, // To make the input phase end as early as all inputs are registered.
					OutputRegistrationTimeout = TimeSpan.FromSeconds(20),
					TransactionSigningTimeout = TimeSpan.FromSeconds(4 * inputCount),
				})));

		await Task.Delay(100);

		// Create the coinjoin client
		var apiClient1 = _apiApplicationFactory.CreateWabiSabiHttpApiClient(coordinatorApp.CreateClient());
		using var roundStateUpdater = RoundStateUpdaterForTesting.Create(apiClient1, cts.Token);
		var roundStateProvider = new RoundStateProvider(roundStateUpdater);

		var roundState = await roundStateProvider.CreateRoundAwaiterAsync(roundState => roundState.Phase == Phase.InputRegistration, cts.Token);

		using var httpClient = coordinatorApp.CreateClient();

		// Creates a mocked HttpClient that forwards all HTTP requests except for the transaction signature request, which is answered with 200 OK without being sent!
		using var nonSigningHttpClientMock = new MockHttpClient()
		{
			BaseAddress = httpClient.BaseAddress,
			OnSendAsync = req =>
			{
				Assert.NotNull(req.RequestUri);

				if (req.RequestUri.ToString().Contains("transaction-signature"))
				{
					return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
				}

				return httpClient.SendAsync(req, CancellationToken.None);
			}
		};

		var apiClient2Bad = _apiApplicationFactory.CreateWabiSabiHttpApiClient(nonSigningHttpClientMock);
		var apiClient3 = _apiApplicationFactory.CreateWabiSabiHttpApiClient(coordinatorApp.CreateClient());

		var coinJoinClient1 = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient1, keyManager1, roundStateProvider);
		var coinJoinClient2Bad = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient2Bad, keyManager2, roundStateProvider);
		var coinJoinClient3 = WabiSabiFactory.CreateTestCoinJoinClient(_ => apiClient3, keyManager3, roundStateProvider);

		var participant1CoinjoinTask = coinJoinClient1.StartCoinJoinAsync(() => participant1Coins, cts.Token);
		var participant2CoinjoinTaskBad = coinJoinClient2Bad.StartRoundAsync(participant2CoinsBad, UnrestrictedRound.Instance, roundState, cts.Token);
		var participant3CoinjoinTask = coinJoinClient3.StartCoinJoinAsync(() => participant3Coins, cts.Token);

		await Task.WhenAll(new Task[] { participant2CoinjoinTaskBad, participant1CoinjoinTask, participant3CoinjoinTask });

		var participant1Result = await participant1CoinjoinTask;
		var participant2ResultBad = await participant2CoinjoinTaskBad;
		var participant3Result = await participant3CoinjoinTask;

		Assert.IsType<SuccessfulCoinJoinResult>(participant1Result);

		// The mock acknowledges signatures without forwarding them to the coordinator.
		// Its local result can be disrupted or failed, but it must never succeed.
		Assert.IsNotType<SuccessfulCoinJoinResult>(participant2ResultBad);

		Assert.IsType<SuccessfulCoinJoinResult>(participant3Result);

		var broadcastedTx = await broadcastedTxTcs.Task; // wait for the transaction to be broadcasted.
		Assert.NotNull(broadcastedTx);

		// Only coins of the honest participants (the first one and the third one) are expected here. The second participant did not sign and was blamed.
		var expectedInputs = participant1Coins.Concat(participant3Coins)
			.Select(x => x.Coin.Outpoint.ToString())
			.Order()
			.ToList();

		var actualInputs = broadcastedTx.Inputs
			.Select(x => x.PrevOut.ToString())
			.Order();

		Assert.Equal(expectedInputs, actualInputs);

		// No address of the round that failed comes back in the blame round.
		var roundStates = (await apiClient1.GetStatusAsync(RoundStateRequest.Empty, cts.Token)).RoundStates;
		var blameRound = Assert.Single(roundStates, x => x.IsBlame && x.EndRoundState == EndRoundState.TransactionBroadcasted);
		var failedRound = Assert.Single(roundStates, x => x.Id == blameRound.BlameOf);
		Assert.Equal(EndRoundState.NotAllAlicesSign, failedRound.EndRoundState);

		var failedRoundAddresses = failedRound.CoinjoinState.Outputs.Select(x => x.ScriptPubKey).ToArray();
		var blameRoundAddresses = blameRound.CoinjoinState.Outputs.Select(x => x.ScriptPubKey).ToArray();

		Assert.Equal(broadcastedTx.Outputs.Select(x => x.ScriptPubKey.ToString()).Order(), blameRoundAddresses.Select(x => x.ToString()).Order());
		foreach (var keyManager in new[] { keyManager1, keyManager3 })
		{
			Assert.Contains(failedRoundAddresses, script => keyManager.TryGetKeyForScriptPubKey(script, out _));
			Assert.Contains(blameRoundAddresses, script => keyManager.TryGetKeyForScriptPubKey(script, out _));
		}

		Assert.Empty(failedRoundAddresses.Intersect(blameRoundAddresses));
	}

	[Theory]
	[InlineData(123456, 0.00, 0.00)]
	public async Task MultiClientsCoinJoinTestAsync(
		int seed,
		double faultInjectorMonkeyAggressiveness,
		double delayInjectorMonkeyAggressiveness)
	{
		// Total test timeout.
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

		const int NumberOfParticipants = 10;
		const int NumberOfCoinsPerParticipant = 2;
		const int ExpectedInputNumber = (NumberOfParticipants * NumberOfCoinsPerParticipant) / 2;

		var coinJoinBroadcasted = new TaskCompletionSource<Transaction>();
		var rpc = BitcoinFactory.GetMockMinimalRpc();
		var onSendRawTransaction = rpc.OnSendRawTransactionAsync;
		rpc.OnSendRawTransactionAsync = tx =>
		{
			onSendRawTransaction?.Invoke(tx);
			if (tx.Inputs.Count > 1)
			{
				coinJoinBroadcasted.SetResult(tx);
			}

			return tx.GetHash();
		};

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.ConfigureServices(services =>
			{
				// Instruct the coordinator DI container to use these two scoped
				// services to build everything (WabiSabi controller, arena, etc)
				services.AddSingleton<IRPCClient>(s => rpc);
				services.AddSingleton(s => new WabiSabiConfig(Path.GetTempFileName())
				{
					MaxRegistrableAmount = Money.Coins(500m),
					MaxInputCountByRound = (int)(ExpectedInputNumber / (1 + (10 * (faultInjectorMonkeyAggressiveness + delayInjectorMonkeyAggressiveness)))),
					StandardInputRegistrationTimeout = TimeSpan.FromSeconds(5 * ExpectedInputNumber),
					BlameInputRegistrationTimeout = TimeSpan.FromSeconds(2 * ExpectedInputNumber),
					ConnectionConfirmationTimeout = TimeSpan.FromSeconds(2 * ExpectedInputNumber),
					OutputRegistrationTimeout = TimeSpan.FromSeconds(30),
					TransactionSigningTimeout = TimeSpan.FromSeconds(3 * ExpectedInputNumber),
				});
			}));

		using var httpClient = coordinatorApp.CreateClient();

		await Task.Delay(100);
		using var httpClientWrapper = new MonkeyHttpClient(
			httpClient,
			() => // This monkey injects `HttpRequestException` randomly to simulate errors in the communication.
			{
				if (Random.Shared.NextDouble() < faultInjectorMonkeyAggressiveness)
				{
					throw new HttpRequestException("Crazy monkey hates you, donkey.");
				}
				return Task.CompletedTask;
			},
			async () => // This monkey injects `Delays` randomly to simulate slow response times.
			{
				await Task.Delay(TimeSpan.FromSeconds(5 * delayInjectorMonkeyAggressiveness), cts.Token).ConfigureAwait(false);
			});
		httpClientWrapper.BaseAddress = httpClient.BaseAddress;

		var apiClient = new WabiSabiHttpApiClient("", new MockHttpClientFactory {OnCreateClient = _ => httpClientWrapper});

		var participants = Enumerable
			.Range(0, NumberOfParticipants)
			.Select(i => new Participant($"participant{i}", rpc, _ => apiClient))
			.ToArray();

		foreach (var participant in participants)
		{
			await participant.GenerateSourceCoinAsync(cts.Token);
		}
		var dummyWallet = new TestWallet("dummy", rpc);
		await dummyWallet.GenerateAsync(101, cts.Token);
		foreach (var participant in participants)
		{
			await participant.GenerateCoinsAsync(NumberOfCoinsPerParticipant, seed, cts.Token);
		}
		await dummyWallet.GenerateAsync(101, cts.Token);

		var tasks = participants.Select(x => x.StartParticipatingAsync(cts.Token)).ToArray();

		var coinjoinTransactionCompletionTask = coinJoinBroadcasted.Task.WaitAsync(cts.Token);
		var participantsFinishedTask = Task.WhenAll(tasks);
		var finishedTask = await Task.WhenAny(participantsFinishedTask, coinjoinTransactionCompletionTask);

		if (finishedTask == coinjoinTransactionCompletionTask)
		{
			var broadcastedCoinjoinTransaction = await coinjoinTransactionCompletionTask;
			var mempool = await rpc.GetRawMempoolAsync();
			var coinjoinFromMempool = await rpc.GetRawTransactionAsync(mempool.Single(), cancellationToken: cts.Token);

			Assert.Equal(broadcastedCoinjoinTransaction.GetHash(), coinjoinFromMempool.GetHash());
		}
		else if (finishedTask == participantsFinishedTask)
		{
			var participantsFinishedSuccessfully = tasks
				.Where(t => t.IsCompletedSuccessfully)
				.Select(t => t.Result)
				.ToArray();

			// In case some participants claim to have finished successfully then wait a second for seeing
			// the coinjoin in the mempool. This seems really hard to believe but just in case.
			if (participantsFinishedSuccessfully.All(x => x is SuccessfulCoinJoinResult))
			{
				await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
				var mempool = await rpc.GetRawMempoolAsync(cts.Token);
				Assert.Single(mempool);
			}
			else if (participantsFinishedSuccessfully.All(x => x is FailedCoinJoinResult))
			{
				throw new Exception("All participants finished, but CoinJoin still not in the mempool (no more blame rounds).");
			}
			else if (participantsFinishedSuccessfully.Length == 0 && !cts.IsCancellationRequested)
			{
				var exceptions = tasks
					.Where(x => x.IsFaulted)
					.Select(x => new Exception("Something went wrong", x.Exception))
					.ToArray();
				throw new AggregateException(exceptions);
			}
			else
			{
				throw new Exception("All participants finished, but CoinJoin still not in the mempool.");
			}
		}
		else
		{
			throw new Exception("This is not so possible.");
		}
	}

	[Fact]
	public async Task RegisterCoinIdempotencyAsync()
	{
		using var signingKey = new Key();
		Coin coinToRegister = new(
			fromOutpoint: BitcoinFactory.CreateOutPoint(),
			fromTxOut: new TxOut(Money.Coins(1), signingKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit)));

		await using var coordinatorApp = _apiApplicationFactory.WithWebHostBuilder(builder =>
			builder.ConfigureServices(services =>
			{
				var rpc = BitcoinFactory.GetMockMinimalRpc();
				rpc.OnGetTxOutAsync = (_, _, _) => new()
				{
					Confirmations = 101,
					IsCoinBase = false,
					ScriptPubKeyType = "witness_v0_keyhash",
					TxOut = coinToRegister.TxOut
				};
				rpc.OnGetRawTransactionAsync = (txid, throwIfNotFound) =>
				{
					var tx = Transaction.Create(Network.Main);
					return Task.FromResult(tx);
				};
				services.AddSingleton<IRPCClient>(s => rpc);
			}));

		using var httpClient = coordinatorApp.CreateClient();

		var apiClient = await _apiApplicationFactory.CreateArenaClientAsync(httpClient);
		var rounds = (await apiClient.GetStatusAsync(RoundStateRequest.Empty, CancellationToken.None)).RoundStates;
		var round = rounds.First(x => x.CoinjoinState is ConstructionState);
		using var stutteredHttpClient = new StuttererHttpClient(httpClient);
		var stutteredApiClient = new ArenaClient(
			apiClient.AmountCredentialClient,
			apiClient.VsizeCredentialClient,
			apiClient.CoordinatorIdentifier,
			_apiApplicationFactory.CreateWabiSabiHttpApiClient(stutteredHttpClient));

		var ownershipProof = WabiSabiFactory.CreateOwnershipProof(signingKey, round.Id);
		var response = await stutteredApiClient.RegisterInputAsync(round.Id, coinToRegister.Outpoint, ownershipProof, CancellationToken.None);

		Assert.NotEqual(Guid.Empty, response.Value);
	}

	private ImmutableList<SmartCoin> GenerateSmartCoins(KeyManager keyManager, long[] amounts, int inputCount) =>
		keyManager.GetKeys()
			.Take(inputCount)
			.Select((x, i) => BitcoinFactory.CreateSmartCoin(x, Money.Satoshis(amounts[i])))
			.ToImmutableList();

	public class TestableRpcClient : RpcClientBase
	{
		public TestableRpcClient(RpcClientBase rpc)
			: base(rpc.RpcClient)
		{
		}

		public Action<Transaction>? AfterSendRawTransaction { get; set; }

		public override async Task<uint256> SendRawTransactionAsync(Transaction transaction, CancellationToken cancellationToken = default)
		{
			var ret = await base.SendRawTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
			AfterSendRawTransaction?.Invoke(transaction);
			return ret;
		}
	}
}
