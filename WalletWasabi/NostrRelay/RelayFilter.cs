using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// What a client asks for when it subscribes: events with one of some ids, of some keys, of some kinds, with some
/// tags, from a span of time. An event has to meet everything that a filter names, and a filter that names nothing
/// asks for every event.
/// </summary>
public sealed class RelayFilter
{
	private RelayFilter(
		ImmutableHashSet<string>? ids,
		ImmutableHashSet<string>? authors,
		ImmutableHashSet<int>? kinds,
		ImmutableDictionary<string, ImmutableHashSet<string>> tags,
		long? since,
		long? until,
		int? limit)
	{
		Ids = ids;
		Authors = authors;
		Kinds = kinds;
		Tags = tags;
		Since = since;
		Until = until;
		Limit = limit;
	}

	/// <summary>The ids that are asked for. Null if any id will do.</summary>
	public ImmutableHashSet<string>? Ids { get; }

	/// <summary>The keys whose events are asked for. Null if any key will do.</summary>
	public ImmutableHashSet<string>? Authors { get; }

	/// <summary>The kinds that are asked for. Null if any kind will do.</summary>
	public ImmutableHashSet<int>? Kinds { get; }

	/// <summary>The tags that are asked for, by their names of one letter: an event has to have each of them with one of the values.</summary>
	public ImmutableDictionary<string, ImmutableHashSet<string>> Tags { get; }

	/// <summary>Events that were made before this time are not asked for. Seconds since 1970.</summary>
	public long? Since { get; }

	/// <summary>Events that were made after this time are not asked for.</summary>
	public long? Until { get; }

	/// <summary>How many of the stored events are asked for at most, the newest first.</summary>
	public int? Limit { get; }

	/// <summary>Whether no stored event can meet the filter, so that there is nothing to look for.</summary>
	public bool IsEmpty =>
		Ids is { Count: 0 } || Authors is { Count: 0 } || Kinds is { Count: 0 } || Limit == 0
		|| Tags.Values.Any(values => values.Count == 0)
		|| (Since is { } since && Until is { } until && since > until);

	/// <summary>Reads a filter.</summary>
	/// <param name="problem">Why it is no filter that the relay answers, in the words that the relay answers with.</param>
	public static bool TryParse(JsonElement json, RelayLimits limits, [NotNullWhen(true)] out RelayFilter? filter, [NotNullWhen(false)] out string? problem)
	{
		filter = null;

		if (json.ValueKind != JsonValueKind.Object)
		{
			problem = "invalid: a filter is an object";
			return false;
		}

		ImmutableHashSet<string>? ids = null;
		ImmutableHashSet<string>? authors = null;
		ImmutableHashSet<int>? kinds = null;
		var tags = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(StringComparer.Ordinal);
		long? since = null;
		long? until = null;
		int? limit = null;

		foreach (var property in json.EnumerateObject())
		{
			var name = property.Name;
			var value = property.Value;

			switch (name)
			{
				case "ids":
					if (ids is not null || !TryReadTexts(value, limits, text => RelayEvent.IsLowerHex(text, 64), out ids))
					{
						problem = $"invalid: the ids of a filter are a list of up to {limits.MaxFilterValues} ids, each 64 characters of lowercase hex";
						return false;
					}

					break;

				case "authors":
					if (authors is not null || !TryReadTexts(value, limits, text => RelayEvent.IsLowerHex(text, 64), out authors))
					{
						problem = $"invalid: the authors of a filter are a list of up to {limits.MaxFilterValues} keys, each 64 characters of lowercase hex";
						return false;
					}

					break;

				case "kinds":
					if (kinds is not null || !TryReadKinds(value, limits, out kinds))
					{
						problem = $"invalid: the kinds of a filter are a list of up to {limits.MaxFilterValues} numbers from 0 to 65535";
						return false;
					}

					break;

				case "since":
					if (since is not null || !TryReadTime(value, out since))
					{
						problem = "invalid: the since of a filter is a time in seconds";
						return false;
					}

					break;

				case "until":
					if (until is not null || !TryReadTime(value, out until))
					{
						problem = "invalid: the until of a filter is a time in seconds";
						return false;
					}

					break;

				case "limit":
					if (limit is not null || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 0)
					{
						problem = "invalid: the limit of a filter is a number that is not negative";
						return false;
					}

					limit = number;
					break;

				// A tag is asked for by its name of one letter, with a number sign in front.
				case ['#', (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')]:
					if (tags.ContainsKey(name[1..]) || !TryReadTexts(value, limits, _ => true, out var values))
					{
						problem = $"invalid: the values of a tag in a filter are a list of up to {limits.MaxFilterValues} texts";
						return false;
					}

					tags[name[1..]] = values;
					break;

				// Whoever asks for something that the relay does not know would get more than it asked for if that part
				// of the question were passed over.
				default:
					problem = "unsupported: the filter contains unknown elements";
					return false;
			}
		}

		filter = new RelayFilter(ids, authors, kinds, tags.ToImmutable(), since, until, limit);
		problem = null;
		return true;
	}

	/// <summary>Whether an event is one that the filter asks for. The limit of the filter does not count here: it is about the stored events.</summary>
	public bool Matches(RelayEvent relayEvent)
	{
		if ((Ids is not null && !Ids.Contains(relayEvent.Id))
			|| (Authors is not null && !Authors.Contains(relayEvent.PublicKey))
			|| (Kinds is not null && !Kinds.Contains(relayEvent.Kind))
			|| (Since is { } since && relayEvent.CreatedAt < since)
			|| (Until is { } until && relayEvent.CreatedAt > until))
		{
			return false;
		}

		foreach (var (name, values) in Tags)
		{
			var hasOne = false;

			foreach (var tag in relayEvent.Tags)
			{
				// A tag is found by its name and by what follows the name.
				if (tag.Length > 1 && tag[0] == name && values.Contains(tag[1]))
				{
					hasOne = true;
					break;
				}
			}

			if (!hasOne)
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryReadTexts(JsonElement json, RelayLimits limits, Func<string, bool> isValid, [NotNullWhen(true)] out ImmutableHashSet<string>? texts)
	{
		texts = null;

		if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() > limits.MaxFilterValues)
		{
			return false;
		}

		var read = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

		foreach (var item in json.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String)
			{
				return false;
			}

			string? text;

			try
			{
				text = item.GetString();
			}
			catch (InvalidOperationException)
			{
				return false;
			}

			if (text is null || !isValid(text))
			{
				return false;
			}

			read.Add(text);
		}

		texts = read.ToImmutable();
		return true;
	}

	private static bool TryReadKinds(JsonElement json, RelayLimits limits, [NotNullWhen(true)] out ImmutableHashSet<int>? kinds)
	{
		kinds = null;

		if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() > limits.MaxFilterValues)
		{
			return false;
		}

		var read = ImmutableHashSet.CreateBuilder<int>();

		foreach (var item in json.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var kind) || kind is < 0 or > 65_535)
			{
				return false;
			}

			read.Add(kind);
		}

		kinds = read.ToImmutable();
		return true;
	}

	private static bool TryReadTime(JsonElement json, [NotNullWhen(true)] out long? time)
	{
		time = null;

		if (json.ValueKind != JsonValueKind.Number || !json.TryGetInt64(out var seconds) || seconds < 0)
		{
			return false;
		}

		time = seconds;
		return true;
	}
}
