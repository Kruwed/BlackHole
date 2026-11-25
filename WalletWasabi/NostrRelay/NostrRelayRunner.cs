using System.IO;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Nito.AsyncEx;
using WalletWasabi.Models;
using WalletWasabi.Tor;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// Runs the Nostr relay of the application the way its user chose: it starts the relay, stops it, starts it again
/// when the user chose other events for it, and tells how it stands with it.
/// </summary>
/// <remarks>
/// The relay keeps two files in its folder: the key that its onion address follows from, and the events that it keeps
/// when it takes the kinds that the user named. The events of Black Hole are kept in memory only.
/// </remarks>
public sealed class NostrRelayRunner : IAsyncDisposable
{
	/// <summary>The file with the key that the onion address of the relay follows from.</summary>
	public const string KeyFileName = "OnionServiceKey";

	/// <summary>The file with the events that the relay keeps when it takes the kinds that the user named.</summary>
	public const string EventsFileName = "Events.sqlite";

	/// <summary>How long the relay is given to have its onion address published when the user turns it on. It goes on trying after that.</summary>
	public static readonly TimeSpan StartPatience = TimeSpan.FromMinutes(3);

	private readonly Network _network;
	private readonly string _folder;
	private readonly WaitForTor _waitForTor;
	private readonly string? _obstacle;
	private readonly AsyncLock _lock = new();

	private volatile OnionRelay? _relay;
	private volatile NostrRelaySettings? _settings;
	private volatile NostrRelayState? _failure;
	private bool _isDisposed;

	/// <param name="network">The network of the wallet, which the events of Black Hole are for.</param>
	/// <param name="folder">Where the relay keeps its files.</param>
	/// <param name="obstacle">Why no relay can run in this application as it was started, see <see cref="DescribeObstacle"/>. Null if one can.</param>
	public NostrRelayRunner(Network network, string folder, WaitForTor waitForTor, string? obstacle)
	{
		_network = network;
		_folder = folder;
		_waitForTor = waitForTor;
		_obstacle = obstacle;
	}

	/// <summary>Tells every change of <see cref="State"/>. It is called from whatever thread the change happens on.</summary>
	public event EventHandler<NostrRelayState>? StateChanged;

	/// <summary>How it stands with the relay.</summary>
	public NostrRelayState State => _relay?.State ?? _failure ?? NostrRelayState.Off;

	/// <summary>What the relay runs with. Null if none runs.</summary>
	public NostrRelaySettings? Settings => _settings;

	/// <summary>Why no relay can run in this application as it was started. Null if one can.</summary>
	public string? Obstacle => _obstacle;

	/// <summary>
	/// Tells why no relay can run with the way the application uses Tor. A relay is reached at an onion address and
	/// nowhere else, and the application can only ask a Tor for such an address that it started itself.
	/// </summary>
	/// <param name="isRegTest">On regtest the application does not use Tor, whatever the setting says.</param>
	/// <returns>Null if a relay can run.</returns>
	public static string? DescribeObstacle(TorMode torMode, bool isRegTest) =>
		torMode switch
		{
			TorMode.Enabled => null,
			TorMode.EnabledOnlyRunning =>
				$"A relay is reached at an onion address, which {Constants.AppName} asks its own Tor for. With Tor set to \"Connect Only\" it uses a Tor that it did not start, and that Tor cannot be asked.",
			_ when isRegTest => "A relay is reached at an onion address, and Tor is not used on regtest.",
			_ => "A relay is reached at an onion address, and Tor is turned off."
		};

	/// <summary>
	/// Sees to it that the relay runs with the given choice of events. A relay that runs with another choice is
	/// stopped and started again: its address stays the same.
	/// </summary>
	/// <param name="patience">How long to wait for the onion address to be published before telling how it stands. The relay goes on trying after that.</param>
	/// <returns>How it stands with the relay after that time, or as soon as it runs or cannot run.</returns>
	public async Task<NostrRelayState> RunAsync(NostrRelaySettings settings, TimeSpan patience, CancellationToken cancellationToken)
	{
		OnionRelay? relay;

		using (await _lock.LockAsync(cancellationToken).ConfigureAwait(false))
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			if (_relay is { } running && _settings == settings)
			{
				relay = running;
			}
			else
			{
				await StopNoLockAsync().ConfigureAwait(false);
				relay = await StartNoLockAsync(settings).ConfigureAwait(false);
			}
		}

		if (relay is null)
		{
			return State;
		}

		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeoutCts.CancelAfter(patience);

		try
		{
			return await relay
				.WaitWhileAsync(state => state.Status is NostrRelayStatus.Starting or NostrRelayStatus.Publishing, timeoutCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return relay.State;
		}
	}

	/// <summary>Stops the relay: its onion address leads nowhere from now on. The key of the address and the events in the file stay.</summary>
	public async Task StopAsync()
	{
		using (await _lock.LockAsync().ConfigureAwait(false))
		{
			await StopNoLockAsync().ConfigureAwait(false);
		}
	}

	public async ValueTask DisposeAsync()
	{
		using (await _lock.LockAsync().ConfigureAwait(false))
		{
			_isDisposed = true;
			await StopNoLockAsync().ConfigureAwait(false);
		}
	}

	/// <returns>The relay, or null if it could not be started: <see cref="State"/> tells why then.</returns>
	private async Task<OnionRelay?> StartNoLockAsync(NostrRelaySettings settings)
	{
		if (_obstacle is not null || settings.Problem is not null)
		{
			Fail(_obstacle ?? settings.Problem!);
			return null;
		}

		NostrRelayServer? server = null;

		try
		{
			CreateFolder(_folder);

			server = new NostrRelayServer(settings.CreateEventSet(_network), Path.Combine(_folder, EventsFileName));

			var relay = new OnionRelay(server, _waitForTor, new OnionServiceKeyFile(Path.Combine(_folder, KeyFileName)));

			// The relay looks after the server from here on.
			server = null;

			relay.StateChanged += OnRelayStateChanged;

			_failure = null;
			_settings = settings;
			_relay = relay;

			relay.Start();
			Logger.LogInfo("The Nostr relay is started, and waits for its onion address.");
			StateChanged?.Invoke(this, State);

			return relay;
		}
		catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or SocketException)
		{
			Logger.LogWarning($"The Nostr relay could not be started: {ex.Message}");
			Fail($"The relay could not be started: {ex.Message}");
			return null;
		}
		finally
		{
			if (server is not null)
			{
				await server.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	/// <summary>Makes the folder of the relay, for the user of this computer only where the system knows of such a rule.</summary>
	private static void CreateFolder(string folder)
	{
		if (Directory.Exists(folder))
		{
			return;
		}

		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(folder);
		}
		else
		{
			Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	private async Task StopNoLockAsync()
	{
		var relay = _relay;
		var hadSomething = relay is not null || _failure is not null;

		_relay = null;
		_settings = null;
		_failure = null;

		if (relay is not null)
		{
			relay.StateChanged -= OnRelayStateChanged;
			await relay.DisposeAsync().ConfigureAwait(false);
			Logger.LogInfo("The Nostr relay is stopped.");
		}

		if (hadSomething)
		{
			StateChanged?.Invoke(this, NostrRelayState.Off);
		}
	}

	private void Fail(string problem)
	{
		_failure = new NostrRelayState(NostrRelayStatus.NotWorking, Problem: problem);
		StateChanged?.Invoke(this, State);
	}

	private void OnRelayStateChanged(object? sender, NostrRelayState state)
	{
		if (ReferenceEquals(sender, _relay))
		{
			StateChanged?.Invoke(this, state);
		}
	}
}
