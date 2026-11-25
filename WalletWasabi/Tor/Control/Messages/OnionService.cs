using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using WalletWasabi.Tor.Control.Exceptions;

namespace WalletWasabi.Tor.Control.Messages;

/// <summary>
/// How Tor makes the visitors of an onion service work for their turn once more of them come than the service lets in:
/// each one solves a puzzle, and those who put more work into it are let in first. While fewer come than the service
/// lets in, nobody has to solve anything.
/// </summary>
/// <param name="QueueRate">How many visitors are let in per second, when that many come.</param>
/// <param name="QueueBurst">How many may be let in at once after a quiet time.</param>
public sealed record OnionServiceProofOfWork(int QueueRate, int QueueBurst);

/// <summary>
/// What an onion service is to be like that Tor is asked for: which port of its onion address leads to which port of
/// this computer, which key gives it its address, and how Tor guards it.
/// </summary>
/// <param name="VirtualPort">The port of the onion address that visitors connect to.</param>
/// <param name="Target">Where Tor hands the visitors over: a port of this computer.</param>
public sealed partial record OnionServiceOptions(int VirtualPort, IPEndPoint Target)
{
	/// <summary>What Tor is given in the place of a key when it is to make a new one.</summary>
	private const string NewKey = "NEW:ED25519-V3";

	/// <summary>
	/// The key of the service, as Tor told it when the service was made the first time, see <see cref="OnionService.PrivateKey"/>.
	/// The address of the service follows from it. Null for a new service: Tor makes a key then, and tells it.
	/// </summary>
	public string? PrivateKey { get; init; }

	/// <summary>
	/// How many connections a visitor may have open at once over one circuit. A circuit that asks for more is closed.
	/// Zero for no limit.
	/// </summary>
	public int MaxStreamsPerCircuit { get; init; }

	/// <summary>How visitors are made to work for their turn when too many come. Null for a service that lets everybody queue alike.</summary>
	public OnionServiceProofOfWork? ProofOfWork { get; init; }

	/// <summary>Whether a text is a key as Tor tells it: the kind of the key, a colon, and the key in Base64.</summary>
	public static bool IsPrivateKey([NotNullWhen(true)] string? text) =>
		text is not null && PrivateKeyRegex().IsMatch(text);

	/// <summary>Writes the command that asks Tor for the service.</summary>
	/// <exception cref="ArgumentException">Something of it is not what Tor could be asked for.</exception>
	public string ToCommand()
	{
		if (VirtualPort is < 1 or > 65535)
		{
			throw new ArgumentException($"{VirtualPort} is not a port.", nameof(VirtualPort));
		}

		if (Target.Port is < 1 or > 65535)
		{
			throw new ArgumentException($"{Target.Port} is not a port.", nameof(Target));
		}

		// A key comes from a file, and it goes into a command: nothing but a key may be in it.
		if (PrivateKey is not null && !IsPrivateKey(PrivateKey))
		{
			throw new ArgumentException("The key of the onion service is not a key that Tor made.", nameof(PrivateKey));
		}

		if (MaxStreamsPerCircuit < 0)
		{
			throw new ArgumentException("A limit cannot be negative.", nameof(MaxStreamsPerCircuit));
		}

		if (ProofOfWork is { QueueRate: < 1 } or { QueueBurst: < 1 })
		{
			throw new ArgumentException("Visitors are let in at a rate of one per second at least.", nameof(ProofOfWork));
		}

		List<string> parts = ["ADD_ONION", PrivateKey ?? NewKey];
		List<string> flags = [];

		// Tor knows a key that it is given, so it need not tell it back.
		if (PrivateKey is not null)
		{
			flags.Add("DiscardPK");
		}

		if (MaxStreamsPerCircuit > 0)
		{
			flags.Add("MaxStreamsCloseCircuit");
		}

		if (flags.Count > 0)
		{
			parts.Add($"Flags={string.Join(',', flags)}");
		}

		if (MaxStreamsPerCircuit > 0)
		{
			parts.Add($"MaxStreams={MaxStreamsPerCircuit}");
		}

		if (ProofOfWork is { } proofOfWork)
		{
			parts.Add("PoWDefensesEnabled=1");
			parts.Add($"PoWQueueRate={proofOfWork.QueueRate}");
			parts.Add($"PoWQueueBurst={proofOfWork.QueueBurst}");
		}

		parts.Add($"Port={VirtualPort},{Target.Address}:{Target.Port}");

		return string.Join(' ', parts) + "\r\n";
	}

	// The end is the end of the text: "$" would let a line break through, and with it a second command.
	[GeneratedRegex(@"\AED25519-V3:[A-Za-z0-9+/]{43,128}={0,2}\z", RegexOptions.CultureInvariant)]
	private static partial Regex PrivateKeyRegex();
}

/// <summary>An onion service that Tor made.</summary>
/// <param name="ServiceId">The name of the service: its onion address without the ending.</param>
/// <param name="PrivateKey">
/// The key of the service, which makes Tor give it the same address the next time. Whoever has it can run a service at
/// that address.
/// </param>
public sealed partial record OnionService(string ServiceId, string PrivateKey)
{
	private const string ServiceIdMarker = "ServiceID=";
	private const string PrivateKeyMarker = "PrivateKey=";

	/// <summary>The onion address of the service.</summary>
	public string Host => $"{ServiceId}.onion";

	/// <summary>Whether a text is the name of an onion service of the kind that Tor makes today.</summary>
	public static bool IsServiceId([NotNullWhen(true)] string? text) =>
		text is not null && ServiceIdRegex().IsMatch(text);

	/// <summary>Reads what Tor answered to the command of <see cref="OnionServiceOptions.ToCommand"/>.</summary>
	/// <param name="givenKey">The key that Tor was given, if it was given one. Tor does not tell that key back.</param>
	/// <exception cref="TorControlException">Tor did not make the service, or what it answered cannot be read.</exception>
	public static OnionService FromReply(TorControlReply reply, string? givenKey)
	{
		if (!reply.Success)
		{
			throw new TorControlException($"Tor did not make the onion service ({Describe(reply)}).");
		}

		var serviceId = Find(reply, ServiceIdMarker);

		if (!IsServiceId(serviceId))
		{
			throw new TorControlException("Tor did not tell the address of the onion service.");
		}

		var privateKey = givenKey ?? Find(reply, PrivateKeyMarker);

		if (!OnionServiceOptions.IsPrivateKey(privateKey))
		{
			throw new TorControlException("Tor did not tell the key of the onion service.");
		}

		return new OnionService(serviceId, privateKey);
	}

	/// <summary>
	/// Whether an event of Tor tells that the address of a service is published: Tor has handed the description of the
	/// service to one of the relays that keep such descriptions, so visitors can find the service from now on.
	/// </summary>
	/// <param name="torEvent">An event of the kind <c>HS_DESC</c>, or any other.</param>
	public static bool IsPublished(TorControlReply torEvent, string serviceId)
	{
		if (torEvent.StatusCode != StatusCode.AsynchronousEventNotify || torEvent.ResponseLines.Count == 0)
		{
			return false;
		}

		// HS_DESC UPLOADED <service> <authentication> <relay> ...
		var words = torEvent.ResponseLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);

		return words is ["HS_DESC", "UPLOADED", var service, ..] && service == serviceId;
	}

	/// <summary>What Tor said, in one line, for a message about what went wrong.</summary>
	public static string Describe(TorControlReply reply) =>
		reply.ResponseLines.Count > 0
			? $"{(int)reply.StatusCode} {reply.ResponseLines[0]}"
			: $"{(int)reply.StatusCode}";

	private static string? Find(TorControlReply reply, string marker) =>
		reply.ResponseLines.FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal))?[marker.Length..];

	[GeneratedRegex(@"\A[a-z2-7]{56}\z", RegexOptions.CultureInvariant)]
	private static partial Regex ServiceIdRegex();
}
