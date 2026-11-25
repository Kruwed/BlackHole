namespace WalletWasabi.NostrRelay;

/// <summary>Which events a relay of the application takes: its user switches between the two.</summary>
public enum RelayEventChoice
{
	/// <summary>The events of Black Hole, kept for a short time: see <see cref="BlackHoleEventSet"/>.</summary>
	BlackHole,

	/// <summary>The kinds of events that the user named, kept in a file: see <see cref="CustomEventSet"/>.</summary>
	Custom
}

/// <summary>What the user chose for the relay of the application.</summary>
/// <param name="Events">Which events the relay takes.</param>
/// <param name="Kinds">The kinds of events that the user named. They count while the choice is <see cref="RelayEventChoice.Custom"/>, and are kept for the next time otherwise.</param>
public sealed record NostrRelaySettings(RelayEventChoice Events, KindList Kinds)
{
	public static NostrRelaySettings Default { get; } = new(RelayEventChoice.BlackHole, KindList.Empty);

	/// <summary>Reads the choice as the settings keep it. What cannot be read is the choice that a new installation has.</summary>
	public static NostrRelaySettings Parse(string events, IEnumerable<string> kinds) =>
		new(
			Enum.TryParse<RelayEventChoice>(events, ignoreCase: true, out var choice) && Enum.IsDefined(choice) ? choice : RelayEventChoice.BlackHole,
			KindList.FromStrings(kinds));

	/// <summary>Why a relay cannot run with this choice. Null if it can.</summary>
	public string? Problem =>
		Events == RelayEventChoice.Custom && Kinds.IsEmpty
			? "Name one kind of event at least for the relay to keep, like 1."
			: null;

	/// <summary>Makes the set of events that the choice stands for.</summary>
	/// <param name="network">The network of the wallet, which the events of Black Hole are for.</param>
	public RelayEventSet CreateEventSet(Network network) =>
		Events == RelayEventChoice.Custom ? new CustomEventSet(Kinds) : new BlackHoleEventSet(network);
}
