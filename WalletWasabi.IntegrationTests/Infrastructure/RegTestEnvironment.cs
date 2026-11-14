using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.BitcoinRpc;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.IntegrationTests.BitcoinCore;
using WalletWasabi.Wallets;

namespace WalletWasabi.IntegrationTests.Infrastructure;

/// <summary>
/// Comprehensive test environment for integration tests.
/// Provides access to Bitcoin Core RPC, the transaction store, and wallet infrastructure.
/// </summary>
public sealed class RegTestEnvironment : IAsyncDisposable
{
	private readonly uint _tipHeightAtCreation;

	private RegTestEnvironment(
		IntegrationTestFixture fixture,
		string workDir,
		EventBus eventBus,
		AllTransactionStore transactionStore,
		CpfpInfoProvider cpfpInfoProvider,
		uint tipHeightAtCreation)
	{
		Fixture = fixture;
		WorkDir = workDir;
		EventBus = eventBus;
		TransactionStore = transactionStore;
		CpfpInfoProvider = cpfpInfoProvider;
		ServiceConfiguration = new ServiceConfiguration();
		_tipHeightAtCreation = tipHeightAtCreation;
	}

	public IntegrationTestFixture Fixture { get; }
	public string WorkDir { get; }
	public EventBus EventBus { get; }
	public AllTransactionStore TransactionStore { get; }
	public CpfpInfoProvider CpfpInfoProvider { get; }
	public ServiceConfiguration ServiceConfiguration { get; }

	/// <summary>
	/// Wallet-specific RPC client for operations requiring wallet context (send, generate, etc.).
	/// </summary>
	public IRPCClient RpcClient => Fixture.WalletRpcClient;
	public Network Network => RpcClient.Network;
	public CoreNode BitcoinCoreNode => Fixture.BitcoinCoreNode;

	public const string DefaultPassword = "password";

	/// <summary>
	/// Creates and initializes a new test environment.
	/// </summary>
	public static async Task<RegTestEnvironment> CreateAsync(
		IntegrationTestFixture fixture,
		[CallerFilePath] string callerFilePath = "",
		[CallerMemberName] string callerMemberName = "")
	{
		string workDir = GetWorkDir(callerFilePath, callerMemberName);

		// Clean up previous test run
		if (Directory.Exists(workDir))
		{
			await IoHelpers.TryDeleteDirectoryAsync(workDir).ConfigureAwait(false);
		}
		Directory.CreateDirectory(workDir);

		var eventBus = new EventBus();

		var transactionStore = new AllTransactionStore(
			Path.Combine(workDir, "transactions"),
			fixture.BitcoinCoreNode.Network);
		await transactionStore.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

		// Use unique worker name to avoid conflicts between tests
		var workerName = $"CpfpInfoProvider_{Guid.NewGuid():N}";
		var cpfpInfoProvider = new CpfpInfoProvider(
			Workers.Spawn(workerName, Workers.EventDriven(Unit.Instance, CpfpInfoUpdater.Create(fixture.WalletRpcClient, eventBus))));

		var tipHeight = (uint)await fixture.WalletRpcClient.GetBlockCountAsync(CancellationToken.None).ConfigureAwait(false);

		return new RegTestEnvironment(
			fixture,
			workDir,
			eventBus,
			transactionStore,
			cpfpInfoProvider,
			tipHeight);
	}

	/// <summary>
	/// Creates a new KeyManager for testing: the wallet of a test is created at the tip that the chain had when the test began.
	/// Whatever the test does to the chain after that is for the wallet to find.
	/// </summary>
	public KeyManager CreateKeyManager(string? password = null)
	{
		var keyManager = KeyManager.CreateNew(out _, password ?? DefaultPassword, Network);
		keyManager.StartAt(_tipHeightAtCreation);
		return keyManager;
	}

	/// <summary>
	/// Creates a Wallet instance (not started). Starting it synchronizes it with the node.
	/// </summary>
	public Wallet CreateWallet(KeyManager keyManager)
	{
		var factory = Wallet.CreateFactory(
			Network,
			RpcClient,
			TransactionStore,
			ServiceConfiguration,
			EventBus,
			CpfpInfoProvider);

		return factory(keyManager);
	}

	/// <summary>
	/// Waits until the wallet processed every block that the node has at the moment.
	/// </summary>
	public async Task WaitForWalletToCatchUpAsync(Wallet wallet, TimeSpan? timeout = null)
	{
		var nodeHeight = (uint)await RpcClient.GetBlockCountAsync().ConfigureAwait(false);
		var nodeTip = await RpcClient.GetBlockHashAsync((int)nodeHeight).ConfigureAwait(false);

		await WaitForConditionAsync(
			() => wallet.KeyManager.GetProcessedBlocks() is var (height, blockHashes)
				&& height is not null
				&& height.Height == nodeHeight
				&& (blockHashes.IsEmpty || blockHashes[^1] == nodeTip),
			timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
	}

	/// <summary>
	/// Funds a wallet address and confirms the transaction.
	/// </summary>
	/// <param name="address">The address to fund.</param>
	/// <param name="amount">The amount to send.</param>
	/// <param name="confirmations">Number of confirmations (blocks to generate after sending).</param>
	/// <returns>The transaction ID.</returns>
	public async Task<uint256> FundAddressAsync(BitcoinAddress address, Money amount, int confirmations = 1)
	{
		var txid = await RpcClient.SendToAddressAsync(address, amount).ConfigureAwait(false);

		if (confirmations > 0)
		{
			await RpcClient.GenerateAsync(confirmations).ConfigureAwait(false);
		}

		return txid;
	}

	/// <summary>
	/// Waits for a specific number of events to be published on the EventBus.
	/// </summary>
	/// <typeparam name="TEvent">The type of event to wait for.</typeparam>
	/// <param name="count">The number of events to wait for.</param>
	/// <param name="timeout">Maximum time to wait.</param>
	/// <returns>The collected events.</returns>
	public async Task<List<TEvent>> WaitForEventsAsync<TEvent>(int count, TimeSpan timeout)
		where TEvent : notnull
	{
		var events = new List<TEvent>();
		var completion = new TaskCompletionSource<List<TEvent>>();

		using var cts = new CancellationTokenSource(timeout);
		using var registration = cts.Token.Register(() =>
			completion.TrySetException(new TimeoutException($"Timed out waiting for {count} {typeof(TEvent).Name} events. Received {events.Count}.")));

		using var subscription = EventBus.Subscribe<TEvent>(e =>
		{
			lock (events)
			{
				events.Add(e);
				if (events.Count >= count)
				{
					completion.TrySetResult(new List<TEvent>(events));
				}
			}
		});

		return await completion.Task.ConfigureAwait(false);
	}

	/// <summary>
	/// Waits for a condition to become true.
	/// </summary>
	/// <param name="condition">The condition to check.</param>
	/// <param name="timeout">Maximum time to wait.</param>
	/// <param name="pollInterval">How often to check the condition.</param>
	public async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null)
	{
		var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
		var deadline = DateTime.UtcNow + timeout;

		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
			{
				throw new TimeoutException("Condition was not met within the timeout period.");
			}
			await Task.Delay(interval).ConfigureAwait(false);
		}
	}


	private static string GetWorkDir(string callerFilePath, string callerMemberName)
	{
		var dataDir = EnvironmentHelpers.GetDataDir(Path.Combine("WalletWasabi", "IntegrationTests"));
		return Path.Combine(dataDir, EnvironmentHelpers.ExtractFileName(callerFilePath), callerMemberName);
	}

	public ValueTask DisposeAsync()
	{
		TransactionStore.Dispose();
		return ValueTask.CompletedTask;
	}
}
