using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WalletWasabi.NostrRelay;

/// <summary>Which events a relay of the application takes: its user switches between the two.</summary>
public enum RelayEventChoice
{
	/// <summary>The events of Black Hole: see <see cref="BlackHoleEventSet"/>.</summary>
	BlackHole,

	/// <summary>The kinds of events that the user named: see <see cref="CustomEventSet"/>.</summary>
	Custom
}

/// <summary>What the user chose for the relay of the application.</summary>
/// <param name="Events">Which events the relay takes.</param>
/// <param name="Kinds">The kinds of events that the user named. They count while the choice is <see cref="RelayEventChoice.Custom"/>, and are kept for the next time otherwise.</param>
/// <param name="Senders">Whose events the relay takes: the two filters that the user set.</param>
public sealed record NostrRelaySettings(RelayEventChoice Events, KindList Kinds, RelaySenderRules Senders)
{
	/// <summary>The most that a fidelity bond can hold, in sats: all the coins that there will ever be.</summary>
	public const long MaxBond = 21_000_000L * 100_000_000L;

	public static NostrRelaySettings Default { get; } = new(RelayEventChoice.BlackHole, KindList.Empty, RelaySenderRules.Anyone);

	/// <summary>Reads the choice as the settings keep it. What cannot be read is the choice that a new installation has.</summary>
	/// <param name="minBond">The least that has to stand behind a key, in bitcoin, see <see cref="TryParseBond"/>.</param>
	/// <param name="whitelist">The keys that need no bond, as npubs or in hex.</param>
	public static NostrRelaySettings Parse(string events, IEnumerable<string> kinds, bool isBondRequired = false, string minBond = "0", IEnumerable<string>? whitelist = null) =>
		new(
			Enum.TryParse<RelayEventChoice>(events, ignoreCase: true, out var choice) && Enum.IsDefined(choice) ? choice : RelayEventChoice.BlackHole,
			KindList.FromStrings(kinds),
			new RelaySenderRules(
				isBondRequired,
				TryParseBond(minBond, out var sats, out _) ? sats : 0,
				RelaySenderRules.ReadWhitelist(whitelist ?? [])));

	/// <summary>
	/// Reads an amount of bitcoin the way the user writes the least that a fidelity bond has to hold: digits, with a
	/// point before the parts of a coin, like 0.5. An empty text is no minimum.
	/// </summary>
	/// <param name="sats">The amount in sats.</param>
	/// <param name="problem">What is wrong with the text, if it is no such amount.</param>
	public static bool TryParseBond(string text, out long sats, [NotNullWhen(false)] out string? problem)
	{
		text = text.Trim();
		sats = 0;
		problem = null;

		if (text.Length == 0)
		{
			return true;
		}

		if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var coins))
		{
			problem = "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.";
			return false;
		}

		var exact = coins * Money.COIN;

		if (exact != decimal.Truncate(exact))
		{
			problem = "A bitcoin has 8 places after the point, and no more.";
			return false;
		}

		if (exact > MaxBond)
		{
			problem = "There are 21,000,000 bitcoin and no more.";
			return false;
		}

		sats = (long)exact;
		return true;
	}

	/// <summary>Writes an amount of sats in bitcoin, the way <see cref="TryParseBond"/> reads it: without zeros that say nothing, like 0.5.</summary>
	public static string FormatBond(long sats) => (sats / (decimal)Money.COIN).ToString("0.########", CultureInfo.InvariantCulture);

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
