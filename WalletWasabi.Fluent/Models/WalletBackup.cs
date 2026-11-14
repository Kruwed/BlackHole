using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Wallets.Slip39;

namespace WalletWasabi.Fluent.Models;

public record MultiShareBackupSettings(
	byte Threshold = WalletGenerator.DefaultShamirThreshold,
	byte Shares = WalletGenerator.DefaultShamirShares);

/// <summary>
/// The SLIP-39 backup of a wallet while it is being created or recovered.
/// </summary>
/// <param name="Password">Encrypts the wallet secret on this computer. It is not part of the backup.</param>
public record MultiShareBackup(
	MultiShareBackupSettings Settings,
	string Password,
	Share[] Shares,
	byte CurrentShare = 0,
	byte CurrentSharePage = 0);
