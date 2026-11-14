using System.Security;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Userfacing;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Userfacing;

public class PasswordTests
{
	[Fact]
	public void FormattingTest()
	{
		string password = "    w¾3AÍ-dCdï×¾M\\Øò¹ãÔÕýÈÝÁÐ9oEp¨}r:SR¦·ßNó±¥*W!¢ê#ikÇå<ðtÇf·a\\]§,à±H7«®È4nèNmæo4.qØ-¾ûda¯\n";

		// Wallets are created with the corrected password.
		var keyManager = KeyManager.CreateNew(out _, Guard.Correct(password), Network.Main);

		// Password will be trimmed inside.
		PasswordHelper.GetMasterExtKey(keyManager, password);

		// This should not throw format exception but pw is not correct.
		Assert.Throws<SecurityException>(() => PasswordHelper.GetMasterExtKey(keyManager, RandomString.AlphaNumeric(PasswordHelper.MaxPasswordLength)));

		// Password should be formatted, before entering here.
		Assert.Throws<FormatException>(() => PasswordHelper.GetMasterExtKey(keyManager, RandomString.AlphaNumeric(PasswordHelper.MaxPasswordLength + 1)));

		// Too long password with extra spaces.
		var badPassword = $"   {RandomString.AlphaNumeric(PasswordHelper.MaxPasswordLength + 1)}   ";

		// Password should be formatted, before entering here.
		Assert.Throws<FormatException>(() => PasswordHelper.GetMasterExtKey(keyManager, badPassword));

		Assert.True(PasswordHelper.IsTrimmable(badPassword, out badPassword));

		// Still too long.
		Assert.Throws<FormatException>(() => PasswordHelper.GetMasterExtKey(keyManager, badPassword!));

		Assert.True(PasswordHelper.IsTooLong(badPassword, out badPassword));

		// This should not throw format exception but pw is not correct.
		Assert.Throws<SecurityException>(() => PasswordHelper.GetMasterExtKey(keyManager, badPassword!));
	}

	[Fact]
	public void GuardTest()
	{
		PasswordHelper.Guard("");
		PasswordHelper.Guard("password");
		PasswordHelper.Guard(RandomString.AlphaNumeric(PasswordHelper.MaxPasswordLength));

		Assert.Throws<FormatException>(() => PasswordHelper.Guard(" password"));
		Assert.Throws<FormatException>(() => PasswordHelper.Guard("password\n"));
		Assert.Throws<FormatException>(() => PasswordHelper.Guard(RandomString.AlphaNumeric(PasswordHelper.MaxPasswordLength + 1)));
	}

	[Fact]
	public void OnlyTheExactPasswordOpensTheWallet()
	{
		var password = "§'\" + !%/= ()ÖÜÓ'";
		var keyManager = KeyManager.CreateNew(out _, password, Network.Main);

		Assert.False(PasswordHelper.TryPassword(keyManager, "falsePassword"));
		Assert.False(PasswordHelper.TryPassword(keyManager, ""));

		// A password that only resembles the right one is wrong as well.
		Assert.False(PasswordHelper.TryPassword(keyManager, password[..^1]));
		Assert.False(PasswordHelper.TryPassword(keyManager, $"{password[..^1]}\ufffd"));
		Assert.False(PasswordHelper.TryPassword(keyManager, password.ToLowerInvariant()));

		Assert.True(PasswordHelper.TryPassword(keyManager, password));

		// White spaces around the password are not part of it.
		Assert.True(PasswordHelper.TryPassword(keyManager, $"  {password}\n"));
	}

	[Fact]
	public void EmptyPasswordTest()
	{
		var keyManager = KeyManager.CreateNew(out _, "", Network.Main);

		Assert.False(PasswordHelper.TryPassword(keyManager, "password"));

		Assert.True(PasswordHelper.TryPassword(keyManager, ""));
		Assert.True(PasswordHelper.TryPassword(keyManager, "   "));
		Assert.True(PasswordHelper.TryPassword(keyManager, null!));
	}
}
