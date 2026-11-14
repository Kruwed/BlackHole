using NBitcoin;
using System.IO;

namespace WalletWasabi.Wallets;

public class WalletDirectories
{
	public const string WalletsDirName = "Wallets";

	/// <summary>There is a single wallet, so its file has a fixed name.</summary>
	public const string WalletFileName = "Wallet.json";

	public WalletDirectories(Network network, string workDir)
	{
		Network = network;
		WalletsDir = network == Network.Main
			? Path.Combine(workDir, WalletsDirName)
			: Path.Combine(workDir, WalletsDirName, network.ToString());

		Directory.CreateDirectory(WalletsDir);

		WalletFilePath = Path.Combine(WalletsDir, WalletFileName);
	}

	public string WalletsDir { get; }

	/// <summary>Path of the file of the wallet. The file exists only after the wallet was created or recovered.</summary>
	public string WalletFilePath { get; }

	public Network Network { get; }
}
