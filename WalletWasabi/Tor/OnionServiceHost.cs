using System.IO;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Exceptions;
using WalletWasabi.Tor.Control.Messages;

namespace WalletWasabi.Tor;

/// <summary>How far an onion service of the application has come.</summary>
public enum OnionServiceStatus
{
	/// <summary>Nobody asked for the service, or it was taken down.</summary>
	Stopped,

	/// <summary>The service is wanted, and Tor is not there to make it: it is starting, or starting again.</summary>
	WaitingForTor,

	/// <summary>Tor made the service, and has not told yet that its address is published. Visitors may not find it yet.</summary>
	Publishing,

	/// <summary>The address is published: visitors can find the service.</summary>
	Published,

	/// <summary>The service could not be made. It is tried again after a while.</summary>
	Failed
}

/// <param name="Host">The onion address of the service, once Tor made it.</param>
/// <param name="Problem">Why there is no service, if it could not be made.</param>
public sealed record OnionServiceState(OnionServiceStatus Status, string? Host = null, string? Problem = null)
{
	public static OnionServiceState Stopped { get; } = new(OnionServiceStatus.Stopped);
}

/// <summary>
/// Waits for the Tor of the application to run.
/// </summary>
/// <returns>
/// A token that is cancelled when that Tor stops, and the connection to its control port. There is no such connection
/// to a Tor that the application did not start.
/// </returns>
public delegate Task<(CancellationToken TorStopped, TorControlClient? Client)> WaitForTor(CancellationToken cancellationToken);

/// <summary>
/// Keeps an onion service of the application's Tor up: a port of an onion address that leads to a port of this
/// computer. Tor forgets the service when it stops, so the service is made again whenever Tor starts again, with the
/// key of the first time, which gives it the same address every time.
/// </summary>
/// <remarks>
/// The service belongs to the connection that the application has to the control port of its Tor. It is gone once the
/// application is closed, also when Tor is left running. A Tor that the application did not start has no control port
/// for the application, so it cannot be asked for a service.
/// </remarks>
public sealed class OnionServiceHost : IAsyncDisposable
{
	/// <summary>What is told when the Tor in use cannot be asked for an onion service.</summary>
	public const string NoControlPortProblem = "This Tor was not started by the application, so it cannot be asked for an onion address.";

	private readonly WaitForTor _waitForTor;
	private readonly OnionServiceKeyFile _keyFile;
	private readonly OnionServiceOptions _options;
	private readonly TimeSpan _retryDelay;
	private readonly CancellationTokenSource _stopCts = new();
	private readonly Lock _lock = new();

	private OnionServiceState _state = OnionServiceState.Stopped;
	private TaskCompletionSource _stateChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task? _loop;
	private bool _isDisposed;

	/// <param name="options">What the service is like. Its key is the one of the file.</param>
	/// <param name="retryDelay">How long to wait after the service could not be made, before it is tried again.</param>
	public OnionServiceHost(WaitForTor waitForTor, OnionServiceKeyFile keyFile, OnionServiceOptions options, TimeSpan? retryDelay = null)
	{
		_waitForTor = waitForTor;
		_keyFile = keyFile;
		_options = options;
		_retryDelay = retryDelay ?? TimeSpan.FromSeconds(15);
	}

	/// <summary>Tells every change of <see cref="State"/>. It is called from whatever thread the change happens on.</summary>
	public event EventHandler<OnionServiceState>? StateChanged;

	public OnionServiceState State
	{
		get
		{
			lock (_lock)
			{
				return _state;
			}
		}
	}

	/// <summary>Starts to keep the service up. It returns at once: <see cref="State"/> tells how far it is.</summary>
	public void Start()
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			if (_loop is not null)
			{
				return;
			}

			_state = new OnionServiceState(OnionServiceStatus.WaitingForTor);
			_loop = Task.Run(() => KeepUpAsync(_stopCts.Token));
		}
	}

	/// <summary>Waits until the state is one that the caller does not want to wait through any longer.</summary>
	/// <param name="isPending">Whether a state is one to wait through.</param>
	/// <returns>The first state that is not.</returns>
	public async Task<OnionServiceState> WaitWhileAsync(Func<OnionServiceState, bool> isPending, CancellationToken cancellationToken)
	{
		while (true)
		{
			OnionServiceState state;
			Task changed;

			lock (_lock)
			{
				state = _state;
				changed = _stateChanged.Task;
			}

			if (!isPending(state))
			{
				return state;
			}

			await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Takes the service down. Tor is told to forget it, so that its address leads nowhere from now on.</summary>
	public async ValueTask DisposeAsync()
	{
		Task? loop;

		lock (_lock)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			loop = _loop;
		}

		await _stopCts.CancelAsync().ConfigureAwait(false);

		if (loop is not null)
		{
			await loop.ConfigureAwait(false);
		}

		_stopCts.Dispose();
		Set(OnionServiceState.Stopped);
	}

	private void Set(OnionServiceState state)
	{
		TaskCompletionSource changed;

		lock (_lock)
		{
			if (_state == state)
			{
				return;
			}

			_state = state;
			changed = _stateChanged;
			_stateChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		changed.TrySetResult();
		StateChanged?.Invoke(this, state);
	}

	private async Task KeepUpAsync(CancellationToken stop)
	{
		while (!stop.IsCancellationRequested)
		{
			try
			{
				// Why the service could not be made the last time stays what is told until it is made, or cannot be made for another reason.
				if (State.Status != OnionServiceStatus.Failed)
				{
					Set(new OnionServiceState(OnionServiceStatus.WaitingForTor));
				}

				var (torStopped, client) = await _waitForTor(stop).ConfigureAwait(false);

				if (client is null)
				{
					Set(new OnionServiceState(OnionServiceStatus.Failed, Problem: NoControlPortProblem));
					await WaitForAnyAsync(torStopped, stop).ConfigureAwait(false);
					continue;
				}

				await ServeAsync(client, torStopped, stop).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// The service is taken down, or Tor stopped while it was waited for: then it is waited for again. A Tor
				// that is not coming back is not asked for without a pause.
				await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"The onion service could not be made: {ex.Message}");
				Logger.LogDebug(ex);
				Set(new OnionServiceState(OnionServiceStatus.Failed, Problem: Describe(ex)));

				await Task.Delay(_retryDelay, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			}
		}
	}

	/// <summary>Makes the service with one Tor, and stays until that Tor stops or the service is taken down.</summary>
	private async Task ServeAsync(TorControlClient client, CancellationToken torStopped, CancellationToken stop)
	{
		// Tor tells that the address is published in an event, so the events are listened to before there is anything to tell.
		using var events = await client.SubscribeEventsAsync(["HS_DESC"], stop).ConfigureAwait(false);

		var key = _keyFile.Read();
		var service = await client.CreateOnionServiceAsync(_options with { PrivateKey = key }, stop).ConfigureAwait(false);

		try
		{
			if (key is null)
			{
				// Without the key the address would be another one the next time, so an address that cannot be kept is not given out.
				_keyFile.Write(service.PrivateKey);
			}

			Set(new OnionServiceState(OnionServiceStatus.Publishing, service.Host));
			Logger.LogInfo($"Tor made the onion service {service.Host}.");

			using var untilGone = CancellationTokenSource.CreateLinkedTokenSource(torStopped, stop);

			// The events end when the connection to Tor does.
			await foreach (var torEvent in events.ReadAllAsync(untilGone.Token).ConfigureAwait(false))
			{
				if (OnionService.IsPublished(torEvent, service.ServiceId) && State.Status != OnionServiceStatus.Published)
				{
					Set(new OnionServiceState(OnionServiceStatus.Published, service.Host));
					Logger.LogInfo($"The onion service {service.Host} is published.");
				}
			}

			// The connection to Tor ended, which is how a Tor that stops looks from here. It takes a moment until it is
			// known to have stopped, and to be starting again.
			Set(new OnionServiceState(OnionServiceStatus.WaitingForTor));

			using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await WaitForAnyAsync(torStopped, stop, graceCts.Token).ConfigureAwait(false);
		}
		finally
		{
			// A Tor that still runs keeps the service for as long as the application is connected to it, so it is told
			// to forget it. A Tor that stopped has forgotten it.
			if (!torStopped.IsCancellationRequested)
			{
				await ForgetAsync(client, service).ConfigureAwait(false);
			}
		}
	}

	private static async Task ForgetAsync(TorControlClient client, OnionService service)
	{
		try
		{
			using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await client.DestroyOnionServiceAsync(service.ServiceId, timeoutCts.Token).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			// Tor may be gone by now, and then so is the service.
			Logger.LogDebug(ex);
		}
	}

	/// <summary>Waits until one of the tokens is cancelled.</summary>
	private static async Task WaitForAnyAsync(params CancellationToken[] tokens)
	{
		using var any = CancellationTokenSource.CreateLinkedTokenSource(tokens);
		await Task.Delay(Timeout.InfiniteTimeSpan, any.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
	}

	private string Describe(Exception exception) =>
		exception switch
		{
			InvalidDataException or IOException or UnauthorizedAccessException =>
				$"The file with the key of the onion address cannot be used ({_keyFile.Path}): {exception.Message}",
			_ => exception.Message
		};
}
