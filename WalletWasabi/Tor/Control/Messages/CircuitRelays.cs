using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace WalletWasabi.Tor.Control.Messages;

/// <summary>
/// The relays that the circuits of a Tor go through, as its control port tells them.
/// </summary>
public static class CircuitRelays
{
	/// <summary>
	/// Reads the answer to <c>GETINFO circuit-status</c> and tells the fingerprints of the relays of the circuits that
	/// are built, every relay once: first hops, middle relays and exits. A circuit that is still being built, or one
	/// that failed or was closed, is no connection.
	/// </summary>
	public static IReadOnlyList<string> GetBuiltRelayFingerprints(TorControlReply reply)
	{
		// A Tor without a circuit answers on one line, and not with a list.
		if (reply.Success && reply.ResponseLines.Count > 0 && reply.ResponseLines[0] == "circuit-status=" && !reply.ResponseLines.Contains("."))
		{
			return [];
		}

		return [.. GetInfoCircuitStatusReply.FromReply(reply).Circuits
			.Where(circuit => circuit.CircuitStatus == CircuitStatus.CircuitStatus.BUILT)
			.SelectMany(circuit => circuit.CircuitPaths)
			.Select(path => path.FingerPrint.TrimStart('$'))
			.Where(fingerprint => fingerprint.Length > 0 && fingerprint.All(Uri.IsHexDigit))
			.Distinct(StringComparer.OrdinalIgnoreCase)];
	}

	/// <summary>
	/// Reads the answer to <c>GETINFO ns/id/&lt;fingerprint&gt;</c>: the entry of a relay in the directory of the Tor
	/// network. The line of the entry that starts with "r" names the relay, and the address of the relay is on it.
	/// </summary>
	/// <returns>Whether the answer told an address. Tor has no entry for a relay that is not in its directory, a bridge for example.</returns>
	public static bool TryParseRelayAddress(TorControlReply reply, [NotNullWhen(true)] out IPAddress? address)
	{
		address = null;

		if (!reply.Success)
		{
			return false;
		}

		foreach (var line in reply.ResponseLines)
		{
			if (!line.StartsWith("r ", StringComparison.Ordinal))
			{
				continue;
			}

			// Of what the line has, only the address is four numbers with dots between them.
			foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
			{
				if (token.Count(character => character == '.') == 3
					&& IPAddress.TryParse(token, out var parsed)
					&& parsed.AddressFamily == AddressFamily.InterNetwork)
				{
					address = parsed;
					return true;
				}
			}
		}

		return false;
	}
}
