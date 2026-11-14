using NBitcoin;
using System.Diagnostics.CodeAnalysis;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Extensions;

namespace WalletWasabi.Userfacing;

/// <summary>
/// Rules for the wallet password. The password only encrypts the wallet secret in the wallet file:
/// it takes no part in deriving the keys and it is not needed to recover the wallet from its backup.
/// </summary>
public static class PasswordHelper
{
	public const int MaxPasswordLength = 150;
	public const string MatchingMessage = "Passwords don't match.";
	public const string WhitespaceMessage = "Leading and trailing white spaces are not allowed!";
	public const string PasswordTooLongMessage = "Password is too long.";

	public static bool IsTooLong(string? password, out string? limitedPassword)
	{
		limitedPassword = password;
		if (password is null)
		{
			return false;
		}

		if (IsTooLong(password.Length))
		{
			limitedPassword = password[..MaxPasswordLength];
			return true;
		}

		return false;
	}

	public static bool IsTooLong(int length)
	{
		return length > MaxPasswordLength;
	}

	public static bool IsTrimmable(string? password, [NotNullWhen(true)] out string? trimmedPassword)
	{
		if (password is { } && password.IsTrimmable())
		{
			trimmedPassword = password.Trim();
			return true;
		}

		trimmedPassword = password;
		return false;
	}

	public static bool TryPassword(KeyManager keyManager, string password)
	{
		try
		{
			GetMasterExtKey(keyManager, password);
		}
		catch
		{
			return false;
		}

		return true;
	}

	public static void Guard(string password)
	{
		if (IsTooLong(password, out _)) // Password should be formatted, before entering here.
		{
			throw new FormatException(PasswordTooLongMessage);
		}

		if (IsTrimmable(password, out _)) // Password should be formatted, before entering here.
		{
			throw new FormatException(WhitespaceMessage);
		}
	}

	/// <exception cref="FormatException">The password is too long or it has leading or trailing white spaces.</exception>
	/// <exception cref="System.Security.SecurityException">The password is not the one of the wallet.</exception>
	public static ExtKey GetMasterExtKey(KeyManager keyManager, string password)
	{
		password = Helpers.Guard.Correct(password);

		Guard(password);

		return keyManager.GetMasterExtKey(password);
	}
}
