using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Tests.UnitTests.Services;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using WalletWasabi.WabiSabi.Client.RoundStateAwaiters;
using WalletWasabi.WabiSabi.Client.StatusChangedEvents;
using WalletWasabi.WabiSabi.Coordinator.PostRequests;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

/// <summary>
/// Tests for <see cref="CoinJoinManager"/>: a coinjoin is one round that the user asked for. When it is over the wallet is
/// stopped, and nothing starts another round by itself.
/// </summary>
public class CoinJoinManagerTests
{
	[Fact]
	public async Task CoinJoinThatCannotTakePlaceStopsTheWalletAsync()
	{
		var testDeadline = TestContext.Current.CancellationToken;

		// The wallet has no coins, so there is nothing that it could register.
		using var environment = await TestEnvironment.CreateAsync();
		var (manager, wallet) = (environment.Manager, environment.Wallet);

		await manager.StartAsync(testDeadline);
		try
		{
			manager.RequestCoinJoinStart(wallet);

			// The coinjoin ends with an error, and that is the end of it: the wallet is told that it stopped. Before, the
			// manager scheduled another start instead, and went on doing so until the wallet had coins to register.
			await environment.WaitForAsync<WalletStoppedCoinJoinEventArgs>(count: 1, testDeadline);

			var seen = environment.GetNotifications();

			Assert.All(seen, e => Assert.Same(wallet, e.Wallet));
			Assert.Equal(
				[typeof(StartedEventArgs), typeof(CompletedEventArgs), typeof(StartErrorEventArgs), typeof(WalletStoppedCoinJoinEventArgs)],
				seen.Select(e => e.GetType()));
			Assert.Equal(CoinjoinError.NoCoinsEligibleToMix, seen.OfType<StartErrorEventArgs>().Single().Error);

			// Nothing is left over: no coinjoin is running and none is scheduled.
			Assert.Equal(CoinJoinClientState.Idle, await GetClientStateAsync(manager, wallet, CoinJoinClientState.Idle, testDeadline));

			await Task.Delay(TimeSpan.FromSeconds(2), testDeadline);
			Assert.Equal(seen.Length, environment.GetNotifications().Length);
		}
		finally
		{
			await manager.StopAsync(CancellationToken.None);
		}
	}

	[Fact]
	public async Task StartThatIsAskedForWhileCoinJoinIsBeingStoppedTakesItsPlaceAsync()
	{
		var testDeadline = TestContext.Current.CancellationToken;

		// The wallet has a coin, and the coordinator has no round: a coinjoin that is started waits for one.
		using var environment = await TestEnvironment.CreateAsync();
		var (manager, wallet) = (environment.Manager, environment.Wallet);
		environment.GiveTheWalletCoin(Money.Coins(1m));

		await manager.StartAsync(testDeadline);
		try
		{
			manager.RequestCoinJoinStart(wallet);
			await environment.WaitForAsync<StartedEventArgs>(count: 1, testDeadline);
			Assert.Equal(CoinJoinClientState.InProgress, await GetClientStateAsync(manager, wallet, CoinJoinClientState.InProgress, testDeadline));

			// A start while the coinjoin is running changes nothing: it is the coinjoin that was asked for.
			manager.RequestCoinJoinStart(wallet);
			await Task.Delay(TimeSpan.FromSeconds(2), testDeadline);
			Assert.Single(environment.GetNotifications().OfType<StartedEventArgs>());
			Assert.Empty(environment.GetNotifications().OfType<WalletStoppedCoinJoinEventArgs>());

			// Stop, and Play right behind it: the first coinjoin is still on its way down when the start arrives. Nothing
			// starts a coinjoin by itself any more, so that start must not get lost.
			manager.RequestCoinJoinStop(wallet);
			manager.RequestCoinJoinStart(wallet);

			await environment.WaitForAsync<StartedEventArgs>(count: 2, testDeadline);

			var kinds = environment.GetNotifications()
				.Where(e => e is StartedEventArgs or CompletedEventArgs or WalletStoppedCoinJoinEventArgs or StartErrorEventArgs)
				.Select(e => e.GetType());
			Assert.Equal(
				[typeof(StartedEventArgs), typeof(CompletedEventArgs), typeof(WalletStoppedCoinJoinEventArgs), typeof(StartedEventArgs)],
				kinds);
			Assert.Equal(CoinJoinClientState.InProgress, await GetClientStateAsync(manager, wallet, CoinJoinClientState.InProgress, testDeadline));

			// A stop alone ends it, and then nothing follows.
			manager.RequestCoinJoinStop(wallet);
			await environment.WaitForAsync<WalletStoppedCoinJoinEventArgs>(count: 2, testDeadline);
			Assert.Equal(CoinJoinClientState.Idle, await GetClientStateAsync(manager, wallet, CoinJoinClientState.Idle, testDeadline));

			await Task.Delay(TimeSpan.FromSeconds(2), testDeadline);
			Assert.Equal(2, environment.GetNotifications().OfType<StartedEventArgs>().Count());
		}
		finally
		{
			await manager.StopAsync(CancellationToken.None);
		}
	}

	[Theory]
	[InlineData(true, TaskStatus.Canceled, true)]
	[InlineData(true, TaskStatus.Faulted, true)]
	[InlineData(true, TaskStatus.RanToCompletion, false)]
	[InlineData(false, TaskStatus.Canceled, false)]
	[InlineData(false, TaskStatus.Faulted, false)]
	[InlineData(false, TaskStatus.RanToCompletion, false)]
	public void OnlyCoinJoinThatWasStoppedBeforeItsEndCountsAsInterrupted(bool isStopped, TaskStatus coinJoinTaskStatus, bool expected)
	{
		// The send workflow and the shutdown start a coinjoin again that they interrupted. A round that ran to its end,
		// which a stop in the critical phase does not prevent, is not followed by another one.
		Assert.Equal(expected, CoinJoinManager.WasInterrupted(isStopped, coinJoinTaskStatus));
	}

	/// <summary>The manager learns the state of a wallet about once a second, so the state that is expected may take a moment.</summary>
	private static async Task<CoinJoinClientState> GetClientStateAsync(CoinJoinManager manager, WalletWasabi.Wallets.Wallet wallet, CoinJoinClientState expected, CancellationToken cancellationToken)
	{
		var state = CoinJoinClientState.Idle;

		for (var attempt = 0; attempt < 50; attempt++)
		{
			try
			{
				state = manager.GetCoinjoinClientState(wallet.WalletId);
				if (state == expected)
				{
					break;
				}
			}
			catch (ArgumentException)
			{
				// The manager has not looked at the wallet yet.
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
		}

		return state;
	}

	/// <summary>A wallet that is not started, a coordinator that is not there, and the manager between them.</summary>
	private sealed class TestEnvironment : IDisposable
	{
		private readonly AllTransactionStore _transactionStore;
		private readonly MailboxProcessor<CpfpInfoMessage> _cpfpInfoUpdater;
		private readonly MailboxProcessor<RoundUpdateMessage> _roundStateUpdater;
		private readonly CoinPrison _coinPrison;
		private readonly List<StatusChangedEventArgs> _notifications = new();

		private TestEnvironment(string workDir)
		{
			var network = Network.Main;
			var eventBus = new EventBus();

			_transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, network);
			_cpfpInfoUpdater = Workers.Spawn($"CpfpInfoProvider_{Guid.NewGuid():N}", Workers.EventDriven(Unit.Instance, CpfpInfoUpdater.Create(new MockRpcClient(), eventBus)));
			var walletFactory = WalletWasabi.Wallets.Wallet.CreateFactory(network, new MockRpcClient(), _transactionStore, new ServiceConfiguration(), eventBus, new CpfpInfoProvider(_cpfpInfoUpdater));

			Wallet = walletFactory(KeyManager.CreateNew(out _, "password", network));
			Assert.True(Wallet.TryLogin("password"));

			// The round states are only asked for when the test says so, and it never does: there is no round.
			_roundStateUpdater = RoundStateUpdaterForTesting.CreateManual(new NoCoordinator());
			_coinPrison = new CoinPrison(Path.Combine(workDir, "PrisonedCoins.json"), []);

			Manager = new CoinJoinManager(
				() => Task.FromResult<IEnumerable<WalletWasabi.Wallets.Wallet>>([Wallet]),
				new RoundStateProvider(_roundStateUpdater),
				_ => new NoCoordinator(),
				new CoinJoinConfiguration("CoinJoinCoordinatorIdentifier", MaxCoinJoinMiningFeeRate: 150m, AbsoluteMinInputCount: 21, AllowSoloCoinjoining: false),
				_coinPrison,
				(_, _) => Task.CompletedTask,
				eventBus);

			Manager.StatusChanged += (_, e) =>
			{
				lock (_notifications)
				{
					_notifications.Add(e);
				}
			};
		}

		public WalletWasabi.Wallets.Wallet Wallet { get; }

		public CoinJoinManager Manager { get; }

		public static async Task<TestEnvironment> CreateAsync([System.Runtime.CompilerServices.CallerMemberName] string callerMemberName = "")
		{
			var workDir = await Common.GetEmptyWorkDirAsync(callerMemberName: callerMemberName);
			return new TestEnvironment(workDir);
		}

		/// <summary>What the manager told so far, in the order in which it told it.</summary>
		public StatusChangedEventArgs[] GetNotifications()
		{
			lock (_notifications)
			{
				return _notifications.ToArray();
			}
		}

		/// <summary>Waits until the manager told a thing that many times.</summary>
		public async Task WaitForAsync<T>(int count, CancellationToken cancellationToken) where T : StatusChangedEventArgs
		{
			var deadline = DateTime.UtcNow.AddSeconds(30);

			while (GetNotifications().OfType<T>().Count() < count)
			{
				if (DateTime.UtcNow > deadline)
				{
					throw new TimeoutException($"The manager told {typeof(T).Name} {GetNotifications().OfType<T>().Count()} times, {count} were expected. It told: {string.Join(", ", GetNotifications().Select(e => e.GetType().Name))}");
				}

				await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
			}
		}

		/// <summary>A confirmed coin that the wallet may register in a round.</summary>
		public void GiveTheWalletCoin(Money amount)
		{
			var tx = Network.Main.CreateTransaction();
			tx.Version = 1;
			tx.LockTime = LockTime.Zero;
			tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
			tx.Outputs.Add(amount, Wallet.KeyManager.GetNextReceiveKey().P2Taproot);

			Wallet.TransactionProcessor.Process(new SmartTransaction(tx, new Height.ChainHeight(100)));
			Assert.Single(Wallet.Coins);
		}

		public void Dispose()
		{
			Manager.Dispose();
			_coinPrison.Dispose();
			_roundStateUpdater.Dispose();
			_cpfpInfoUpdater.Dispose();
			_transactionStore.Dispose();
		}
	}

	/// <summary>The tests have no coordinator. A coinjoin that finds no round never asks for one.</summary>
	private sealed class NoCoordinator : IWabiSabiApiRequestHandler
	{
		public Task<InputRegistrationResponse> RegisterInputAsync(InputRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ConnectionConfirmationResponse> ConfirmConnectionAsync(ConnectionConfirmationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RegisterOutputAsync(OutputRegistrationRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task RemoveInputAsync(InputsRemovalRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task SignTransactionAsync(TransactionSignaturesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<ReissueCredentialResponse> ReissuanceAsync(ReissueCredentialRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<RoundStateResponse> GetStatusAsync(RoundStateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
