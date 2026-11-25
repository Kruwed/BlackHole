using System.Diagnostics.CodeAnalysis;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using NNostr.Client.Protocols;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// The key of whoever signs events, as people write it: as an npub, or as 64 characters of hex. Inside the relay a key
/// is always the hex, in lowercase, which is how events name it.
/// </summary>
public static class RelayKey
{
	private const string NpubPrefix = "npub1";

	/// <summary>Reads a key that is written as an npub or as hex.</summary>
	/// <param name="hex">The key as events name it.</param>
	public static bool TryParse(string text, [NotNullWhen(true)] out string? hex)
	{
		hex = null;
		text = text.Trim();

		if (text.StartsWith(NpubPrefix, StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				hex = Encoders.Hex.EncodeData(text.ToLowerInvariant().FromNIP19Npub().ToBytes());
				return true;
			}
			catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
			{
				// It looks like an npub and is none: a character is wrong or missing.
				return false;
			}
		}

		var lower = text.ToLowerInvariant();

		// A key is the x coordinate of a point of the curve. What is none can sign nothing.
		if (RelayEvent.IsLowerHex(lower, 64) && ECXOnlyPubKey.TryCreate(Encoders.Hex.DecodeData(lower), out _))
		{
			hex = lower;
			return true;
		}

		return false;
	}

	/// <summary>Writes a key as an npub, the way people hand keys to each other.</summary>
	/// <param name="hex">The key as events name it.</param>
	public static string ToNpub(string hex) =>
		ECXOnlyPubKey.Create(Encoders.Hex.DecodeData(hex)).ToNIP19();
}

/// <summary>
/// Whose events a relay takes, and whose it keeps the longest when its room is used up. The user of the relay sets
/// two filters: one that asks for a fidelity bond, and one that names keys which need none.
/// </summary>
/// <remarks>
/// <para>
/// A key has a fidelity bond behind it once it has shown the proof of one in an event, see <see cref="RelayBondClaim"/>,
/// and the node has found the bond. The coins of a bond count once: if several keys show the same bond, they share
/// what it holds in equal parts. So spreading one bond over many keys gains nothing.
/// </para>
/// <para>
/// With the filter on, the relay takes events from keys whose share of a bond is the minimum or more, and from the
/// keys of the list. Without it the relay takes events from anybody. Either way, when the room for events is used up,
/// the events of the keys with the least behind them make way first, and the events of the keys of the list last.
/// </para>
/// </remarks>
/// <param name="IsBondRequired">Whether a key needs a fidelity bond for its events to be taken.</param>
/// <param name="MinBond">What the share of a bond behind a key has to be at least, in sats, where a bond is required.</param>
/// <param name="Whitelist">The keys whose events are taken whatever stands behind them, as events name them.</param>
public sealed record RelaySenderRules(bool IsBondRequired, long MinBond, ImmutableSortedSet<string> Whitelist)
{
	/// <summary>What stands behind the events of a key of the list, for the order in which events make way: more than any bond.</summary>
	public const long WhitelistRank = long.MaxValue;

	/// <summary>The rules of a relay that takes events from anybody.</summary>
	public static RelaySenderRules Anyone { get; } = new(false, 0, []);

	/// <summary>
	/// What has to stand behind a key for its events to be taken: the minimum where a bond is required, and a sat at
	/// least, as a key with nothing behind it has no bond. Nothing where no bond is required.
	/// </summary>
	public long RequiredShare => IsBondRequired ? Math.Max(MinBond, 1) : 0;

	/// <summary>
	/// Reads the keys of a list that the user wrote: npubs or keys in hex, one on a line, or with commas or blanks
	/// between them. A key that is named twice counts once.
	/// </summary>
	/// <param name="problem">What is wrong with the list, if it cannot be read.</param>
	public static bool TryParseWhitelist(string text, [NotNullWhen(true)] out ImmutableSortedSet<string>? keys, [NotNullWhen(false)] out string? problem)
	{
		var read = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);

		foreach (var entry in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			if (!RelayKey.TryParse(entry, out var key))
			{
				keys = null;
				problem = $"'{(entry.Length <= 20 ? entry : entry[..20] + "…")}' is not a key: a key is an npub, or 64 characters of hex.";
				return false;
			}

			read.Add(key);
		}

		keys = read.ToImmutable();
		problem = null;
		return true;
	}

	/// <summary>Reads the keys of a list the way the settings keep them. What is no key is left out.</summary>
	public static ImmutableSortedSet<string> ReadWhitelist(IEnumerable<string> entries) =>
		[.. entries.Select(entry => RelayKey.TryParse(entry, out var key) ? key : null).OfType<string>()];

	/// <summary>Writes the keys of the list as npubs, the way the user reads them and the settings keep them.</summary>
	public string[] WhitelistToNpubs() => [.. Whitelist.Select(RelayKey.ToNpub)];

	public bool Equals(RelaySenderRules? other) =>
		other is not null && IsBondRequired == other.IsBondRequired && MinBond == other.MinBond && Whitelist.SetEquals(other.Whitelist);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(IsBondRequired);
		hash.Add(MinBond);

		foreach (var key in Whitelist)
		{
			hash.Add(key);
		}

		return hash.ToHashCode();
	}
}
