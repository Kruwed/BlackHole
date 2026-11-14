
namespace WalletWasabi.Fluent.Models;

public abstract record WalletCreationOptions
{
	public record AddNewWallet(MultiShareBackup? Backup = null) : WalletCreationOptions
	{
		/// <summary>
		/// Starts a backup with the default settings. Its shares are generated once the user has chosen how many to make.
		/// </summary>
		public AddNewWallet WithNewBackup() =>
			this with
			{
				Backup = new MultiShareBackup(
					Settings: new MultiShareBackupSettings(),
					Shares: [],
					Password: "")
			};
	}

	public record RecoverWallet(
		MultiShareBackup? Backup = null,
		int? MinGapLimit = null,
		uint? BirthHeight = null) : WalletCreationOptions;
}
