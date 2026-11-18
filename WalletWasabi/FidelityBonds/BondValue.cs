using System.Numerics;

namespace WalletWasabi.FidelityBonds;

/// <summary>
/// What a fidelity bond is worth to someone who chooses between makers. The formula is the one of JoinMarket, with the
/// time counted in blocks: the bond counts for the interest that its owner gives up by locking the coins, raised to a
/// power above one, so that one large bond is worth more than the same coins spread over many small ones.
/// </summary>
/// <remarks>
/// Counted in blocks, the value depends on nothing but the chain: everyone gets the same number for the same bond at
/// the same height, whatever their clock says.
/// </remarks>
public static class BondValue
{
	/// <summary>The yearly interest that locked coins are assumed to give up.</summary>
	public const double DefaultInterestRate = 0.015;

	public const double DefaultExponent = 1.3;

	/// <summary>The blocks of a year of 365.2425 days, at one block every ten minutes.</summary>
	public const double BlocksPerYear = 365.2425 * 24 * 6;

	/// <summary>
	/// The value of a bond. It is fixed from the confirmation of the bond until its unlock height: what counts is how long
	/// the coins were committed for. After the unlock height it wears off, as the coins could be spent any time.
	/// </summary>
	/// <param name="confirmationHeight">The height of the block that confirmed the bond.</param>
	/// <param name="currentHeight">The height of the last block of the chain.</param>
	/// <returns>A number without a unit. It only serves to compare bonds.</returns>
	public static BigInteger Calculate(
		Money amount,
		uint confirmationHeight,
		uint unlockHeight,
		uint currentHeight,
		double interestRate = DefaultInterestRate,
		double exponent = DefaultExponent)
	{
		var yearsLocked = ((double)unlockHeight - confirmationHeight) / BlocksPerYear;
		var yearsUnlocked = Math.Max(0, ((double)currentHeight - unlockHeight) / BlocksPerYear);

		var earned = Math.Min(1, Math.Exp(interestRate * yearsLocked) - 1);
		var wornOff = Math.Min(1, Math.Exp(interestRate * yearsUnlocked) - 1);
		var share = Math.Max(0, earned - wornOff);

		return new BigInteger(Math.Pow(amount.Satoshi * share, exponent));
	}
}
