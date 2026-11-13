using System.IO;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Wallets;

namespace WalletWasabi.Helpers;

public static class ImportWalletHelper
{
	private const string WalletExistsErrorMessage = "Wallet with the same fingerprint already exists!";

	public static Task<KeyManager> ImportWalletAsync(WalletManager walletManager, string walletName, string filePath)
	{
		var walletFullPath = walletManager.WalletDirectories.GetWalletFilePaths(walletName);

		var km = KeyManager.FromFile(filePath);

		if (walletManager.WalletExists(km.MasterFingerprint))
		{
			throw new InvalidOperationException(WalletExistsErrorMessage);
		}

		km.SetFilePath(walletFullPath);
		km.SetBestHeight(0);
		return Task.FromResult(km);
	}
}
