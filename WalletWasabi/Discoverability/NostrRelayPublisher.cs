using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NNostr.Client;

namespace WalletWasabi.Discoverability;

/// <summary>What a relay made of an event that was sent to it.</summary>
/// <param name="Accepted">Whether the relay took the event.</param>
/// <param name="Message">What the relay said about the event, or why it could not be asked.</param>
public record RelayAnswer(Uri Relay, bool Accepted, string Message);

/// <summary>Sends events to Nostr relays.</summary>
public interface INostrPublisher
{
	/// <summary>The relays that the events are sent to.</summary>
	IReadOnlyList<Uri> Relays { get; }

	/// <summary>Sends a signed event to every relay and tells what each of them made of it. A relay that cannot be reached is an answer too, not an exception.</summary>
	Task<IReadOnlyList<RelayAnswer>> PublishAsync(NostrEvent nostrEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Sends events to Nostr relays, one connection per relay and event, and waits for each relay to say whether it took the event.
/// </summary>
/// <remarks>
/// With Tor, the connections go through its SOCKS port. The proxy gets the name of the relay and resolves it, so the
/// computer asks nobody where a relay is. The key that signed an event is given to Tor as the SOCKS user name: Tor keeps
/// the connections of different user names on circuits of their own, so that a relay cannot tell from the address it sees
/// that two keys belong together.
/// </remarks>
public class NostrRelayPublisher : INostrPublisher
{
	/// <summary>How long a relay is given to be reached and to answer. Through Tor that takes its time.</summary>
	public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	/// <summary>No message of a relay about an event is anywhere near this long. A relay that sends more is not listened to.</summary>
	private const int MaxMessageLength = 64 * 1024;

	/// <summary>How much is kept of what a relay says about an event.</summary>
	private const int MaxReasonLength = 200;

	private readonly TimeSpan _timeout;

	/// <param name="torSocksEndPoint">The SOCKS port of Tor, or null to connect to the relays directly.</param>
	public NostrRelayPublisher(IEnumerable<Uri> relays, EndPoint? torSocksEndPoint, TimeSpan? timeout = null)
	{
		Relays = relays.Distinct().ToArray();
		TorSocksEndPoint = torSocksEndPoint;
		_timeout = timeout ?? DefaultTimeout;
	}

	public IReadOnlyList<Uri> Relays { get; }

	/// <summary>The SOCKS port of Tor that every relay is reached through, or null if the relays are reached directly.</summary>
	public EndPoint? TorSocksEndPoint { get; }

	/// <summary>Reads the addresses of relays: WebSocket addresses, like <c>wss://relay.example</c>.</summary>
	/// <exception cref="FormatException">One of them is not the address of a relay.</exception>
	public static Uri[] ParseRelays(IEnumerable<string> addresses) =>
		[.. addresses
			.Select(address => address.Trim())
			.Where(address => address.Length > 0)
			.Select(address =>
				Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss)
					? uri
					: throw new FormatException($"'{address}' is not the address of a Nostr relay. It starts with wss:// or ws://."))];

	public async Task<IReadOnlyList<RelayAnswer>> PublishAsync(NostrEvent nostrEvent, CancellationToken cancellationToken)
	{
		var message = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new object[] { "EVENT", nostrEvent }));
		var proxy = CreateProxy(nostrEvent.PublicKey);

		return await Task.WhenAll(Relays.Select(relay => PublishAsync(relay, nostrEvent.Id, message, proxy, cancellationToken))).ConfigureAwait(false);
	}

	private IWebProxy? CreateProxy(string isolationId) =>
		TorSocksEndPoint switch
		{
			null => null,
			IPEndPoint endPoint => CreateProxy(endPoint.Address.ToString(), endPoint.Port, isolationId),
			DnsEndPoint endPoint => CreateProxy(endPoint.Host, endPoint.Port, isolationId),
			_ => throw new NotSupportedException($"The SOCKS port of Tor is given as {TorSocksEndPoint.GetType().Name}, which is not supported.")
		};

	private static WebProxy CreateProxy(string host, int port, string isolationId) =>
		new(new UriBuilder("socks5", host, port).Uri) { Credentials = new NetworkCredential(isolationId, isolationId) };

	private async Task<RelayAnswer> PublishAsync(Uri relay, string eventId, byte[] message, IWebProxy? proxy, CancellationToken cancellationToken)
	{
		using var timeoutCts = new CancellationTokenSource(_timeout);
		using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

		try
		{
			using var socket = new ClientWebSocket();

			// Without a proxy of ours the relay is reached directly, and not through a proxy that the system has.
			socket.Options.Proxy = proxy;

			await socket.ConnectAsync(relay, linkedCts.Token).ConfigureAwait(false);
			await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, linkedCts.Token).ConfigureAwait(false);

			var answer = await ReceiveAnswerAsync(socket, relay, eventId, linkedCts.Token).ConfigureAwait(false);

			await CloseAsync(socket).ConfigureAwait(false);
			return answer;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new RelayAnswer(relay, Accepted: false, "The relay did not answer in time.");
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Whatever went wrong on the way to this relay is what there is to tell about it. The other relays are not affected.
			return new RelayAnswer(relay, Accepted: false, ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message);
		}
	}

	/// <summary>Reads what the relay sends until it says what it made of the event. Whatever else it sends meanwhile is not for us.</summary>
	private static async Task<RelayAnswer> ReceiveAnswerAsync(ClientWebSocket socket, Uri relay, string eventId, CancellationToken cancellationToken)
	{
		while (true)
		{
			if (await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false) is not { } text)
			{
				return new RelayAnswer(relay, Accepted: false, "The relay closed the connection without an answer.");
			}

			if (TryReadAnswer(text, eventId, out var accepted, out var reason))
			{
				return new RelayAnswer(relay, accepted, reason);
			}
		}
	}

	/// <summary>Reads the message with which a relay says what it made of an event: <c>["OK", event id, true or false, reason]</c>.</summary>
	private static bool TryReadAnswer(string text, string eventId, out bool accepted, out string reason)
	{
		accepted = false;
		reason = "";

		try
		{
			using var json = JsonDocument.Parse(text);
			var root = json.RootElement;

			if (root.ValueKind != JsonValueKind.Array
				|| root.GetArrayLength() < 3
				|| root[0].ValueKind != JsonValueKind.String
				|| !string.Equals(root[0].GetString(), "OK", StringComparison.OrdinalIgnoreCase)
				|| root[1].ValueKind != JsonValueKind.String
				|| root[1].GetString() != eventId
				|| root[2].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			{
				return false;
			}

			accepted = root[2].GetBoolean();
			reason = Tidy(root.GetArrayLength() > 3 && root[3].ValueKind == JsonValueKind.String ? root[3].GetString() ?? "" : "");
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	/// <summary>
	/// What a relay says about an event is shown to the user and written into the log, and a relay can say anything.
	/// It is made one line of a sensible length.
	/// </summary>
	private static string Tidy(string text)
	{
		var line = new string([.. text.Select(character => char.IsControl(character) ? ' ' : character)]).Trim();

		return line.Length <= MaxReasonLength ? line : $"{line[..MaxReasonLength]}…";
	}

	/// <summary>Reads one text message. Null if the relay closed the connection instead.</summary>
	private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
	{
		using var message = new MemoryStream();
		var buffer = new byte[4096];

		while (true)
		{
			var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

			if (result.MessageType == WebSocketMessageType.Close)
			{
				return null;
			}

			message.Write(buffer, 0, result.Count);

			if (message.Length > MaxMessageLength)
			{
				throw new InvalidOperationException("The relay sent more than it is listened to.");
			}

			if (result.EndOfMessage)
			{
				return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
			}
		}
	}

	/// <summary>Says goodbye to the relay. It has answered by now, so nothing depends on the relay hearing it.</summary>
	private static async Task CloseAsync(ClientWebSocket socket)
	{
		try
		{
			using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
			await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, statusDescription: null, cts.Token).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or InvalidOperationException)
		{
			Logger.LogTrace(ex);
		}
	}
}
