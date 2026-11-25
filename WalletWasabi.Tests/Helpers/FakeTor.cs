using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Tor;
using WalletWasabi.Tor.Control;
using WalletWasabi.Tor.Control.Messages;

namespace WalletWasabi.Tests.Helpers;

/// <summary>
/// Stands in for the control port of a Tor in tests: it takes the commands that ask for an onion service and answers
/// them the way Tor does, and it tells when it is told to that the address of a service is published.
/// </summary>
public sealed class FakeTor : IAsyncDisposable
{
	/// <summary>A name as onion services have them. It is made up: no service has it.</summary>
	public static readonly string DefaultServiceId = new string('a', 55) + "d";

	/// <summary>A key in the form in which Tor tells keys. It is made up as well.</summary>
	public static readonly string DefaultKey = "ED25519-V3:" + Convert.ToBase64String([.. Enumerable.Range(0, 64).Select(i => (byte)i)]);

	private readonly Pipe _toServer = new();
	private readonly Pipe _toClient = new();
	private readonly CancellationTokenSource _stoppedCts = new();
	private readonly CancellationTokenSource _serveCts = new();
	private readonly Lock _lock = new();
	private readonly SemaphoreSlim _writeLock = new(1, 1);
	private readonly List<string> _commands = [];
	private readonly Task _serving;
	private TaskCompletionSource _commandReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public FakeTor()
	{
		Client = new TorControlClient(pipeReader: _toClient.Reader, pipeWriter: _toServer.Writer);
		_serving = Task.Run(ServeAsync);
	}

	/// <summary>The connection that the application has to this Tor.</summary>
	public TorControlClient Client { get; }

	/// <summary>Is cancelled when this Tor stops.</summary>
	public CancellationToken Stopped => _stoppedCts.Token;

	/// <summary>The name of the service that this Tor makes.</summary>
	public string ServiceId { get; init; } = DefaultServiceId;

	/// <summary>The key that this Tor makes for a new service.</summary>
	public string Key { get; init; } = DefaultKey;

	/// <summary>Answers a command in the place of this Tor, with all the lines of the answer. Null leaves the answer to this Tor.</summary>
	public Func<string, string?>? Answer { get; set; }

	/// <summary>The commands that this Tor got, in the order in which they came.</summary>
	public IReadOnlyList<string> Commands
	{
		get
		{
			lock (_lock)
			{
				return [.. _commands];
			}
		}
	}

	/// <summary>What <see cref="WaitForTor"/> gives for this Tor.</summary>
	public (CancellationToken TorStopped, TorControlClient? Client) Attempt => (Stopped, Client);

	/// <summary>Tells the application that the address of the service is published, the way Tor tells it.</summary>
	public async Task PublishAsync(string? serviceId = null)
	{
		var line = $"650 HS_DESC UPLOADED {serviceId ?? ServiceId} UNKNOWN $E9F71AC06F29B2110E3FC09016B0E50407444EE2~libertas\r\n";

		await WriteAsync(line);
	}

	/// <summary>Writes to the application. An event and the answer to a command may be due at the same time, and are written one after the other.</summary>
	private async Task WriteAsync(string text)
	{
		await _writeLock.WaitAsync(_serveCts.Token);

		try
		{
			await _toClient.Writer.WriteAsync(Encoding.ASCII.GetBytes(text), _serveCts.Token);
			await _toClient.Writer.FlushAsync(_serveCts.Token);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <summary>Waits until this Tor got a command that begins the given way and has answered it, and tells that command.</summary>
	public async Task<string> WaitForCommandAsync(string start, CancellationToken cancellationToken, int occurrence = 1)
	{
		while (true)
		{
			Task received;

			lock (_lock)
			{
				var matching = _commands.Where(command => command.StartsWith(start, StringComparison.Ordinal)).ToArray();

				if (matching.Length >= occurrence)
				{
					return matching[occurrence - 1];
				}

				received = _commandReceived.Task;
			}

			await received.WaitAsync(cancellationToken);
		}
	}

	/// <summary>Stops this Tor: the connection to it ends, and its services are gone.</summary>
	public async Task StopAsync()
	{
		if (_stoppedCts.IsCancellationRequested)
		{
			return;
		}

		await _serveCts.CancelAsync();

		try
		{
			await _serving;
		}
		catch (OperationCanceledException)
		{
		}

		await _toClient.Writer.CompleteAsync();
		await _stoppedCts.CancelAsync();
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		await Client.DisposeAsync();

		_stoppedCts.Dispose();
		_serveCts.Dispose();
		_writeLock.Dispose();
	}

	private async Task ServeAsync()
	{
		while (!_serveCts.IsCancellationRequested)
		{
			var command = await _toServer.Reader.ReadLineAsync(_serveCts.Token);

			await WriteAsync(Answer?.Invoke(command) ?? AnswerLikeTor(command));

			// Whoever waits for the command is told once it is answered: what they make this Tor say next comes after the answer.
			lock (_lock)
			{
				_commands.Add(command);

				var received = _commandReceived;
				_commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				received.TrySetResult();
			}
		}
	}

	private string AnswerLikeTor(string command) =>
		command.Split(' ') switch
		{
			["SETEVENTS", ..] => "250 OK\r\n",
			["ADD_ONION", "NEW:ED25519-V3", ..] => $"250-ServiceID={ServiceId}\r\n250-PrivateKey={Key}\r\n250 OK\r\n",
			["ADD_ONION", var key, ..] when OnionServiceOptions.IsPrivateKey(key) => $"250-ServiceID={ServiceId}\r\n250 OK\r\n",
			["DEL_ONION", var serviceId] when serviceId == ServiceId => "250 OK\r\n",
			["DEL_ONION", ..] => "552 Unknown Onion Service id\r\n",
			_ => $"510 Unrecognized command \"{command.Split(' ')[0]}\"\r\n"
		};
}
