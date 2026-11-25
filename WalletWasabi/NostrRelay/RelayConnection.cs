using System.Buffers;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// One visitor of the relay: a connection from Tor on behalf of somebody who knows the onion address. The visitor
/// opens it with a request, which either asks the relay about itself or starts to speak Nostr over WebSocket, and
/// then sends events, subscribes to events, and closes subscriptions.
/// </summary>
/// <remarks>
/// Nothing tells one visitor from another, so each of them is held to the limits of <see cref="RelayLimits"/>: in what
/// a message may weigh, in how fast messages may come, in how many subscriptions are open, and in how much may wait
/// to be sent to a visitor that does not read.
/// </remarks>
internal sealed class RelayConnection : IDisposable
{
	private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

	private readonly NostrRelayServer _server;
	private readonly TcpClient _client;
	private readonly RelayLimits _limits;
	private readonly CancellationTokenSource _cts;
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly Channel<byte[]> _queue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
	private readonly Lock _subscriptionsLock = new();
	private readonly Dictionary<string, RelayFilter[]> _subscriptions = new(StringComparer.Ordinal);
	private readonly Pace _messagePace;
	private readonly Pace _eventPace;

	private long _lastActivity = Environment.TickCount64;
	private int _queuedMessages;
	private int _queuedBytes;
	private int _paceViolations;
	private int _isAborted;

	public RelayConnection(NostrRelayServer server, TcpClient client, long id, CancellationToken serverStopping)
	{
		_server = server;
		_client = client;
		_limits = server.Limits;
		_cts = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
		_messagePace = new Pace(_limits.MessageBurst, _limits.MessagesPerSecond);
		_eventPace = new Pace(_limits.EventBurst, _limits.EventsPerSecond);

		Id = id;
	}

	public long Id { get; }

	/// <summary>When the visitor was heard of the last time, on the clock of <see cref="Environment.TickCount64"/>.</summary>
	public long LastActivity => Interlocked.Read(ref _lastActivity);

	/// <summary>Set when the connection is over.</summary>
	public Task Completion { get; set; } = Task.CompletedTask;

	/// <summary>Serves the visitor until it leaves, or is made to.</summary>
	public async Task RunAsync()
	{
		var cancellationToken = _cts.Token;
		var socket = _client.Client;

		using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		handshakeCts.CancelAfter(_limits.HandshakeTimeout);

		var request = await ReadRequestAsync(socket, handshakeCts.Token).ConfigureAwait(false);

		if (request is null)
		{
			await RespondAsync(socket, RelayRequest.CreateResponse(400, "Bad Request"), handshakeCts.Token).ConfigureAwait(false);
			return;
		}

		// The relay answers to its onion address and to nothing else. A page in a browser of this computer that tries the
		// ports of the computer one by one asks for another host, and gets nothing out of the relay.
		if (_server.OnionHost is not { } onionHost || request.Host != onionHost.ToLowerInvariant())
		{
			await RespondAsync(socket, RelayRequest.CreateResponse(421, "Misdirected Request"), handshakeCts.Token).ConfigureAwait(false);
			return;
		}

		if (request.Method == "OPTIONS")
		{
			await RespondAsync(socket, RelayRequest.CreateResponse(204, "No Content", extraHeaders: RelayInformation.CrossOriginHeaders), handshakeCts.Token).ConfigureAwait(false);
			return;
		}

		if (request.Method != "GET")
		{
			await RespondAsync(socket, RelayRequest.CreateResponse(405, "Method Not Allowed", extraHeaders: "Allow: GET, OPTIONS\r\n"), handshakeCts.Token).ConfigureAwait(false);
			return;
		}

		if (request.IsWebSocketUpgrade)
		{
			if (request.GetWebSocketAcceptance() is not { } acceptance)
			{
				await RespondAsync(socket, RelayRequest.CreateResponse(426, "Upgrade Required", extraHeaders: "Sec-WebSocket-Version: 13\r\n"), handshakeCts.Token).ConfigureAwait(false);
				return;
			}

			await socket.SendAsync(Encoding.ASCII.GetBytes(acceptance), SocketFlags.None, handshakeCts.Token).ConfigureAwait(false);

			using var webSocket = WebSocket.CreateFromStream(
				_client.GetStream(),
				new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = KeepAliveInterval, KeepAliveTimeout = KeepAliveTimeout });

			await TalkAsync(webSocket, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (request.AsksForRelayInformation)
		{
			var information = RelayRequest.CreateResponse(200, "OK", "application/nostr+json", _server.Information, RelayInformation.CrossOriginHeaders);
			await RespondAsync(socket, information, handshakeCts.Token).ConfigureAwait(false);
			return;
		}

		await RespondAsync(socket, RelayRequest.CreateResponse(200, "OK", body: "This is a Nostr relay. It is spoken to with a Nostr client.\n"), handshakeCts.Token).ConfigureAwait(false);
	}

	/// <summary>Hands an event that the relay just took to the visitor, as far as the visitor subscribed to such events.</summary>
	public void Offer(RelayEvent relayEvent)
	{
		List<byte[]>? messages = null;

		lock (_subscriptionsLock)
		{
			foreach (var (subscriptionId, filters) in _subscriptions)
			{
				if (filters.Any(filter => filter.Matches(relayEvent)))
				{
					(messages ??= []).Add(RelayMessages.Event(subscriptionId, relayEvent.Json));
				}
			}
		}

		if (messages is null)
		{
			return;
		}

		foreach (var message in messages)
		{
			// A visitor that does not read what it subscribed to is not kept waiting for: what it is owed would pile up.
			if (Volatile.Read(ref _queuedMessages) >= _limits.MaxQueuedMessages
				|| Volatile.Read(ref _queuedBytes) + message.Length > _limits.MaxQueuedBytes)
			{
				Abort();
				return;
			}

			Interlocked.Increment(ref _queuedMessages);
			Interlocked.Add(ref _queuedBytes, message.Length);
			_queue.Writer.TryWrite(message);
		}
	}

	/// <summary>Ends the connection at once, without a word to the visitor.</summary>
	public void Abort()
	{
		if (Interlocked.Exchange(ref _isAborted, 1) != 0)
		{
			return;
		}

		try
		{
			_cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The connection is over already.
		}

		_client.Close();
	}

	public void Dispose()
	{
		Abort();
		_queue.Writer.TryComplete();
		_cts.Dispose();
		_sendLock.Dispose();
		_client.Dispose();
	}

	/// <summary>
	/// Reads the request that opens the connection, up to the empty line that ends its head, and not a byte further:
	/// what follows is for the WebSocket.
	/// </summary>
	/// <returns>Null for what is no request, or is larger than a request may be.</returns>
	private async Task<RelayRequest?> ReadRequestAsync(Socket socket, CancellationToken cancellationToken)
	{
		var buffer = new byte[_limits.MaxRequestHeadBytes];
		var length = 0;

		while (length < buffer.Length)
		{
			// Looking without taking: only what belongs to the head is taken.
			var peeked = await socket.ReceiveAsync(buffer.AsMemory(length), SocketFlags.Peek, cancellationToken).ConfigureAwait(false);

			if (peeked == 0)
			{
				return null;
			}

			var searchFrom = Math.Max(0, length - 3);
			var end = buffer.AsSpan(searchFrom, length + peeked - searchFrom).IndexOf("\r\n\r\n"u8);
			var take = end >= 0 ? searchFrom + end + 4 - length : peeked;

			for (var taken = 0; taken < take;)
			{
				var received = await socket.ReceiveAsync(buffer.AsMemory(length + taken, take - taken), SocketFlags.None, cancellationToken).ConfigureAwait(false);

				if (received == 0)
				{
					return null;
				}

				taken += received;
			}

			length += take;

			if (end >= 0)
			{
				return RelayRequest.TryParse(buffer.AsSpan(0, length - 4), out var request) ? request : null;
			}
		}

		return null;
	}

	/// <summary>Answers a request that does not open a WebSocket connection. The connection is over after the answer.</summary>
	private static async Task RespondAsync(Socket socket, byte[] response, CancellationToken cancellationToken)
	{
		await socket.SendAsync(response, SocketFlags.None, cancellationToken).ConfigureAwait(false);
		socket.Shutdown(SocketShutdown.Send);
	}

	private async Task TalkAsync(WebSocket webSocket, CancellationToken cancellationToken)
	{
		var sending = Task.Run(() => SendQueuedAsync(webSocket, cancellationToken), CancellationToken.None);

		try
		{
			await ReceiveAsync(webSocket, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			lock (_subscriptionsLock)
			{
				_subscriptions.Clear();
			}

			_queue.Writer.TryComplete();
			Abort();

			await sending.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
		}
	}

	/// <summary>Sends the events that came in from others and that the visitor subscribed to.</summary>
	private async Task SendQueuedAsync(WebSocket webSocket, CancellationToken cancellationToken)
	{
		try
		{
			await foreach (var message in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
			{
				Interlocked.Decrement(ref _queuedMessages);
				Interlocked.Add(ref _queuedBytes, -message.Length);

				await SendAsync(webSocket, message, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or System.IO.IOException)
		{
			Abort();
		}
	}

	private async Task SendAsync(WebSocket webSocket, byte[] message, CancellationToken cancellationToken)
	{
		await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeoutCts.CancelAfter(_limits.SendTimeout);

			await webSocket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, timeoutCts.Token).ConfigureAwait(false);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private async Task ReceiveAsync(WebSocket webSocket, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(_limits.MaxMessageBytes);

		try
		{
			while (webSocket.State == WebSocketState.Open)
			{
				var length = 0;
				ValueWebSocketReceiveResult result;

				do
				{
					if (length == _limits.MaxMessageBytes)
					{
						await SendAsync(webSocket, RelayMessages.Notice($"error: a message has {_limits.MaxMessageBytes} bytes at most"), cancellationToken).ConfigureAwait(false);
						await CloseAsync(webSocket, WebSocketCloseStatus.MessageTooBig, "message too large").ConfigureAwait(false);
						return;
					}

					result = await webSocket.ReceiveAsync(buffer.AsMemory(length, _limits.MaxMessageBytes - length), cancellationToken).ConfigureAwait(false);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						await CloseAsync(webSocket, WebSocketCloseStatus.NormalClosure, "").ConfigureAwait(false);
						return;
					}

					length += result.Count;
				}
				while (!result.EndOfMessage);

				Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

				if (result.MessageType != WebSocketMessageType.Text)
				{
					await CloseAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "text messages only").ConfigureAwait(false);
					return;
				}

				if (!await HandleAsync(webSocket, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false))
				{
					await CloseAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "too many messages").ConfigureAwait(false);
					return;
				}
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or System.IO.IOException)
		{
			// The visitor is gone, or was made to go.
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	/// <summary>Reads a message of the visitor and answers it.</summary>
	/// <returns>False if the visitor has sent more messages than it may once too often, and is to be disconnected.</returns>
	private async Task<bool> HandleAsync(WebSocket webSocket, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
	{
		var isWithinPace = _messagePace.TryTake();

		if (!isWithinPace && ++_paceViolations > _limits.MaxPaceViolations)
		{
			return false;
		}

		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
		}
		catch (JsonException)
		{
			await SendAsync(webSocket, RelayMessages.Notice("invalid: the message is not JSON"), cancellationToken).ConfigureAwait(false);
			return true;
		}

		using (document)
		{
			var root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.String)
			{
				await SendAsync(webSocket, RelayMessages.Notice("invalid: a message is a list that begins with its name"), cancellationToken).ConfigureAwait(false);
				return true;
			}

			if (root[0].ValueEquals("EVENT"))
			{
				await HandleEventAsync(webSocket, root, isWithinPace, cancellationToken).ConfigureAwait(false);
			}
			else if (root[0].ValueEquals("REQ"))
			{
				await HandleRequestAsync(webSocket, root, isWithinPace, cancellationToken).ConfigureAwait(false);
			}
			else if (root[0].ValueEquals("CLOSE"))
			{
				// Closing costs nothing and frees something, so it is done at any pace.
				if (root.GetArrayLength() == 2 && TryGetSubscriptionId(root[1], out var subscriptionId))
				{
					lock (_subscriptionsLock)
					{
						_subscriptions.Remove(subscriptionId);
					}
				}
				else
				{
					await SendAsync(webSocket, RelayMessages.Notice("invalid: CLOSE names one subscription"), cancellationToken).ConfigureAwait(false);
				}
			}
			else
			{
				await SendAsync(webSocket, RelayMessages.Notice("unsupported: this relay knows the messages EVENT, REQ and CLOSE"), cancellationToken).ConfigureAwait(false);
			}
		}

		return true;
	}

	private async Task HandleEventAsync(WebSocket webSocket, JsonElement root, bool isWithinPace, CancellationToken cancellationToken)
	{
		if (root.GetArrayLength() != 2)
		{
			await SendAsync(webSocket, RelayMessages.Notice("invalid: EVENT brings one event"), cancellationToken).ConfigureAwait(false);
			return;
		}

		// The relay answers about an event by the id that the event claims. Without one there is nothing to answer about.
		if (RelayEvent.ReadClaimedId(root[1]) is not { } id)
		{
			await SendAsync(webSocket, RelayMessages.Notice("invalid: the event has no id of 64 characters of lowercase hex"), cancellationToken).ConfigureAwait(false);
			return;
		}

		// Checking a signature and keeping an event are the work that a visitor can make the relay do, so that work has a pace of its own.
		if (!isWithinPace || !_eventPace.TryTake())
		{
			await SendAsync(webSocket, RelayMessages.Ok(id, false, "rate-limited: slow down"), cancellationToken).ConfigureAwait(false);
			return;
		}

		if (!RelayEvent.TryParse(root[1], _limits, out var relayEvent, out var problem))
		{
			await SendAsync(webSocket, RelayMessages.Ok(id, false, problem), cancellationToken).ConfigureAwait(false);
			return;
		}

		var now = _server.GetTime();
		var verdict = _server.Events.Judge(relayEvent, now);

		if (!verdict.IsAccepted)
		{
			await SendAsync(webSocket, RelayMessages.Ok(id, false, verdict.Message), cancellationToken).ConfigureAwait(false);
			return;
		}

		StoreOutcome outcome;

		try
		{
			// An ephemeral event is for whoever listens at this moment, and is not kept.
			outcome = relayEvent.IsEphemeral ? StoreOutcome.Stored : _server.Store.Add(relayEvent, verdict.ExpiresAt, now);
		}
		catch (SqliteException ex)
		{
			// A full disk, for example. The visitor is told that it did not work, and not what the computer said.
			Logger.LogWarning($"The Nostr relay could not keep an event: {ex.Message}");
			await SendAsync(webSocket, RelayMessages.Ok(id, false, "error: the relay could not keep the event"), cancellationToken).ConfigureAwait(false);
			return;
		}

		var answer = outcome switch
		{
			StoreOutcome.Stored => RelayMessages.Ok(id, true, ""),
			StoreOutcome.Duplicate => RelayMessages.Ok(id, true, "duplicate: already have this event"),
			StoreOutcome.Superseded => RelayMessages.Ok(id, false, "replaced: have newer event"),
			_ when _server.Events.DropsOldestWhenFull => RelayMessages.Ok(id, false, "error: the relay is full, and has no room for an event this old"),
			_ => RelayMessages.Ok(id, false, "rate-limited: the relay is full, try again in a few minutes")
		};

		await SendAsync(webSocket, answer, cancellationToken).ConfigureAwait(false);

		if (outcome == StoreOutcome.Stored)
		{
			_server.Broadcast(relayEvent);
		}
	}

	private async Task HandleRequestAsync(WebSocket webSocket, JsonElement root, bool isWithinPace, CancellationToken cancellationToken)
	{
		if (root.GetArrayLength() < 3 || !TryGetSubscriptionId(root[1], out var subscriptionId))
		{
			await SendAsync(webSocket, RelayMessages.Notice($"invalid: REQ names a subscription with up to {_limits.MaxSubscriptionIdLength} characters, and one filter at least"), cancellationToken).ConfigureAwait(false);
			return;
		}

		if (!isWithinPace)
		{
			await SendAsync(webSocket, RelayMessages.Closed(subscriptionId, "rate-limited: slow down"), cancellationToken).ConfigureAwait(false);
			return;
		}

		if (root.GetArrayLength() - 2 > _limits.MaxFilters)
		{
			await SendAsync(webSocket, RelayMessages.Closed(subscriptionId, $"invalid: a subscription has {_limits.MaxFilters} filters at most"), cancellationToken).ConfigureAwait(false);
			return;
		}

		var filters = new RelayFilter[root.GetArrayLength() - 2];

		for (var i = 0; i < filters.Length; i++)
		{
			if (!RelayFilter.TryParse(root[i + 2], _limits, out var filter, out var problem))
			{
				await SendAsync(webSocket, RelayMessages.Closed(subscriptionId, problem), cancellationToken).ConfigureAwait(false);
				return;
			}

			filters[i] = filter;
		}

		lock (_subscriptionsLock)
		{
			// A subscription with the name of one that is open takes its place.
			if (!_subscriptions.ContainsKey(subscriptionId) && _subscriptions.Count >= _limits.MaxSubscriptions)
			{
				filters = [];
			}
			else
			{
				// From here on the visitor gets the events that come in. What is stored follows, so nothing falls between the two.
				_subscriptions[subscriptionId] = filters;
			}
		}

		if (filters.Length == 0)
		{
			await SendAsync(webSocket, RelayMessages.Closed(subscriptionId, $"rate-limited: a connection has {_limits.MaxSubscriptions} subscriptions at most"), cancellationToken).ConfigureAwait(false);
			return;
		}

		var now = _server.GetTime();
		HashSet<string>? sent = filters.Length > 1 ? new(StringComparer.Ordinal) : null;

		foreach (var filter in filters)
		{
			var remaining = Math.Min(filter.Limit ?? _limits.MaxLimit, _limits.MaxLimit);
			StoredEvent? last = null;

			// A page at a time, so that what a visitor asked for is not all in memory while the visitor takes its time to read.
			while (remaining > 0)
			{
				var pageSize = Math.Min(_limits.QueryPageSize, remaining);
				IReadOnlyList<StoredEvent> page;

				try
				{
					page = _server.Store.Query(filter, now, pageSize, last);
				}
				catch (SqliteException ex)
				{
					Logger.LogWarning($"The Nostr relay could not look its events up: {ex.Message}");

					lock (_subscriptionsLock)
					{
						_subscriptions.Remove(subscriptionId);
					}

					await SendAsync(webSocket, RelayMessages.Closed(subscriptionId, "error: the relay could not look its events up"), cancellationToken).ConfigureAwait(false);
					return;
				}

				foreach (var stored in page)
				{
					// An event that several filters ask for is sent once.
					if (sent is null || sent.Add(stored.Id))
					{
						await SendAsync(webSocket, RelayMessages.Event(subscriptionId, stored.Json), cancellationToken).ConfigureAwait(false);
					}
				}

				if (page.Count < pageSize)
				{
					break;
				}

				remaining -= page.Count;
				last = page[^1];
			}
		}

		await SendAsync(webSocket, RelayMessages.EndOfStoredEvents(subscriptionId), cancellationToken).ConfigureAwait(false);
	}

	private bool TryGetSubscriptionId(JsonElement json, out string subscriptionId)
	{
		subscriptionId = "";

		if (json.ValueKind != JsonValueKind.String)
		{
			return false;
		}

		try
		{
			subscriptionId = json.GetString() ?? "";
		}
		catch (InvalidOperationException)
		{
			return false;
		}

		return subscriptionId.Length > 0 && subscriptionId.Length <= _limits.MaxSubscriptionIdLength;
	}

	private static async Task CloseAsync(WebSocket webSocket, WebSocketCloseStatus status, string description)
	{
		try
		{
			using var timeoutCts = new CancellationTokenSource(CloseTimeout);
			await webSocket.CloseOutputAsync(status, description, timeoutCts.Token).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or System.IO.IOException)
		{
			// The visitor is gone already.
		}
	}

	/// <summary>
	/// A pace that a visitor is held to: a number of things in a row, and then so many per second.
	/// </summary>
	private sealed class Pace(int burst, double perSecond)
	{
		private double _available = burst;
		private long _lastRefill = Environment.TickCount64;

		public bool TryTake()
		{
			var now = Environment.TickCount64;

			_available = Math.Min(burst, _available + (now - _lastRefill) / 1000.0 * perSecond);
			_lastRefill = now;

			if (_available < 1)
			{
				return false;
			}

			_available -= 1;
			return true;
		}
	}
}

/// <summary>The messages that a relay sends, as the bytes that go over the connection.</summary>
internal static class RelayMessages
{
	/// <summary>An event for a subscription.</summary>
	/// <param name="eventJson">The event, as JSON that the relay wrote itself.</param>
	public static byte[] Event(string subscriptionId, string eventJson) =>
		Encoding.UTF8.GetBytes($"[\"EVENT\",{Quote(subscriptionId)},{eventJson}]");

	/// <summary>What became of an event that a visitor sent.</summary>
	public static byte[] Ok(string eventId, bool isAccepted, string message) =>
		Encoding.UTF8.GetBytes($"[\"OK\",{Quote(eventId)},{(isAccepted ? "true" : "false")},{Quote(message)}]");

	/// <summary>Everything that the relay had stored for a subscription was sent. What follows are events as they come in.</summary>
	public static byte[] EndOfStoredEvents(string subscriptionId) =>
		Encoding.UTF8.GetBytes($"[\"EOSE\",{Quote(subscriptionId)}]");

	/// <summary>A subscription is not opened, or is ended by the relay.</summary>
	public static byte[] Closed(string subscriptionId, string message) =>
		Encoding.UTF8.GetBytes($"[\"CLOSED\",{Quote(subscriptionId)},{Quote(message)}]");

	/// <summary>Something for whoever reads along at the visitor's end.</summary>
	public static byte[] Notice(string message) =>
		Encoding.UTF8.GetBytes($"[\"NOTICE\",{Quote(message)}]");

	private static string Quote(string text) => JsonSerializer.Serialize(text);
}
