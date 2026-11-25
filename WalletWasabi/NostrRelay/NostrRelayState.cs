using WalletWasabi.Tor;

namespace WalletWasabi.NostrRelay;

/// <summary>How it stands with the Nostr relay that the application runs.</summary>
public enum NostrRelayStatus
{
	/// <summary>The application runs no relay.</summary>
	Off,

	/// <summary>The relay is wanted, and Tor has not given it its onion address yet: Tor is starting, or starting again.</summary>
	Starting,

	/// <summary>The relay has its onion address, and Tor has not told yet that the address is published. It may not be found yet.</summary>
	Publishing,

	/// <summary>The relay runs, and its onion address is published: whoever knows the address can reach it.</summary>
	Running,

	/// <summary>The relay is wanted and cannot run. The reason is told, and it is tried again after a while.</summary>
	NotWorking
}

/// <param name="Address">The address of the relay, once it has one: a WebSocket address at an onion host.</param>
/// <param name="Problem">Why the relay does not run, if it is wanted and does not, in words for the user.</param>
public sealed record NostrRelayState(NostrRelayStatus Status, Uri? Address = null, string? Problem = null)
{
	public static NostrRelayState Off { get; } = new(NostrRelayStatus.Off);

	/// <summary>Whether the relay is wanted: it runs, or it is on its way, or it should run and cannot.</summary>
	public bool IsOn => Status != NostrRelayStatus.Off;

	/// <summary>The address of a relay at an onion host, the way a Nostr client is told it. The port is the one that such addresses have when they name none.</summary>
	public static Uri GetAddress(string onionHost) => new($"ws://{onionHost}");

	/// <summary>The address the way it is shown and copied: without the stroke that a program puts at its end.</summary>
	public static string Format(Uri address) => address.GetLeftPart(UriPartial.Authority);

	/// <summary>Tells how it stands with a relay from how it stands with the onion service that it is reached at.</summary>
	public static NostrRelayState From(OnionServiceState onionService) =>
		onionService switch
		{
			{ Status: OnionServiceStatus.Stopped } => Off,
			{ Status: OnionServiceStatus.Publishing, Host: { } host } => new NostrRelayState(NostrRelayStatus.Publishing, GetAddress(host)),
			{ Status: OnionServiceStatus.Published, Host: { } host } => new NostrRelayState(NostrRelayStatus.Running, GetAddress(host)),
			{ Status: OnionServiceStatus.Failed } => new NostrRelayState(NostrRelayStatus.NotWorking, Problem: onionService.Problem),
			_ => new NostrRelayState(NostrRelayStatus.Starting)
		};
}
