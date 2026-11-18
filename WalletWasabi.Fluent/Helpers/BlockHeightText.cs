using System.Globalization;
using WalletWasabi.Fluent.Extensions;

namespace WalletWasabi.Fluent.Helpers;

/// <summary>
/// Writes block heights, and tells when the chain is expected to reach one.
/// </summary>
public static class BlockHeightText
{
	/// <summary>The chain aims at a block every ten minutes.</summary>
	public static readonly TimeSpan BlockInterval = TimeSpan.FromMinutes(10);

	/// <summary>Within this many blocks, two days, the expected time is told with its hour. Further ahead only the day means something.</summary>
	private const int BlocksToldWithTime = 288;

	public static string Format(uint height) => height.ToString("N0", CultureInfo.InvariantCulture);

	/// <summary>"1 block" or "52,560 blocks".</summary>
	public static string Count(uint blocks) => blocks == 1 ? "1 block" : $"{Format(blocks)} blocks";

	/// <summary>
	/// When the chain reaches the height if it goes on at its pace. It is an estimation: the blocks come at random, and
	/// over months a little faster or slower than every ten minutes.
	/// </summary>
	/// <param name="chainHeight">The height of the last block of the chain at the moment of <paramref name="now"/>.</param>
	public static DateTimeOffset Estimate(uint height, uint chainHeight, DateTimeOffset now) =>
		now + TimeSpan.FromTicks(BlockInterval.Ticks * ((long)height - chainHeight));

	/// <summary>The expected time of a height that the chain did not reach yet: "March 2, 2027", or "14:30 on November 19, 2026" when it is near.</summary>
	public static string DescribeExpectedTime(uint height, uint chainHeight, DateTimeOffset now) =>
		Estimate(height, chainHeight, now).ToUserFacingString(withTime: (long)height - chainHeight <= BlocksToldWithTime);

	/// <summary>
	/// Tells until when coins are locked that unlock at the height, and how long that is from now:
	/// "Locked until block 1,028,732, expected around March 2, 2027. That is 52,560 blocks from now, about 1 year."
	/// </summary>
	public static string DescribeLock(uint unlockHeight, uint chainHeight, DateTimeOffset now)
	{
		if (unlockHeight <= chainHeight)
		{
			return $"Locked until block {Format(unlockHeight)}, which the chain has reached already.";
		}

		var expected = Estimate(unlockHeight, chainHeight, now);

		return $"Locked until block {Format(unlockHeight)}, expected around {DescribeExpectedTime(unlockHeight, chainHeight, now)}. " +
			$"That is {Count(unlockHeight - chainHeight)} from now, about {DurationText.Between(now, expected, withHours: true)}.";
	}
}
