using System.Numerics;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// What a fidelity bond is worth to someone who chooses between makers. The formula is the one of JoinMarket: the bond
/// counts for the interest that its owner gives up by locking the coins, raised to a power above one, so that one large
/// bond is worth more than the same coins spread over many small ones.
/// </summary>
public static class BondValue
{
	/// <summary>The yearly interest that locked coins are assumed to give up.</summary>
	public const double DefaultInterestRate = 0.015;

	public const double DefaultExponent = 1.3;

	private const double SecondsInYear = 60 * 60 * 24 * 365.2425;

	/// <summary>
	/// The value of a bond. It is fixed from the confirmation of the bond until its locktime: what counts is how long the
	/// coins were committed for. After the locktime it wears off, as the coins could be spent any time.
	/// </summary>
	/// <param name="confirmationTime">The time of the block that confirmed the bond.</param>
	/// <returns>A number without a unit. It only serves to compare bonds.</returns>
	public static BigInteger Calculate(
		Money amount,
		DateTimeOffset confirmationTime,
		BondLocktime locktime,
		DateTimeOffset currentTime,
		double interestRate = DefaultInterestRate,
		double exponent = DefaultExponent)
	{
		var timeToMaturity = (locktime.UnixTime - confirmationTime.ToUnixTimeSeconds()) / SecondsInYear;
		var locktimeYears = locktime.UnixTime / SecondsInYear;
		var currentYears = currentTime.ToUnixTimeSeconds() / SecondsInYear;

		var earned = Math.Min(1, Math.Exp(interestRate * timeToMaturity) - 1);
		var wornOff = Math.Min(1, Math.Exp(interestRate * Math.Max(0, currentYears - locktimeYears)) - 1);
		var share = Math.Max(0, earned - wornOff);

		return new BigInteger(Math.Pow(amount.Satoshi * share, exponent));
	}
}
