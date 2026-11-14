using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Wallets.Slip39;

namespace WalletWasabi.Tests.Helpers;

public static class ServiceFactory
{
	public static TransactionFactory CreateTransactionFactory(
		(string Label, int KeyIndex, decimal Amount, bool Confirmed, int AnonymitySet)[] coins)
	{
		string password = "foo";
		KeyManager keyManager = CreateKeyManager(password);
		SmartCoin[] sCoins = CreateCoins(keyManager, coins);
		var coinsView = new CoinsView(sCoins);
		return new TransactionFactory(Network.Main, keyManager, coinsView, password);
	}

	public static SmartCoin[] CreateCoins(
		KeyManager keyManager,
		(string Label, int KeyIndex, decimal Amount, bool Confirmed, int AnonymitySet)[] coins)
	{
		var generated = keyManager.GetKeys().Length;
		var toGenerate = coins.Length - generated;
		for (int i = 0; i < toGenerate; i++)
		{
			keyManager.GenerateNewKey("", KeyState.Clean, false);
		}

		var keys = keyManager.GetKeys().Take(coins.Length).ToArray();
		var sCoins = new List<SmartCoin>(coins.Length);

		foreach (var c in coins)
		{
			var k = keys[c.KeyIndex];
			k.SetLabel(c.Label);

			var sCoin = BitcoinFactory.CreateSmartCoin(keys[c.KeyIndex], c.Amount, c.Confirmed, c.AnonymitySet);
			sCoin.SetAnonymitySet(c.AnonymitySet);

			sCoins.Add(sCoin);
		}

		foreach (var coin in sCoins)
		{
			foreach (var sameLabelCoin in sCoins.Where(c => !c.HdPubKey.Labels.IsEmpty && c.HdPubKey.Labels == coin.HdPubKey.Labels))
			{
				sameLabelCoin.HdPubKey.Cluster = coin.HdPubKey.Cluster;
			}
		}

		var uniqueCoins = sCoins.Distinct().Count();
		if (uniqueCoins != sCoins.Count)
		{
			throw new InvalidOperationException($"Coin clones have been detected. Number of all coins:{sCoins.Count}, unique coins:{uniqueCoins}.");
		}

		return sCoins.ToArray();
	}

	/// <summary>Seed for the tests that need the same keys in every run.</summary>
	public static byte[] FixedSeed => Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

	/// <summary>A SLIP-39 backup of the given seed that consists of a single share.</summary>
	public static Share[] CreateBackup(byte[] seed) =>
		Shamir.Generate(threshold: 1, shares: 1, seed);

	/// <param name="seed">Seed of the wallet. A random one is used if it is not given.</param>
	public static KeyManager CreateKeyManager(string password = "blahblahblah", byte[]? seed = null)
	{
		ExtKey extKey = ExtKey.CreateFromSeed(seed ?? WalletGenerator.GenerateShamirEntropy());
		var encryptedSecret = extKey.PrivateKey.GetEncryptedBitcoinSecret(password, Network.Main);

		HDFingerprint masterFingerprint = extKey.Neuter().PubKey.GetHDFingerPrint();
		BlockchainState blockchainState = new(Network.Main);
		KeyPath taprootAccountKeyPath = KeyManager.GetAccountKeyPath(Network.Main);
		ExtPubKey taprootExtPubKey = extKey.Derive(taprootAccountKeyPath).Neuter();

		return new KeyManager(encryptedSecret, extKey.ChainCode, masterFingerprint, taprootExtPubKey, null, null, 21, blockchainState, null, taprootAccountKeyPath);
	}
}
