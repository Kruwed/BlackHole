using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WalletWasabi.NostrRelay;

/// <summary>The request that a visitor of the relay opens its connection with: what it asks for, and its headers.</summary>
public sealed record RelayRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
	private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

	/// <summary>The host that the visitor asked for, in lowercase and without the port that needs no naming. Null if it named none.</summary>
	public string? Host
	{
		get
		{
			if (!Headers.TryGetValue("Host", out var host))
			{
				return null;
			}

			host = host.Trim().ToLowerInvariant();

			return host.EndsWith(":80", StringComparison.Ordinal) ? host[..^3] : host;
		}
	}

	/// <summary>Whether the visitor asks to speak WebSocket, which is how Nostr is spoken.</summary>
	public bool IsWebSocketUpgrade =>
		Method == "GET"
		&& HasToken("Upgrade", "websocket")
		&& HasToken("Connection", "upgrade");

	/// <summary>Whether the visitor asks the relay about itself: what it is, and what its limits are.</summary>
	public bool AsksForRelayInformation =>
		Method == "GET" && Headers.TryGetValue("Accept", out var accept) && accept.Contains("application/nostr+json", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Reads the head of a request: its first line and its headers, without the empty line that ends them.
	/// </summary>
	/// <returns>False for what is no request of HTTP/1.1.</returns>
	public static bool TryParse(ReadOnlySpan<byte> head, [NotNullWhen(true)] out RelayRequest? request)
	{
		request = null;

		// Everything in the head of a request is ASCII. What is not could be anything.
		foreach (var octet in head)
		{
			if (octet is not ((>= 0x20 and < 0x7f) or (byte)'\r' or (byte)'\n' or (byte)'\t'))
			{
				return false;
			}
		}

		var lines = Encoding.ASCII.GetString(head).Split("\r\n");
		var requestLine = lines[0].Split(' ');

		if (requestLine is not [{ Length: > 0 } method, { Length: > 0 } target, "HTTP/1.1"] || lines[0].Contains('\n'))
		{
			return false;
		}

		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (var line in lines.AsSpan(1))
		{
			var colon = line.IndexOf(':');

			// A header is a name, a colon and a value. A name has no blanks in it, and a line that continues another one is not taken.
			if (colon <= 0 || line.Contains('\n') || line.AsSpan(0, colon).ContainsAny(" \t"))
			{
				return false;
			}

			var name = line[..colon];
			var value = line[(colon + 1)..].Trim();

			headers[name] = headers.TryGetValue(name, out var before) ? $"{before}, {value}" : value;
		}

		request = new RelayRequest(method, target, headers);
		return true;
	}

	/// <summary>
	/// What the relay answers to open a WebSocket connection, if the visitor asked for one the way the protocol has it.
	/// </summary>
	/// <returns>The lines of the answer, or null if the request is not one that opens a WebSocket connection of the version that there is.</returns>
	public string? GetWebSocketAcceptance()
	{
		if (!IsWebSocketUpgrade
			|| !Headers.TryGetValue("Sec-WebSocket-Version", out var version) || version.Trim() != "13"
			|| !Headers.TryGetValue("Sec-WebSocket-Key", out var key))
		{
			return null;
		}

		key = key.Trim();

		// The key is sixteen bytes in Base64.
		Span<byte> keyBytes = stackalloc byte[18];

		if (key.Length != 24 || !Convert.TryFromBase64String(key, keyBytes, out var written) || written != 16)
		{
			return null;
		}

		var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

		return $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
	}

	/// <summary>Writes an answer that is not a WebSocket connection. The connection is closed after it.</summary>
	/// <param name="extraHeaders">More headers, each with its line end.</param>
	public static byte[] CreateResponse(int status, string reason, string contentType = "text/plain; charset=utf-8", string body = "", string extraHeaders = "")
	{
		var content = Encoding.UTF8.GetBytes(body);
		var head = string.Create(
			CultureInfo.InvariantCulture,
			$"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {content.Length}\r\nConnection: close\r\n{extraHeaders}\r\n");

		return [.. Encoding.ASCII.GetBytes(head), .. content];
	}

	private bool HasToken(string header, string token) =>
		Headers.TryGetValue(header, out var value)
		&& value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(token, StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the relay tells about itself to a client that asks: its name, what it takes, and its limits.</summary>
public static class RelayInformation
{
	/// <summary>The headers that let a page in a browser read the answer, whatever site the page is from.</summary>
	public const string CrossOriginHeaders = "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: *\r\nAccess-Control-Allow-Methods: GET, OPTIONS\r\n";

	/// <summary>The parts of the protocol that the relay speaks: the basic one, and the one that this answer is.</summary>
	public static readonly ImmutableArray<int> SupportedNips = [1, 11];

	/// <summary>Tells in a sentence whose events a relay takes.</summary>
	public static string DescribeSenders(RelaySenderRules senders)
	{
		if (!senders.IsBondRequired)
		{
			return "It takes them from anybody.";
		}

		var bond = $"It takes them from keys that have shown a fidelity bond of {RelaySenders.FormatAmount(senders.RequiredShare)} or more";

		return senders.Whitelist.Count switch
		{
			0 => $"{bond}.",
			1 => $"{bond}, and from one key that it has on a list.",
			var count => string.Create(CultureInfo.InvariantCulture, $"{bond}, and from {count} keys that it has on a list.")
		};
	}

	public static string Create(RelayEventSet events, RelaySenderRules senders)
	{
		var limits = events.Limits;
		var buffer = new ArrayBufferWriter<byte>();
		var retention = (long)RelayEventSet.Retention.TotalSeconds;

		using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			writer.WriteStartObject();
			writer.WriteString("name", events.RelayName);
			writer.WriteString("description", $"{events.Description} {DescribeSenders(senders)} It keeps an event for {RelayEventSet.Retention.TotalHours:0} hours.");
			writer.WriteStartArray("supported_nips");

			foreach (var nip in SupportedNips)
			{
				writer.WriteNumberValue(nip);
			}

			writer.WriteEndArray();
			writer.WriteStartObject("limitation");
			writer.WriteNumber("max_message_length", limits.MaxMessageBytes);
			writer.WriteNumber("max_subscriptions", limits.MaxSubscriptions);
			writer.WriteNumber("max_limit", limits.MaxLimit);
			writer.WriteNumber("max_subid_length", limits.MaxSubscriptionIdLength);
			writer.WriteNumber("max_event_tags", limits.MaxEventTags);
			writer.WriteNumber("max_content_length", limits.MaxContentLength);

			writer.WriteNumber("created_at_upper_limit", (long)limits.MaxClockDifference.TotalSeconds);
			writer.WriteBoolean("auth_required", false);
			writer.WriteBoolean("payment_required", false);

			// The relay takes certain kinds of events only, which is for a client to know before it sends others.
			writer.WriteBoolean("restricted_writes", true);
			writer.WriteEndObject();

			// For how long the relay keeps what it takes, whatever its kind.
			writer.WriteStartArray("retention");
			writer.WriteStartObject();
			writer.WriteNumber("time", retention);
			writer.WriteEndObject();
			writer.WriteEndArray();
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan);
	}
}
