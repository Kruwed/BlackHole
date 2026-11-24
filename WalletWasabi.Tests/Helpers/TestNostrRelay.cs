using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NNostr.Client;

namespace WalletWasabi.Tests.Helpers;

/// <summary>
/// A Nostr relay for tests, on a port of this computer. It takes the events that are sent to it, checks their signatures,
/// keeps the newest event of a key for the kinds that are replaceable, and serves what it keeps.
/// </summary>
public sealed class TestNostrRelay : IAsyncDisposable
{
	private readonly TcpListener _listener;
	private readonly CancellationTokenSource _cts = new();
	private readonly Task _acceptLoop;
	private readonly Lock _lock = new();
	private readonly List<NostrEvent> _received = [];
	private readonly List<NostrEvent> _stored = [];
	private int _connectionCount;

	/// <param name="port">The port to listen on, or 0 for one that is free.</param>
	public TestNostrRelay(int port = 0)
	{
		_listener = new TcpListener(IPAddress.Loopback, port);
		_listener.Start();
		EndPoint = (IPEndPoint)_listener.LocalEndpoint;
		Uri = new Uri($"ws://127.0.0.1:{EndPoint.Port}/");
		_acceptLoop = Task.Run(AcceptLoopAsync);
	}

	/// <summary>The address of the relay.</summary>
	public Uri Uri { get; }

	public IPEndPoint EndPoint { get; }

	/// <summary>Says whether an event is taken, and what the relay tells about that. Every event is taken unless this says otherwise.</summary>
	public Func<NostrEvent, (bool Accepted, string Message)>? Judge { get; set; }

	/// <summary>A relay that is silent takes connections, events and requests and never answers.</summary>
	public bool IsSilent { get; set; }

	/// <summary>Every event that was sent to the relay, in the order of arrival, taken or not.</summary>
	public IReadOnlyList<NostrEvent> Received
	{
		get
		{
			lock (_lock)
			{
				return [.. _received];
			}
		}
	}

	/// <summary>The events that the relay keeps now.</summary>
	public IReadOnlyList<NostrEvent> Stored
	{
		get
		{
			lock (_lock)
			{
				return [.. _stored];
			}
		}
	}

	public int ConnectionCount => Volatile.Read(ref _connectionCount);

	/// <summary>Finds a port of this computer that nothing listens on.</summary>
	public static int GetFreePort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	public async ValueTask DisposeAsync()
	{
		await _cts.CancelAsync();
		_listener.Stop();

		try
		{
			await _acceptLoop;
		}
		catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
		{
		}

		_cts.Dispose();
	}

	private async Task AcceptLoopAsync()
	{
		while (!_cts.IsCancellationRequested)
		{
			var client = await _listener.AcceptTcpClientAsync(_cts.Token);

			Interlocked.Increment(ref _connectionCount);
			_ = Task.Run(() => ServeAsync(client));
		}
	}

	/// <summary>Answers the request that opens a WebSocket, whatever host and path it names.</summary>
	private async Task<WebSocket> AcceptWebSocketAsync(NetworkStream stream)
	{
		var request = new StringBuilder();
		var buffer = new byte[1];

		while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
		{
			await stream.ReadExactlyAsync(buffer, _cts.Token);
			request.Append((char)buffer[0]);
		}

		var key = request.ToString()
			.Split("\r\n")
			.First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
			.Split(':', 2)[1]
			.Trim();

		var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

		await stream.WriteAsync(
			Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"),
			_cts.Token);

		return WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero);
	}

	private async Task ServeAsync(TcpClient client)
	{
		try
		{
			using var _ = client;
			using var socket = await AcceptWebSocketAsync(client.GetStream());

			while (socket.State == WebSocketState.Open)
			{
				if (await ReceiveTextAsync(socket) is not { } text)
				{
					break;
				}

				using var json = JsonDocument.Parse(text);
				var message = json.RootElement;

				switch (message[0].GetString())
				{
					case "EVENT":
						await TakeEventAsync(socket, message[1].Deserialize<NostrEvent>()!);
						break;

					case "REQ" when !IsSilent:
						await ServeRequestAsync(socket, message[1].GetString()!, message.EnumerateArray().Skip(2).Select(filter => filter.Deserialize<NostrSubscriptionFilter>()!).ToArray());
						break;
				}
			}

			if (socket.State is WebSocketState.CloseReceived)
			{
				await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, _cts.Token);
			}
		}
		catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or SocketException)
		{
			// The other side went away, or the relay is closing down.
		}
	}

	private async Task TakeEventAsync(WebSocket socket, NostrEvent nostrEvent)
	{
		(bool Accepted, string Message) verdict;

		lock (_lock)
		{
			_received.Add(nostrEvent);

			verdict = !nostrEvent.Verify()
				? (false, "invalid: bad signature")
				: Judge?.Invoke(nostrEvent) ?? (true, "");

			if (verdict.Accepted)
			{
				verdict = Store(nostrEvent);
			}
		}

		if (!IsSilent)
		{
			await SendAsync(socket, JsonSerializer.Serialize(new object[] { "OK", nostrEvent.Id, verdict.Accepted, verdict.Message }));
		}
	}

	/// <summary>Keeps an event. Of the kinds that are replaceable, only the newest event of a key is kept.</summary>
	private (bool Accepted, string Message) Store(NostrEvent nostrEvent)
	{
		if (_stored.Any(stored => stored.Id == nostrEvent.Id))
		{
			return (true, "duplicate: already have this event");
		}

		var isReplaceable = nostrEvent.Kind is 0 or 3 or (>= 10_000 and < 20_000);

		if (isReplaceable && _stored.FirstOrDefault(stored => stored.Kind == nostrEvent.Kind && stored.PublicKey == nostrEvent.PublicKey) is { } current)
		{
			// The newer one stays. Of two events of the same second, the one with the lower id.
			var isNewer = nostrEvent.CreatedAt > current.CreatedAt
				|| (nostrEvent.CreatedAt == current.CreatedAt && string.CompareOrdinal(nostrEvent.Id, current.Id) < 0);

			if (!isNewer)
			{
				return (false, "replaced: have newer event");
			}

			_stored.Remove(current);
		}

		_stored.Add(nostrEvent);
		return (true, "");
	}

	private async Task ServeRequestAsync(WebSocket socket, string subscriptionId, NostrSubscriptionFilter[] filters)
	{
		NostrEvent[] matching;

		lock (_lock)
		{
			matching = [.. _stored.Where(stored => filters.Any(filter =>
				(filter.Kinds is null || filter.Kinds.Contains(stored.Kind))
				&& (filter.Authors is null || filter.Authors.Contains(stored.PublicKey))
				&& (filter.Ids is null || filter.Ids.Contains(stored.Id))
				&& (filter.Since is null || stored.CreatedAt >= filter.Since)))];
		}

		foreach (var stored in matching)
		{
			await SendAsync(socket, JsonSerializer.Serialize(new object[] { "EVENT", subscriptionId, stored }));
		}

		await SendAsync(socket, JsonSerializer.Serialize(new object[] { "EOSE", subscriptionId }));
	}

	private Task SendAsync(WebSocket socket, string text) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, _cts.Token);

	private async Task<string?> ReceiveTextAsync(WebSocket socket)
	{
		using var message = new MemoryStream();
		var buffer = new byte[4096];

		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, _cts.Token);

			if (result.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			message.Write(buffer, 0, result.Count);

			if (result.EndOfMessage)
			{
				return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
			}
		}
	}
}
