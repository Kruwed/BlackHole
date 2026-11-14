using WalletWasabi.Userfacing;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class WalletInfoModel
{
	public WalletInfoModel(Wallet wallet)
	{
		var network = wallet.Network;
		var keyManager = wallet.KeyManager;
		var secret = PasswordHelper.GetMasterExtKey(keyManager, wallet.Password);

		ExtendedMasterPrivateKey = secret.GetWif(network).ToWif();
		ExtendedAccountPrivateKey = secret.Derive(keyManager.TaprootAccountKeyPath).GetWif(network).ToWif();

		OutputDescriptor = keyManager.GetOutputDescriptor(network);
		ExtendedAccountPublicKey = keyManager.TaprootExtPubKey.ToString(network);
		AccountKeyPath = $"m/{keyManager.TaprootAccountKeyPath}";
		MasterKeyFingerprint = keyManager.MasterFingerprint?.ToString();
	}

	public string ExtendedAccountPublicKey { get; }

	public string AccountKeyPath { get; }

	public string? MasterKeyFingerprint { get; }

	public string ExtendedMasterPrivateKey { get; }

	public string ExtendedAccountPrivateKey { get; }

	/// <summary>Public descriptor of the wallet's addresses, e.g. to watch them with other software.</summary>
	public string OutputDescriptor { get; }
}
