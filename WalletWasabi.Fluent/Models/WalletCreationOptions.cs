
namespace WalletWasabi.Fluent.Models;

public abstract record WalletCreationOptions(string? WalletName = null)
{
	public record AddNewWallet(string? WalletName = null, MultiShareBackup? Backup = null)
		: WalletCreationOptions(WalletName)
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
		string? WalletName = null,
		MultiShareBackup? Backup = null,
		int? MinGapLimit = null,
		uint? BirthHeight = null) : WalletCreationOptions(WalletName);
}
