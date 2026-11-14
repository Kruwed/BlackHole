using WalletWasabi.Fluent.Models;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// Tests for <see cref="WalletCreationOptions"/>: what the user is offered when a wallet is created.
/// </summary>
public class WalletCreationOptionsTests
{
	[Fact]
	public void NewBackupIsOneShareUnlessTheUserSplitsIt()
	{
		var backup = new WalletCreationOptions.AddNewWallet().WithNewBackup().Backup;

		Assert.NotNull(backup);

		// One share with a threshold of one: a single list of 20 words. The user can raise both numbers.
		Assert.Equal(1, backup.Settings.Shares);
		Assert.Equal(1, backup.Settings.Threshold);

		// The shares are generated once the numbers are final.
		Assert.Empty(backup.Shares);
	}
}
