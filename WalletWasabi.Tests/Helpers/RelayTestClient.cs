using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.NostrRelay;

namespace WalletWasabi.Tests.Helpers;

/// <summary>A client of Nostr for tests of the relay: it connects the way a client does, and sends and reads messages as text.</summary>
public sealed class RelayTestClient : IDisposable
{
	private readonly ClientWebSocket _socket = new();

	public WebSocketState State => _socket.State;

	public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

	/// <summary>Makes the relay answer to the address that tests reach it at, in the place of an onion address.</summary>
	public static void Open(NostrRelayServer server) => server.OnionHost = $"127.0.0.1:{server.EndPoint.Port}";

	public static Uri GetUri(NostrRelayServer server) => new($"ws://127.0.0.1:{server.EndPoint.Port}/");

	public static async Task<RelayTestClient> ConnectAsync(NostrRelayServer server, CancellationToken cancellationToken)
	{
		var client = new RelayTestClient();

		try
		{
			await client._socket.ConnectAsync(GetUri(server), cancellationToken);
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}

	public Task SendAsync(string message, CancellationToken cancellationToken) =>
		_socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

	public Task SendEventAsync(string eventJson, CancellationToken cancellationToken) =>
		SendAsync($"[\"EVENT\",{eventJson}]", cancellationToken);

	public Task SendBinaryAsync(byte[] message, CancellationToken cancellationToken) =>
		_socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);

	/// <summary>Reads the next message of the relay.</summary>
	/// <returns>The message, or null if the relay closed the connection.</returns>
	public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
	{
		using var message = new MemoryStream();
		var buffer = new byte[16 * 1024];

		while (true)
		{
			WebSocketReceiveResult result;

			try
			{
				result = await _socket.ReceiveAsync(buffer, cancellationToken);
			}
			catch (WebSocketException)
			{
				// The relay cut the connection without a word.
				return null;
			}

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

	/// <summary>Sends an event and reads what the relay answers about it.</summary>
	/// <returns>Whether the relay took the event, and what it said.</returns>
	public async Task<(bool IsAccepted, string Message)> PublishAsync(string eventJson, CancellationToken cancellationToken)
	{
		await SendEventAsync(eventJson, cancellationToken);

		using var answer = JsonDocument.Parse(await ReceiveAsync(cancellationToken) ?? throw new IOException("The relay closed the connection."));
		var root = answer.RootElement;

		if (root[0].GetString() != "OK")
		{
			throw new InvalidDataException($"The relay answered {root.GetRawText()}.");
		}

		return (root[2].GetBoolean(), root[3].GetString()!);
	}

	/// <summary>Subscribes and reads the stored events that the relay sends, up to the message that ends them.</summary>
	/// <returns>The ids of the events, in the order in which they came.</returns>
	public async Task<string[]> SubscribeAsync(string subscriptionId, string filters, CancellationToken cancellationToken)
	{
		await SendAsync($"[\"REQ\",\"{subscriptionId}\",{filters}]", cancellationToken);

		var ids = new System.Collections.Generic.List<string>();

		while (true)
		{
			using var message = JsonDocument.Parse(await ReceiveAsync(cancellationToken) ?? throw new IOException("The relay closed the connection."));
			var root = message.RootElement;

			switch (root[0].GetString())
			{
				case "EVENT" when root[1].GetString() == subscriptionId:
					ids.Add(root[2].GetProperty("id").GetString()!);
					break;

				case "EOSE" when root[1].GetString() == subscriptionId:
					return [.. ids];

				default:
					throw new InvalidDataException($"The relay answered {root.GetRawText()}.");
			}
		}
	}

	public void Dispose() => _socket.Dispose();

	/// <summary>Sends a request as it is written, over a connection of its own, and reads everything that the relay answers until it closes the connection.</summary>
	public static async Task<string> RequestAsync(NostrRelayServer server, string request, CancellationToken cancellationToken)
	{
		using var client = new TcpClient();
		await client.ConnectAsync(server.EndPoint, cancellationToken);

		var stream = client.GetStream();
		await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);

		using var answer = new MemoryStream();

		try
		{
			await stream.CopyToAsync(answer, cancellationToken);
		}
		catch (IOException)
		{
			// The relay cut the connection, which ends the answer as well.
		}

		return Encoding.UTF8.GetString(answer.ToArray());
	}
}
