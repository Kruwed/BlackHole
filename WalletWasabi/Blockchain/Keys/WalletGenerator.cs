using NBitcoin;
using System.IO;
using System.Linq;
using WalletWasabi.Models;
using WalletWasabi.Userfacing;
using WalletWasabi.Wallets.Slip39;

namespace WalletWasabi.Blockchain.Keys;

public class WalletGenerator
{
	public const byte DefaultShamirShares = 3;
	public const byte DefaultShamirThreshold = 2;
	public const int MinShamirShares = 1;
	public const int MaxShamirShares = 16;
	public const int MinShamirThreshold = 1;
	public const int MaxShamirThreshold = 16;

	public WalletGenerator(string walletsDir, Network network)
	{
		WalletsDir = walletsDir;
		Network = network;
	}

	public string WalletsDir { get; private set; }
	public Network Network { get; private set; }
	public uint TipHeight { get; set; }

	/// <summary>
	/// Creates the wallet that the given SLIP-39 shares back up.
	/// </summary>
	/// <param name="shares">As many shares as their threshold requires.</param>
	/// <param name="password">Encrypts the wallet secret in the wallet file. It is not part of the backup.</param>
	public KeyManager GenerateWallet(string walletName, string password, Share[] shares)
	{
		string walletFilePath = GetWalletFilePath(walletName, WalletsDir);

		// Here we are not letting anything that will be autocorrected later. The wallet must open with exactly the entered password.
		PasswordHelper.Guard(password);

		var km = KeyManager.CreateNew(shares, password, Network);

		km.SetBestHeight(TipHeight);
		km.SetFilePath(walletFilePath);
		return km;
	}

	public static string GetWalletFilePath(string walletName, string walletsDir)
	{
		if (!ValidateWalletName(walletName))
		{
			throw new ArgumentException("Invalid wallet name.");
		}

		string walletFilePath = Path.Combine(walletsDir, $"{walletName}.json");
		if (File.Exists(walletFilePath))
		{
			throw new ArgumentException("Wallet name is already taken.");
		}

		return walletFilePath;
	}

	public static bool ValidateWalletName(string walletName)
	{
		if (string.IsNullOrWhiteSpace(walletName))
		{
			return false;
		}
		var invalidChars = Path.GetInvalidFileNameChars();
		return !walletName.Any(c => invalidChars.Contains(c)) && !walletName.EndsWith('.');
	}

	public static byte[] GenerateShamirEntropy()
	{
		return RandomUtils.GetBytes(128 / 8);
	}

	/// <summary>
	/// Splits fresh entropy into the SLIP-39 shares of a new wallet.
	/// </summary>
	public static Share[] GenerateShares(byte threshold = DefaultShamirThreshold, byte shares = DefaultShamirShares) =>
		Shamir.Generate(threshold, shares, GenerateShamirEntropy());
}
