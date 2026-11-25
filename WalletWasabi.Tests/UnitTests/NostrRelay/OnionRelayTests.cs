using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The Nostr relay of the application is reached at an onion address and nowhere else: Tor is asked for an onion
/// service that leads to the port of this computer where the relay listens, and the relay answers to that address only.
/// </summary>
public class OnionRelayTests
{
	private static readonly string Host = $"{FakeTor.DefaultServiceId}.onion";

	private static async Task<OnionServiceKeyFile> NewKeyFileAsync([System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
		new(Path.Combine(await Common.GetEmptyWorkDirAsync(callerMemberName: testName), "NostrRelay", "OnionServiceKey"));

	/// <summary>
	/// Tor is asked for an address that leads to the relay, with its guards: a cap on the connections of one circuit,
	/// and work for the visitors when too many come. The relay answers to the address from the moment that Tor made it.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayIsReachedAtItsOnionAddressAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();
		await using var relay = new FakeLocalRelay(port: 45123);

		await using var tor = new FakeTor();
		await using var onionRelay = new OnionRelay(relay, _ => Task.FromResult(tor.Attempt), keyFile);

		Assert.Equal(NostrRelayState.Off, onionRelay.State);
		Assert.Null(relay.OnionHost);

		onionRelay.Start();

		Assert.Equal(
			"ADD_ONION NEW:ED25519-V3 Flags=MaxStreamsCloseCircuit MaxStreams=4 PoWDefensesEnabled=1 PoWQueueRate=5 PoWQueueBurst=50 Port=80,127.0.0.1:45123",
			await tor.WaitForCommandAsync("ADD_ONION", timeoutCts.Token));

		var made = await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Starting, timeoutCts.Token);

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Publishing, new Uri($"ws://{Host}")), made);
		Assert.Equal(Host, relay.OnionHost);

		await tor.PublishAsync();

		var running = await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Publishing, timeoutCts.Token);

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Running, new Uri($"ws://{Host}")), running);
		Assert.Equal(running, onionRelay.State);
		Assert.True(running.IsOn);
	}

	/// <summary>
	/// While Tor is away there is no onion address, and the relay answers to nobody. It answers to its address again
	/// once the Tor that was started in its place has made it again.
	/// </summary>
	[Fact(Timeout = 120000)]
	public async Task RelayAnswersToNobodyWhileTorIsAwayAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();
		await using var relay = new FakeLocalRelay(port: 45123);

		await using var firstTor = new FakeTor();
		await using var secondTor = new FakeTor();

		var currentTor = firstTor;
		var secondTorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		async Task<(CancellationToken, TorControlClient?)> WaitForTorAsync(CancellationToken cancellationToken)
		{
			if (Volatile.Read(ref currentTor) == firstTor && firstTor.Stopped.IsCancellationRequested)
			{
				await secondTorStarted.Task.WaitAsync(cancellationToken);
				Volatile.Write(ref currentTor, secondTor);
			}

			return Volatile.Read(ref currentTor).Attempt;
		}

		await using var onionRelay = new OnionRelay(relay, WaitForTorAsync, keyFile);

		onionRelay.Start();
		await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Starting, timeoutCts.Token);
		Assert.Equal(Host, relay.OnionHost);

		await firstTor.StopAsync();

		var waiting = await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Publishing, timeoutCts.Token);

		Assert.Equal(new NostrRelayState(NostrRelayStatus.Starting), waiting);
		Assert.Null(relay.OnionHost);

		secondTorStarted.SetResult();

		await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Starting, timeoutCts.Token);
		Assert.Equal(Host, relay.OnionHost);
	}

	/// <summary>Stopping takes the address down first, and then closes the relay.</summary>
	[Fact(Timeout = 120000)]
	public async Task StoppingTakesTheAddressDownAndClosesTheRelayAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		var keyFile = await NewKeyFileAsync();
		await using var relay = new FakeLocalRelay(port: 45123);

		await using var tor = new FakeTor();
		var onionRelay = new OnionRelay(relay, _ => Task.FromResult(tor.Attempt), keyFile);
		NostrRelayState? last = null;
		onionRelay.StateChanged += (_, state) => last = state;

		onionRelay.Start();
		await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Starting, timeoutCts.Token);

		await onionRelay.DisposeAsync();

		Assert.Equal(NostrRelayState.Off, onionRelay.State);
		Assert.Equal(NostrRelayState.Off, last);
		Assert.Null(relay.OnionHost);
		Assert.True(relay.IsDisposed);
		Assert.True(relay.WasAddressDownWhenDisposed);
		Assert.Equal($"DEL_ONION {FakeTor.DefaultServiceId}", tor.Commands[^1]);

		await onionRelay.DisposeAsync();
	}

	/// <summary>A relay that cannot get an onion address tells why, and answers to nobody.</summary>
	[Fact(Timeout = 120000)]
	public async Task RelayWithoutAnAddressTellsWhyAsync()
	{
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		using var torStoppedCts = new CancellationTokenSource();
		var keyFile = await NewKeyFileAsync();
		await using var relay = new FakeLocalRelay(port: 45123);

		await using var onionRelay = new OnionRelay(relay, _ => Task.FromResult((torStoppedCts.Token, (TorControlClient?)null)), keyFile);

		onionRelay.Start();

		var state = await onionRelay.WaitWhileAsync(state => state.Status == NostrRelayStatus.Starting, timeoutCts.Token);

		Assert.Equal(new NostrRelayState(NostrRelayStatus.NotWorking, Problem: OnionServiceHost.NoControlPortProblem), state);
		Assert.True(state.IsOn);
		Assert.Null(relay.OnionHost);
	}

	[Fact]
	public void StateOfTheRelayFollowsFromItsOnionService()
	{
		Assert.Equal(NostrRelayState.Off, NostrRelayState.From(OnionServiceState.Stopped));
		Assert.False(NostrRelayState.Off.IsOn);
		Assert.Equal(new NostrRelayState(NostrRelayStatus.Starting), NostrRelayState.From(new OnionServiceState(OnionServiceStatus.WaitingForTor)));
		Assert.Equal(
			new NostrRelayState(NostrRelayStatus.Publishing, new Uri($"ws://{Host}/")),
			NostrRelayState.From(new OnionServiceState(OnionServiceStatus.Publishing, Host)));
		Assert.Equal(
			new NostrRelayState(NostrRelayStatus.Running, new Uri($"ws://{Host}/")),
			NostrRelayState.From(new OnionServiceState(OnionServiceStatus.Published, Host)));
		Assert.Equal(
			new NostrRelayState(NostrRelayStatus.NotWorking, Problem: "Tor refused."),
			NostrRelayState.From(new OnionServiceState(OnionServiceStatus.Failed, Problem: "Tor refused.")));
	}

	/// <summary>The address is shown the way a Nostr client is told it: a WebSocket address without a port and without a path.</summary>
	[Fact]
	public void AddressIsAWebSocketAddressAtTheOnionHost()
	{
		var address = NostrRelayState.GetAddress(Host);

		Assert.Equal("ws", address.Scheme);
		Assert.Equal(Host, address.Host);
		Assert.Equal(80, address.Port);
		Assert.Equal($"ws://{Host}", NostrRelayState.Format(address));
	}

	/// <summary>Stands in for the relay: it has a port, and it is told which host it answers to.</summary>
	private sealed class FakeLocalRelay(int port) : ILocalRelay
	{
		private string? _onionHost;

		public IPEndPoint EndPoint { get; } = new(IPAddress.Loopback, port);

		public string? OnionHost
		{
			get => Volatile.Read(ref _onionHost);
			set => Volatile.Write(ref _onionHost, value);
		}

		public bool IsDisposed { get; private set; }

		/// <summary>Whether the relay had stopped answering to its address by the time it was closed.</summary>
		public bool WasAddressDownWhenDisposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			// The test closes it once more when it is over, which tells nothing.
			if (!IsDisposed)
			{
				WasAddressDownWhenDisposed = OnionHost is null;
				IsDisposed = true;
			}

			return ValueTask.CompletedTask;
		}
	}
}
