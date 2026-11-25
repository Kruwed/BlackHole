using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace WalletWasabi.NostrRelay;

/// <summary>
/// The kinds of events that the user of a relay named for it to keep: single kinds like <c>1</c>, and spans of kinds
/// like <c>30000-39999</c>. A kind is a number from 0 to 65535 that tells what an event is, a note or a profile for
/// example.
/// </summary>
public sealed class KindList : IEquatable<KindList>
{
	public const int MaxKind = 65_535;

	/// <summary>The spans of kinds in rising order, none of them overlapping the next.</summary>
	private readonly ImmutableArray<(int First, int Last)> _spans;

	private KindList(ImmutableArray<(int First, int Last)> spans)
	{
		_spans = spans;
	}

	public static KindList Empty { get; } = new([]);

	public bool IsEmpty => _spans.IsEmpty;

	/// <summary>
	/// Reads the kinds that the user named: numbers and spans of numbers, with commas, spaces or line breaks between
	/// them. A kind that is named twice counts once.
	/// </summary>
	/// <param name="problem">What is wrong with what the user wrote, if it cannot be read.</param>
	public static bool TryParse(string text, [NotNullWhen(true)] out KindList? kinds, [NotNullWhen(false)] out string? problem)
	{
		kinds = null;

		var spans = new List<(int First, int Last)>();

		foreach (var entry in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
		{
			var ends = entry.Split('-');

			if (ends.Length > 2 || !TryParseKind(ends[0], out var first) || !TryParseKind(ends[^1], out var last))
			{
				problem = $"'{Shorten(entry)}' is not a kind of event: a kind is a number from 0 to {MaxKind}, and a span of kinds is two of them with a hyphen between, like 30000-39999.";
				return false;
			}

			if (first > last)
			{
				problem = $"The span {entry} begins after its end.";
				return false;
			}

			spans.Add((first, last));
		}

		kinds = new KindList(Merge(spans));
		problem = null;
		return true;
	}

	/// <summary>Reads the kinds the way <see cref="ToStrings"/> writes them. What cannot be read is no kind at all.</summary>
	public static KindList FromStrings(IEnumerable<string> entries) =>
		TryParse(string.Join(',', entries), out var kinds, out _) ? kinds : Empty;

	public bool Contains(int kind)
	{
		foreach (var (first, last) in _spans)
		{
			if (kind < first)
			{
				return false;
			}

			if (kind <= last)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The spans of kinds in rising order. A single kind is a span that ends where it begins.</summary>
	public IEnumerable<(int First, int Last)> GetSpans() => _spans;

	/// <summary>Writes the kinds one by one, the way a config file keeps them.</summary>
	public string[] ToStrings() =>
		[.. _spans.Select(span => span.First == span.Last
			? span.First.ToString(CultureInfo.InvariantCulture)
			: string.Create(CultureInfo.InvariantCulture, $"{span.First}-{span.Last}"))];

	/// <summary>Writes the kinds the way the user reads and writes them.</summary>
	public override string ToString() => string.Join(", ", ToStrings());

	public bool Equals(KindList? other) => other is not null && _spans.SequenceEqual(other._spans);

	public override bool Equals(object? obj) => Equals(obj as KindList);

	public override int GetHashCode()
	{
		var hash = new HashCode();

		foreach (var span in _spans)
		{
			hash.Add(span);
		}

		return hash.ToHashCode();
	}

	private static bool TryParseKind(string text, out int kind) =>
		int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out kind) && kind <= MaxKind;

	private static string Shorten(string text) => text.Length <= 20 ? text : text[..20] + "…";

	/// <summary>
	/// Puts the spans in order, and makes one span of spans that overlap. Kinds that follow each other stay as the
	/// user wrote them: the list is shown to the user again.
	/// </summary>
	private static ImmutableArray<(int First, int Last)> Merge(List<(int First, int Last)> spans)
	{
		var merged = ImmutableArray.CreateBuilder<(int First, int Last)>();

		foreach (var span in spans.OrderBy(span => span.First).ThenByDescending(span => span.Last))
		{
			if (merged.Count > 0 && span.First <= merged[^1].Last)
			{
				merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, span.Last));
			}
			else
			{
				merged.Add(span);
			}
		}

		return merged.ToImmutable();
	}
}
