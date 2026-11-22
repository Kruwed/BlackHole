using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Client;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Helpers;
using WalletWasabi.JoinMarket;
using WalletWasabi.Services;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Manager;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Client;

/// <summary>
/// The settings connect the application to the Nostr relays and to the coordinator that the user names, while it runs.
/// The application takes them when they answer and keeps what it has when they do not. It can also only be asked
/// whether they answer, which changes nothing.
/// </summary>
/// <remarks>
/// A process holds one application: its workers are known by their names. So this is one test that goes through
/// everything with one application, on regtest, where Tor is not used and nothing leaves this computer.
/// </remarks>
public class ConnectionsTests
{
	[Fact]
	public async Task ApplicationTakesTheRelaysAndTheCoordinatorThatAnswerAsync()
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
		var dataDir = await Common.GetEmptyWorkDirAsync();
		var configuredRelay = new Uri($"ws://127.0.0.1:{TestNostrRelay.GetFreePort()}/");

		// No coordinator is set, as on mainnet before the user names one, and nothing listens where the node is said to be.
		var persistentConfig = PersistentConfigManager.DefaultRegTestConfig with
		{
			CoordinatorUri = "",
			NostrRelays = new ValueList<string>([configuredRelay.ToString()]),
			BitcoinRpcUri = $"http://127.0.0.1:{TestNostrRelay.GetFreePort()}/",
			JsonRpcServerEnabled = false
		};

		var global = new Global(dataDir, new Config(persistentConfig, []));

		try
		{
			await AssertTorAndTheNodeAreOnlyAskedAsync(global, cts.Token);
			await AssertRelaysAreTakenOnceOneOfThemAnswersAsync(global, configuredRelay, cts.Token);

			// The services of the application start, as they do when it starts. The RPC server is off, and it alone would need to know how to end the application.
			await global.InitializeAsync(initializeSleepInhibitor: false, terminateService: null!, cts.Token);

			await AssertCoordinatorIsTakenOnceItAnswersAsync(global, cts.Token);
		}
		finally
		{
			await global.DisposeAsync();
		}
	}

	private static async Task AssertTorAndTheNodeAreOnlyAskedAsync(Global global, CancellationToken cancellationToken)
	{
		// On regtest Tor is not used, so there is none to connect to, and none to ask.
		var result = await global.ConnectToTorAsync(cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("Tor is turned off.", result.Error);

		result = await global.VerifyTorAsync(cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("Tor is turned off.", result.Error);

		// What is not an address is not asked at all, and nothing answers where the node is said to be.
		result = await global.VerifyBitcoinNodeAsync("node.example", "", cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("The address of the Bitcoin node is not an HTTP address.", result.Error);

		result = await global.VerifyBitcoinNodeAsync(global.Config.BitcoinRpcUri, "user:password", cancellationToken);
		Assert.False(result.IsOk);
		Assert.StartsWith("The Bitcoin node cannot be reached", result.Error);

		result = await global.VerifyBitcoinNodeAsync(global.Config.BitcoinRpcUri, "neither this nor that", cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("The RPC credentials are neither 'user:password' nor 'cookiefile=path'.", result.Error);
	}

	private static async Task AssertRelaysAreTakenOnceOneOfThemAnswersAsync(Global global, Uri configuredRelay, CancellationToken cancellationToken)
	{
		await using var relay = new TestNostrRelay();
		var nobody = new Uri($"ws://127.0.0.1:{TestNostrRelay.GetFreePort()}/");

		var offers = global.HostedServices.Get<MakerOfferService>();
		Assert.Equal([configuredRelay], offers.Relays);
		Assert.Equal([configuredRelay], global.NostrRelays);

		// The relays are only asked: whatever they answer, the application keeps the relays that it has.
		var answers = await global.VerifyNostrRelaysAsync([relay.Uri, nobody], cancellationToken);

		Assert.Equal([true, false], answers.Select(x => x.IsReachable));
		Assert.Equal([configuredRelay], offers.Relays);
		Assert.Empty(relay.Received);
		await Assert.ThrowsAsync<ArgumentException>(async () => await global.VerifyNostrRelaysAsync([], cancellationToken));

		// No relay answers: the application keeps the relays that it has.
		var reachabilities = await global.ConnectToNostrRelaysAsync([nobody], cancellationToken);

		Assert.False(reachabilities.Single().IsReachable);
		Assert.Equal([configuredRelay], offers.Relays);

		// One of two answers: the application posts to both from now on, as the other one may answer later.
		reachabilities = await global.ConnectToNostrRelaysAsync([relay.Uri, nobody], cancellationToken);

		Assert.Equal([true, false], reachabilities.Select(x => x.IsReachable));
		Assert.Equal([relay.Uri, nobody], offers.Relays);
		Assert.Equal([relay.Uri, nobody], global.NostrRelays);

		// Connecting has sent the relay nothing.
		Assert.Empty(relay.Received);

		// With no relay there is nothing to connect to.
		await Assert.ThrowsAsync<ArgumentException>(async () => await global.ConnectToNostrRelaysAsync([], cancellationToken));
		Assert.Equal([relay.Uri, nobody], offers.Relays);
	}

	private static async Task AssertCoordinatorIsTakenOnceItAnswersAsync(Global global, CancellationToken cancellationToken)
	{
		await using var coordinator = new TestCoordinatorServer();
		await using var otherCoordinator = new TestCoordinatorServer();
		await using var somethingElse = new TestCoordinatorServer { StatusAnswer = "<html><body>Welcome</body></html>" };
		var nobody = $"http://127.0.0.1:{TestNostrRelay.GetFreePort()}/";

		Assert.Null(global.HostedServices.GetOrDefault<CoinJoinManager>());
		Assert.Null(global.CoordinatorUri);

		// What is not an address is not asked at all.
		var result = await global.ConnectToCoordinatorAsync("coordinator.example", cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("The address of the coordinator is not an HTTP address.", result.Error);

		result = await global.VerifyCoordinatorAsync("coordinator.example", cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("The address of the coordinator is not an HTTP address.", result.Error);

		// The coordinator is only asked: it answers, and the application stays without one.
		result = await global.VerifyCoordinatorAsync(coordinator.Uri.ToString(), cancellationToken);
		Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
		Assert.Null(global.HostedServices.GetOrDefault<CoinJoinManager>());
		Assert.Null(global.CoordinatorUri);

		result = await global.VerifyCoordinatorAsync(nobody, cancellationToken);
		Assert.False(result.IsOk);
		Assert.StartsWith("The coordinator cannot be reached", result.Error);

		result = await global.VerifyCoordinatorAsync(somethingElse.Uri.ToString(), cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("What answers at this address is not a coordinator.", result.Error);

		// An onion service cannot be reached without Tor, and on regtest Tor is not used.
		result = await global.ConnectToCoordinatorAsync("http://abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcd.onion/", cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("The coordinator is an onion service, and Tor is turned off.", result.Error);

		// Nothing answers there: the application stays without a coordinator.
		result = await global.ConnectToCoordinatorAsync(nobody, cancellationToken);
		Assert.False(result.IsOk);
		Assert.StartsWith("The coordinator cannot be reached", result.Error);
		Assert.Null(global.HostedServices.GetOrDefault<CoinJoinManager>());

		// Something answers there, and it is not a coordinator.
		result = await global.ConnectToCoordinatorAsync(somethingElse.Uri.ToString(), cancellationToken);
		Assert.False(result.IsOk);
		Assert.Equal("What answers at this address is not a coordinator.", result.Error);
		Assert.Null(global.HostedServices.GetOrDefault<CoinJoinManager>());

		// The coordinator answers: the coinjoins of the application are there, and they run. A wallet that is open is told.
		var announcements = 0;
		using var subscription = global.EventBus.Subscribe<CoinJoinAvailable>(_ => Interlocked.Increment(ref announcements));

		result = await global.ConnectToCoordinatorAsync(coordinator.Uri.ToString(), cancellationToken);
		Assert.Equal(1, announcements);

		Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
		var manager = global.HostedServices.GetOrDefault<CoinJoinManager>();
		Assert.NotNull(manager);
		Assert.NotNull(manager.ExecuteTask);
		Assert.False(manager.ExecuteTask.IsCompleted);
		Assert.Contains("/wabisabi/status", coordinator.Requests);
		Assert.Equal(coordinator.Uri, global.CoordinatorUri);

		// Connecting to the coordinator that the application has changes nothing.
		result = await global.ConnectToCoordinatorAsync(coordinator.Uri.ToString(), cancellationToken);
		Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
		Assert.Same(manager, global.HostedServices.Get<CoinJoinManager>());
		Assert.Equal(coordinator.Uri, global.CoordinatorUri);

		// A coordinator that does not answer does not take the place of the one that the application has.
		result = await global.ConnectToCoordinatorAsync(nobody, cancellationToken);
		Assert.False(result.IsOk);
		Assert.Empty(otherCoordinator.Requests);
		Assert.Equal(coordinator.Uri, global.CoordinatorUri);

		// Neither does one that answers when it is only asked.
		result = await global.VerifyCoordinatorAsync(otherCoordinator.Uri.ToString(), cancellationToken);
		Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
		Assert.Equal(coordinator.Uri, global.CoordinatorUri);

		// Another one that answers does. Whoever coinjoins is the same as before.
		result = await global.ConnectToCoordinatorAsync(otherCoordinator.Uri.ToString(), cancellationToken);

		Assert.True(result.IsOk, result.IsOk ? "" : result.Error);
		Assert.Same(manager, global.HostedServices.Get<CoinJoinManager>());
		Assert.False(manager.ExecuteTask.IsCompleted);
		Assert.Contains("/wabisabi/status", otherCoordinator.Requests);
		Assert.Equal(otherCoordinator.Uri, global.CoordinatorUri);

		// What coinjoins was there already: it is announced once, when it comes about.
		Assert.Equal(1, announcements);
	}
}
