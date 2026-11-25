using System.Net;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control.Messages;

namespace WalletWasabi.NostrRelay;

/// <summary>A Nostr relay that listens on a port of this computer, where nothing from outside of the computer reaches it.</summary>
public interface ILocalRelay : IAsyncDisposable
{
	/// <summary>Where the relay listens.</summary>
	IPEndPoint EndPoint { get; }

	/// <summary>
	/// The host that visitors have to ask for: the onion address of the relay. Whoever asks for another host is turned
	/// away, and so is everybody as long as there is none.
	/// </summary>
	string? OnionHost { get; set; }
}

/// <summary>
/// A Nostr relay of the application that is reached at an onion address and nowhere else. The relay itself listens on
/// this computer only, and Tor leads the visitors of the onion address to it, so no visitor learns where the
/// computer is.
/// </summary>
/// <remarks>
/// The address stays the same for as long as the key file is kept. Tor guards the address: one circuit of a visitor
/// holds a few connections at most, and visitors have to work for their turn once more of them come than are let in.
/// </remarks>
public sealed class OnionRelay : IAsyncDisposable
{
	/// <summary>The port of the onion address. It is the one that a WebSocket address has when it names none.</summary>
	public const int OnionPort = 80;

	/// <summary>How many connections a visitor may have open at once over one circuit. A client keeps one.</summary>
	public const int MaxConnectionsPerCircuit = 4;

	/// <summary>How many visitors Tor lets in per second when that many come. Beyond that they queue, and work for their turn.</summary>
	public const int VisitorsPerSecond = 5;

	/// <summary>How many visitors Tor lets in at once after a quiet time.</summary>
	public const int VisitorsAtOnce = 50;

	private readonly ILocalRelay _relay;
	private readonly OnionServiceHost _onionService;
	private int _isDisposed;

	/// <param name="relay">The relay. It is closed with this object.</param>
	/// <param name="keyFile">Keeps the key that the onion address follows from.</param>
	public OnionRelay(ILocalRelay relay, WaitForTor waitForTor, OnionServiceKeyFile keyFile, TimeSpan? retryDelay = null)
	{
		_relay = relay;

		_onionService = new OnionServiceHost(waitForTor, keyFile, CreateOptions(relay.EndPoint), retryDelay);
		_onionService.StateChanged += OnOnionServiceStateChanged;
	}

	/// <summary>Tells every change of <see cref="State"/>. It is called from whatever thread the change happens on.</summary>
	public event EventHandler<NostrRelayState>? StateChanged;

	public NostrRelayState State => NostrRelayState.From(_onionService.State);

	/// <summary>What Tor is asked for: an onion service that leads to the relay, with its guards.</summary>
	public static OnionServiceOptions CreateOptions(IPEndPoint relayEndPoint) =>
		new(OnionPort, relayEndPoint)
		{
			MaxStreamsPerCircuit = MaxConnectionsPerCircuit,
			ProofOfWork = new OnionServiceProofOfWork(VisitorsPerSecond, VisitorsAtOnce)
		};

	/// <summary>Starts to keep the relay reachable. It returns at once: <see cref="State"/> tells how far it is.</summary>
	public void Start() => _onionService.Start();

	/// <summary>Waits until the state is one that the caller does not want to wait through any longer.</summary>
	/// <returns>The first state that is not.</returns>
	public async Task<NostrRelayState> WaitWhileAsync(Func<NostrRelayState, bool> isPending, CancellationToken cancellationToken) =>
		NostrRelayState.From(await _onionService.WaitWhileAsync(state => isPending(NostrRelayState.From(state)), cancellationToken).ConfigureAwait(false));

	/// <summary>Takes the onion address down, and then closes the relay.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
		{
			return;
		}

		await _onionService.DisposeAsync().ConfigureAwait(false);
		_onionService.StateChanged -= OnOnionServiceStateChanged;

		_relay.OnionHost = null;
		await _relay.DisposeAsync().ConfigureAwait(false);
	}

	private void OnOnionServiceStateChanged(object? sender, OnionServiceState state)
	{
		// The relay answers to its onion address from the moment that there is one, and to nothing before.
		_relay.OnionHost = state.Host;

		StateChanged?.Invoke(this, NostrRelayState.From(state));
	}
}
