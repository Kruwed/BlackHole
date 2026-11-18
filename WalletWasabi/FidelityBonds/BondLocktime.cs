using System.Diagnostics.CodeAnalysis;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// The moment a fidelity bond can be spent from. As in JoinMarket it is the first moment of a month in UTC, and there are
/// 960 of them: January 2020 to December 2099.
/// </summary>
/// <remarks>
/// That there are so few makes a wallet able to find its bonds again from nothing but its seed: it derives one key for each
/// of these months and looks for all of the 960 scripts.
/// </remarks>
public readonly record struct BondLocktime : IComparable<BondLocktime>
{
	/// <summary>The number of months a bond can unlock in.</summary>
	public const int Count = 80 * 12;

	private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

	private BondLocktime(int index)
	{
		Index = index;
	}

	/// <summary>
	/// The number of the month, with January 2020 as 0. JoinMarket calls it the timenumber. It is also the index of the key
	/// of the bond on its chain of keys.
	/// </summary>
	public int Index { get; }

	public DateTimeOffset Date => Epoch.AddMonths(Index);

	/// <summary>The locktime as the script and the transaction that spends the bond state it: in seconds since 1970.</summary>
	public long UnixTime => Date.ToUnixTimeSeconds();

	public LockTime LockTime => new(Date);

	public static BondLocktime First => new(0);

	public static BondLocktime Last => new(Count - 1);

	/// <summary>Every month a bond can unlock in, the earliest first.</summary>
	public static IEnumerable<BondLocktime> All => Enumerable.Range(0, Count).Select(index => new BondLocktime(index));

	public static BondLocktime FromIndex(int index) =>
		index is >= 0 and < Count
			? new BondLocktime(index)
			: throw new ArgumentOutOfRangeException(nameof(index), index, $"A fidelity bond unlocks in one of {Count} months, which are numbered from 0.");

	/// <param name="month">January is 1.</param>
	public static BondLocktime FromMonth(int year, int month)
	{
		if (month is < 1 or > 12)
		{
			throw new ArgumentOutOfRangeException(nameof(month), month, "There are twelve months.");
		}

		var index = (year - Epoch.Year) * 12 + (month - 1);

		return index is >= 0 and < Count
			? new BondLocktime(index)
			: throw new ArgumentOutOfRangeException(nameof(year), year, $"A fidelity bond unlocks between {First.Date:yyyy-MM} and {Last.Date:yyyy-MM}.");
	}

	/// <summary>
	/// Tells the locktime that a number of seconds since 1970 stands for, if it is one: exactly the start of a month in UTC.
	/// </summary>
	public static bool TryFromUnixTime(long unixTime, [NotNullWhen(true)] out BondLocktime? locktime)
	{
		locktime = null;

		if (unixTime < First.UnixTime || unixTime > Last.UnixTime)
		{
			return false;
		}

		var date = DateTimeOffset.FromUnixTimeSeconds(unixTime);
		var candidate = new BondLocktime((date.Year - Epoch.Year) * 12 + (date.Month - 1));

		if (candidate.UnixTime != unixTime)
		{
			return false;
		}

		locktime = candidate;
		return true;
	}

	/// <summary>The first locktime after the given moment, or null if the last one is over by then.</summary>
	public static BondLocktime? FirstAfter(DateTimeOffset time)
	{
		var utc = time.ToUniversalTime();
		var index = Math.Max(0, (utc.Year - Epoch.Year) * 12 + (utc.Month - 1));

		// The start of the month of the moment is not after it, unless the moment is before all of them.
		while (index < Count && new BondLocktime(index).Date <= utc)
		{
			index++;
		}

		return index < Count ? new BondLocktime(index) : null;
	}

	public int CompareTo(BondLocktime other) => Index.CompareTo(other.Index);

	public override string ToString() => Date.ToString("yyyy-MM-dd");
}
