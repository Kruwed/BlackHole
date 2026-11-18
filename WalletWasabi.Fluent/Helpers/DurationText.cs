namespace WalletWasabi.Fluent.Helpers;

/// <summary>
/// Tells how long it is from one moment to another in words, by the calendar: "1 year and 2 months", "3 months and 12 days".
/// </summary>
public static class DurationText
{
	/// <summary>The time from one moment to a later one, in its two largest units. Less than a day is "less than a day".</summary>
	public static string Between(DateTimeOffset from, DateTimeOffset to)
	{
		from = from.ToUniversalTime();
		to = to.ToUniversalTime();

		if (to <= from)
		{
			return "no time";
		}

		// Whole months by the calendar, then the days that are left.
		var months = (to.Year - from.Year) * 12 + (to.Month - from.Month);
		if (from.AddMonths(months) > to)
		{
			months--;
		}

		var days = (int)(to - from.AddMonths(months)).TotalDays;
		var years = months / 12;
		months %= 12;

		return (years, months, days) switch
		{
			(0, 0, 0) => "less than a day",
			(0, 0, _) => Count(days, "day"),
			(0, _, 0) => Count(months, "month"),
			(0, _, _) => $"{Count(months, "month")} and {Count(days, "day")}",
			(_, 0, _) => Count(years, "year"),
			_ => $"{Count(years, "year")} and {Count(months, "month")}"
		};
	}

	private static string Count(int number, string unit) => number == 1 ? $"1 {unit}" : $"{number} {unit}s";
}
