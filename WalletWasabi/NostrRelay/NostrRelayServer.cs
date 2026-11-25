using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// The Nostr relay of the application. It listens on a port of this computer that nothing outside of the computer
/// reaches, takes the events of the set that its user chose, from the keys that its user lets in, keeps them for a
/// day, and hands them to whoever asks or listens.
/// </summary>
/// <remarks>
/// <para>
/// The relay speaks the basic protocol of Nostr: a client sends events, subscribes with filters, gets the stored
/// events that the filters ask for and then the ones that come in, and closes its subscriptions. A client that asks
/// the relay about itself is told its name, what it takes and its limits.
/// </para>
/// <para>
/// Visitors come through Tor, to the onion address that <see cref="OnionRelay"/> gets for the relay, and the relay
/// answers nobody who asks for another host than that address. It opens no connection of its own to anywhere.
/// </para>
/// </remarks>
public sealed class NostrRelayServer : ILocalRelay
{
	private static readonly TimeSpan ExpiryInterval = TimeSpan.FromSeconds(30);

	private readonly Func<DateTimeOffset> _now;
	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _stopCts = new();
	private readonly ConcurrentDictionary<long, RelayConnection> _connections = new();
	private readonly Task _acceptLoop;
	private readonly Task _expiryLoop;

	private long _lastConnectionId;
	private volatile string? _onionHost;
	private int _isDisposed;

	/// <param name="events">The events that the relay takes.</param>
	/// <param name="storeFolder">The folder for the file that keeps the events, see <see cref="RelayEventSet.StoreFileName"/>. Without one the events are kept in memory, and are gone when the relay stops.</param>
	/// <param name="port">The port to listen on, or 0 for one that is free.</param>
	/// <param name="limits">Other limits than the ones that go with the events, for a test that needs a limit within its reach.</param>
	/// <param name="senders">Whose events the relay takes. Without rules it takes events from anybody.</param>
	/// <param name="checkBond">Asks the node about a fidelity bond that a key shows. Without it no bond counts.</param>
	/// <param name="getTipHeight">Tells the height of the chain, if it is known.</param>
	/// <exception cref="SocketException">The port is taken.</exception>
	public NostrRelayServer(
		RelayEventSet events,
		string? storeFolder = null,
		int port = 0,
		Func<DateTimeOffset>? now = null,
		RelayLimits? limits = null,
		RelaySenderRules? senders = null,
		CheckBond? checkBond = null,
		Func<uint?>? getTipHeight = null)
	{
		Events = events;
		Limits = limits ?? events.Limits;
		Information = RelayInformation.Create(events, senders ?? RelaySenderRules.Anyone);
		_now = now ?? (() => DateTimeOffset.UtcNow);

		Store = RelayEventStore.Open(
			storeFolder is null ? null : Path.Combine(storeFolder, events.StoreFileName),
			Limits,
			servedKinds: (events as CustomEventSet)?.Kinds);

		try
		{
			Senders = new RelaySenders(senders ?? RelaySenderRules.Anyone, Store, Limits, checkBond, getTipHeight);

			// The address of this computer that only this computer reaches. The relay listens nowhere else.
			_listener = new TcpListener(IPAddress.Loopback, port);
			_listener.Start();
		}
		catch
		{
			Senders?.Dispose();
			Store.Dispose();
			_stopCts.Dispose();
			throw;
		}

		EndPoint = (IPEndPoint)_listener.LocalEndpoint;

		_acceptLoop = Task.Run(AcceptLoopAsync);
		_expiryLoop = Task.Run(ExpiryLoopAsync);
	}

	public IPEndPoint EndPoint { get; }

	public string? OnionHost
	{
		get => _onionHost;
		set => _onionHost = value;
	}

	/// <summary>The events that the relay takes.</summary>
	public RelayEventSet Events { get; }

	public RelayLimits Limits { get; }

	/// <summary>How many visitors are connected at the moment.</summary>
	public int ConnectionCount => _connections.Count;

	/// <summary>How many events the relay keeps at the moment.</summary>
	public long EventCount => Store.Count;

	internal RelayEventStore Store { get; }

	/// <summary>Decides whose events the relay takes.</summary>
	internal RelaySenders Senders { get; }

	/// <summary>What the relay tells about itself, as JSON.</summary>
	internal string Information { get; }

	/// <summary>The time, in seconds since 1970.</summary>
	internal long GetTime() => _now().ToUnixTimeSeconds();

	/// <summary>Hands an event that the relay just took to every visitor that subscribed to such events.</summary>
	internal void Broadcast(RelayEvent relayEvent)
	{
		foreach (var connection in _connections.Values)
		{
			connection.Offer(relayEvent);
		}
	}

	/// <summary>Stops listening, disconnects the visitors, and closes the store.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
		{
			return;
		}

		await _stopCts.CancelAsync().ConfigureAwait(false);
		_listener.Stop();

		await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		await _expiryLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

		var connections = _connections.Values.ToArray();

		foreach (var connection in connections)
		{
			connection.Abort();
		}

		await Task.WhenAll(connections.Select(connection => connection.Completion))
			.WaitAsync(TimeSpan.FromSeconds(10))
			.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

		_listener.Dispose();
		Senders.Dispose();
		Store.Dispose();
		_stopCts.Dispose();
	}

	private async Task AcceptLoopAsync()
	{
		while (!_stopCts.IsCancellationRequested)
		{
			TcpClient client;

			try
			{
				client = await _listener.AcceptTcpClientAsync(_stopCts.Token).ConfigureAwait(false);
			}
			catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
			{
				break;
			}
			catch (SocketException ex)
			{
				// One visitor that could not be let in is no reason to let nobody in from now on.
				Logger.LogDebug(ex);
				continue;
			}

			// Nothing but this computer reaches the port. Should that ever not hold, whatever comes from elsewhere is turned away.
			if (client.Client.RemoteEndPoint is not IPEndPoint { Address: var address } || !IPAddress.IsLoopback(address))
			{
				client.Dispose();
				continue;
			}

			client.NoDelay = true;
			MakeRoomForVisitor();

			var connection = new RelayConnection(this, client, Interlocked.Increment(ref _lastConnectionId), _stopCts.Token);

			_connections[connection.Id] = connection;
			connection.Completion = Task.Run(() => ServeAsync(connection));
		}
	}

	/// <summary>When as many visitors are connected as may be, the one that was silent the longest goes, so that a new one can come.</summary>
	private void MakeRoomForVisitor()
	{
		while (_connections.Count >= Limits.MaxConnections && _connections.Values.MinBy(connection => connection.LastActivity) is { } idlest)
		{
			_connections.TryRemove(idlest.Id, out _);
			idlest.Abort();
		}
	}

	private async Task ServeAsync(RelayConnection connection)
	{
		try
		{
			await connection.RunAsync().ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or System.IO.IOException or System.Net.WebSockets.WebSocketException)
		{
			// The visitor left in the middle of something, or took too long, or was made to go.
		}
		catch (Exception ex)
		{
			Logger.LogWarning($"A visitor of the Nostr relay was disconnected: {ex.Message}");
			Logger.LogDebug(ex);
		}
		finally
		{
			_connections.TryRemove(connection.Id, out _);
			connection.Dispose();
		}
	}

	/// <summary>Forgets the events that have expired, and the fidelity bonds that no longer stand behind a key, every now and then.</summary>
	private async Task ExpiryLoopAsync()
	{
		using var timer = new PeriodicTimer(ExpiryInterval);

		try
		{
			while (await timer.WaitForNextTickAsync(_stopCts.Token).ConfigureAwait(false))
			{
				try
				{
					var now = GetTime();

					Store.DeleteExpired(now);
					Senders.Sweep(now);
				}
				catch (Exception ex) when (ex is not ObjectDisposedException)
				{
					Logger.LogWarning($"The Nostr relay could not forget its expired events: {ex.Message}");
				}
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
		{
			// The relay stops.
		}
	}
}
