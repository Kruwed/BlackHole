using NBitcoin;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using WalletWasabi.Blockchain.BlockFilters;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.CoinJoinProfiles;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.Tests.UnitTests;

public class KeyManagerTests
{
	[Fact]
	public void CanCreateNew()
	{
		string password = "password";
		var manager = KeyManager.CreateNew(out Share[] shares, password, Network.Main);
		var manager2 = KeyManager.CreateNew(out Share[] shares2, "", Network.Main);
		var manager3 = KeyManager.CreateNew(out Share[] shares3, "P@ssw0rdé", Network.Main);

		// The backup is made of SLIP-39 shares of 20 words each.
		foreach (var backup in new[] { shares, shares2, shares3 })
		{
			Assert.Equal(WalletGenerator.DefaultShamirShares, backup.Length);
			Assert.All(backup, share => Assert.Equal(WalletGenerator.DefaultShamirThreshold, share.MemberThreshold));
			Assert.All(backup, share => Assert.Equal(20, share.ToMnemonic(WordList.Wordlist).Split(' ').Length));
		}

		// Every wallet has its own seed.
		Assert.NotEqual(manager.TaprootExtPubKey, manager2.TaprootExtPubKey);
		Assert.NotEqual(manager.TaprootExtPubKey, manager3.TaprootExtPubKey);
		Assert.NotEqual(manager2.TaprootExtPubKey, manager3.TaprootExtPubKey);

		Assert.NotNull(manager.ChainCode);
		Assert.NotNull(manager.EncryptedSecret);
		Assert.NotNull(manager.TaprootExtPubKey);
		Assert.NotNull(manager.SilentPaymentScanExtPubKey);
		Assert.NotNull(manager.SilentPaymentSpendExtPubKey);

		Assert.NotNull(manager2.ChainCode);
		Assert.NotNull(manager2.EncryptedSecret);
		Assert.NotNull(manager2.TaprootExtPubKey);
		Assert.NotNull(manager2.SilentPaymentScanExtPubKey);
		Assert.NotNull(manager2.SilentPaymentSpendExtPubKey);

		Assert.NotNull(manager3.ChainCode);
		Assert.NotNull(manager3.EncryptedSecret);
		Assert.NotNull(manager3.TaprootExtPubKey);
		Assert.NotNull(manager3.SilentPaymentScanExtPubKey);
		Assert.NotNull(manager3.SilentPaymentSpendExtPubKey);

		var sameManager = new KeyManager(manager.EncryptedSecret, manager.ChainCode, manager.MasterFingerprint, manager.TaprootExtPubKey,
			manager.SilentPaymentScanExtPubKey, manager.SilentPaymentSpendExtPubKey, null, new BlockchainState(Network.Main));

		Assert.Equal(manager.ChainCode, sameManager.ChainCode);
		Assert.Equal(manager.EncryptedSecret, sameManager.EncryptedSecret);
		Assert.Equal(manager.TaprootExtPubKey, sameManager.TaprootExtPubKey);
		Assert.Equal(manager.SilentPaymentScanExtPubKey, sameManager.SilentPaymentScanExtPubKey);
		Assert.Equal(manager.SilentPaymentSpendExtPubKey, sameManager.SilentPaymentSpendExtPubKey);
	}

	[Fact]
	public void CanRecover()
	{
		string password = "password";
		var manager = KeyManager.CreateNew(out Share[] shares, password, Network.Main);

		// Any two of the three shares recover the wallet.
		foreach (var backup in new[] { shares[..2], shares[1..], new[] { shares[2], shares[0] } })
		{
			var sameManager = KeyManager.Recover(backup, password, Network.Main);

			Assert.Equal(manager.ChainCode, sameManager.ChainCode);
			Assert.Equal(manager.EncryptedSecret, sameManager.EncryptedSecret);
			Assert.Equal(manager.TaprootExtPubKey, sameManager.TaprootExtPubKey);
			Assert.Equal(manager.SilentPaymentScanExtPubKey, sameManager.SilentPaymentScanExtPubKey);
			Assert.Equal(manager.SilentPaymentSpendExtPubKey, sameManager.SilentPaymentSpendExtPubKey);
		}

		// One share is not enough, and neither are more shares than the threshold.
		Assert.Throws<ArgumentException>(() => KeyManager.Recover(shares[..1], password, Network.Main));
		Assert.Throws<ArgumentException>(() => KeyManager.Recover(shares, password, Network.Main));

		// A wider gap limit only means more keys of the same account.
		var widerManager = KeyManager.Recover(shares[..2], password, Network.Main, filePath: null, minGapLimit: 55);
		Assert.Equal(manager.ChainCode, widerManager.ChainCode);
		Assert.Equal(manager.EncryptedSecret, widerManager.EncryptedSecret);
		Assert.Equal(manager.TaprootExtPubKey, widerManager.TaprootExtPubKey);
		Assert.Equal(manager.SilentPaymentScanExtPubKey, widerManager.SilentPaymentScanExtPubKey);
		Assert.Equal(manager.SilentPaymentSpendExtPubKey, widerManager.SilentPaymentSpendExtPubKey);

		var newKey = widerManager.GenerateNewKey(KeyState.Clean, true);
		Assert.Equal(newKey.Index, widerManager.MinGapLimit);
		Assert.Equal("86'/0'/0'/1/55", newKey.FullKeyPath.ToString());
	}

	[Fact]
	public void PasswordIsNotPartOfTheBackup()
	{
		var manager = KeyManager.CreateNew(out Share[] shares, "password", Network.Main);

		// The shares alone decide the keys: recovering with another password gives the very same wallet...
		foreach (var newPassword in new[] { "differentPassword", "" })
		{
			var recovered = KeyManager.Recover(shares[..2], newPassword, Network.Main);

			Assert.Equal(manager.MasterFingerprint, recovered.MasterFingerprint);
			Assert.Equal(manager.ChainCode, recovered.ChainCode);
			Assert.Equal(manager.TaprootExtPubKey, recovered.TaprootExtPubKey);
			Assert.Equal(manager.SilentPaymentScanExtPubKey, recovered.SilentPaymentScanExtPubKey);
			Assert.Equal(manager.SilentPaymentSpendExtPubKey, recovered.SilentPaymentSpendExtPubKey);
			Assert.Equal(manager.GetMasterExtKey("password").GetWif(Network.Main), recovered.GetMasterExtKey(newPassword).GetWif(Network.Main));

			// ... whose secret is encrypted with the new password only.
			Assert.NotEqual(manager.EncryptedSecret, recovered.EncryptedSecret);
			var freshlyLoaded = KeyManager.Recover(shares[..2], newPassword, Network.Main);
			Assert.Throws<SecurityException>(() => freshlyLoaded.GetMasterExtKey("password"));
		}
	}

	[Fact]
	public void CanHandleGap()
	{
		string password = "password";
		var manager = KeyManager.CreateNew(out _, password, Network.Main);

		var lastKey = manager.GetKeys(KeyState.Clean, isInternal: false).Last();
		manager.SetKeyState(KeyState.Used, lastKey);

		var newLastKey = manager.GetKeys(KeyState.Clean, isInternal: false).Last();
		Assert.Equal(manager.MinGapLimit, newLastKey.Index - lastKey.Index);
	}

	[Fact]
	public void CanGetReceiveKeyAfterReloadingAWalletBeyondItsGapLimit()
	{
		var filePath = "wallet-beyond-gap-limit.json";
		int minGapLimit;

		// Create keys beyond the gap limit, and save the wallet to disk.
		{
			var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
			minGapLimit = manager.MinGapLimit;

			for (var i = 0; i <= minGapLimit; i++)
			{
				// Each call leads to storing the new key in the file.
				manager.GetNextReceiveKey();
			}
		}

		// Deserialize the wallet. Then generate a new key beyond the gap limit, it should not crash.
		{
			var sameManager = KeyManager.FromFile(filePath);
			var key = sameManager.GetNextReceiveKey();
			Assert.Equal(minGapLimit + 1, key.Index);
		}

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void ReceiveKeysAreHandedOutOnlyOnce()
	{
		var filePath = "wallet-handed-out-keys.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		Assert.DoesNotContain(manager.GetKeys(), key => key.IsHandedOut);

		// Every request gives the next address that nobody was given yet, even when none of them was paid.
		var first = manager.GetNextReceiveKey();
		var second = manager.GetNextReceiveKey();
		var third = manager.GetNextReceiveKey();

		Assert.Equal([0, 1, 2], new[] { first, second, third }.Select(key => key.Index));
		Assert.All(new[] { first, second, third }, key =>
		{
			Assert.False(key.IsInternal);
			Assert.True(key.IsHandedOut);
			Assert.Equal(KeyState.Clean, key.KeyState);
		});

		// Change keys are never handed out.
		Assert.False(manager.GetNextChangeKey().IsHandedOut);

		// Peeking at the next key does not take it.
		var peeked = manager.GetNextReceiveKey(KeyPurpose.Loud, handOut: false);
		Assert.Equal(3, peeked.Index);
		Assert.False(peeked.IsHandedOut);
		Assert.Equal(peeked, manager.GetNextReceiveKey(KeyPurpose.Loud, handOut: false));

		// The wallet file remembers which addresses were given out.
		var sameManager = KeyManager.FromFile(filePath);
		var handedOut = sameManager.GetKeys(key => key.IsHandedOut);
		Assert.Equal(new[] { first, second, third }.Select(key => key.PubKey), handedOut.Select(key => key.PubKey));
		Assert.Equal(3, sameManager.GetNextReceiveKey().Index);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void OnlyUsePrivateFundsForPaymentsIsOffByDefault()
	{
		Assert.False(KeyManager.CreateNew(out _, "", Network.Main).OnlyUsePrivateFundsForPayments);
		Assert.True(new PrivacyProfiles.MaximizePrivacy().OnlyUsePrivateFundsForPayments);
	}

	[Fact]
	public void CanSerialize()
	{
		string password = "password";

		var filePath = Path.Combine(Common.GetWorkDir(), "Wallet.json");
		DeleteFileAndDirectoryIfExists(filePath);

		Assert.Throws<FileNotFoundException>(() => KeyManager.FromFile(filePath));

		var manager = KeyManager.CreateNew(out _, password, Network.Main, filePath);
		KeyManager.FromFile(filePath);

		manager.ToFile();

		manager.ToFile(); // assert it does not throw

		for (int i = 0; i < 1000; i++)
		{
			var isInternal = Random.Shared.Next(2) == 0;
			var keyState = (KeyState)Random.Shared.Next(3);
			manager.GenerateNewKey(keyState, isInternal);
		}

		manager.ToFile();

		Assert.True(File.Exists(filePath));

		var sameManager = KeyManager.FromFile(filePath);

		Assert.Equal(manager.ChainCode, sameManager.ChainCode);
		Assert.Equal(manager.EncryptedSecret, sameManager.EncryptedSecret);
		Assert.Equal(manager.TaprootExtPubKey, sameManager.TaprootExtPubKey);
		Assert.Equal(manager.SilentPaymentScanExtPubKey, sameManager.SilentPaymentScanExtPubKey);
		Assert.Equal(manager.SilentPaymentSpendExtPubKey, sameManager.SilentPaymentSpendExtPubKey);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void CanSerializeCoinjoinCosts()
	{
		var filePath = "wallet-coinjoin-costs.json";
		var transactionId = uint256.One;

		// Add costs and serialize.
		{
			var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
			manager.AddCoinjoinCosts(transactionId, new CoinjoinCosts(Money.Satoshis(690), Money.Satoshis(10), Money.Satoshis(50_000)));
		}

		// Check that the costs entry is serialized and present.
		{
			var sameManager = KeyManager.FromFile(filePath);

			// Present.
			var costs = sameManager.GetCoinjoinCosts(transactionId);
			Assert.NotNull(costs);
			Assert.Equal(Money.Satoshis(690), costs.MiningFee);
			Assert.Equal(Money.Satoshis(10), costs.WastedDust);
			Assert.Equal(Money.Satoshis(50_000), costs.PaymentsTotal);

			// Not present.
			costs = sameManager.GetCoinjoinCosts(uint256.Zero);
			Assert.Null(costs);
		}

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void RecordingTheCostsOfTheSameCoinjoinTwiceKeepsOneRecord()
	{
		// A round can be reported more than once - the costs belong to the transaction, so the last word wins.
		var manager = KeyManager.CreateNew(out _, "", Network.Main);
		var transactionId = uint256.Parse("0000000000000000000000000000000000000000000000000000000000000002");

		manager.AddCoinjoinCosts(transactionId, new CoinjoinCosts(Money.Satoshis(100), Money.Zero, Money.Zero));
		manager.AddCoinjoinCosts(transactionId, new CoinjoinCosts(Money.Satoshis(200), Money.Zero, Money.Zero));

		Assert.Equal(Money.Satoshis(200), manager.GetCoinjoinCosts(transactionId)?.MiningFee);
	}

	[Fact]
	public void WalletsWithoutRecordedCoinjoinCostsCanStillBeLoaded()
	{
		// Coinjoins made before the costs were recorded have no record, and the details screen falls back to a single figure.
		var filePath = "wallet-without-coinjoin-costs.json";
		KeyManager.CreateNew(out _, "", Network.Main, filePath).ToFile();

		var json = File.ReadAllText(filePath);
		Assert.Contains("\"CoinjoinCosts\"", json);
		File.WriteAllText(filePath, json.Replace("\"CoinjoinCosts\": [],", ""));

		Assert.Null(KeyManager.FromFile(filePath).GetCoinjoinCosts(uint256.One));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void CanSerializeHeightCorrectly()
	{
		var filePath = "wallet-serialization.json";
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.SetBestHeight(500_000);
		manager.ToFile();

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(new Height.ChainHeight(499_899), sameManager.GetBestHeight());

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void CanSerializeHeightBelowResyncMargin()
	{
		// The resync margin is subtracted when persisting the height. Networks whose first filter
		// is the genesis block can legitimately sit below that margin, so the subtraction must not
		// wrap around instead of being floored at zero.
		var filePath = "wallet-serialization-below-resync-margin.json";
		var manager = KeyManager.CreateNew(out _, "", Network.RegTest, filePath);
		manager.SetBestHeight(0);
		manager.ToFile();

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(new Height.ChainHeight(0), sameManager.GetBestHeight());

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void BestHeightNeverPrecedesFirstFilter()
	{
		// Filters are only available from the first checkpoint onwards, and the wallet asks for the
		// filter right after its best height, so a lower height makes that filter unfetchable.
		var firstFilterHeight = FilterCheckpoints.GetWasabiGenesisFilter(Network.Main).Header.Height;
		var manager = KeyManager.CreateNew(out _, "", Network.Main);

		manager.SetBestHeight(0);

		Assert.Equal(firstFilterHeight, manager.GetBestHeight());
	}

	[Fact]
	public void CanGenerateKeys()
	{
		string password = "password";
		var network = Network.Main;
		var manager = KeyManager.CreateNew(out _, password, network);

		var k1 = manager.GenerateNewKey(KeyState.Clean, true);
		Assert.True(k1.IsInternal);
		Assert.False(k1.IsHandedOut);

		for (int i = 0; i < 1000; i++)
		{
			var isInternal = Random.Shared.Next(2) == 0;
			var keyState = (KeyState)Random.Shared.Next(3);
			var generatedKey = manager.GenerateNewKey(keyState, isInternal);

			Assert.Equal(isInternal, generatedKey.IsInternal);
			Assert.Equal(keyState, generatedKey.KeyState);
			Assert.StartsWith(KeyManager.GetAccountKeyPath(network).ToString(), generatedKey.FullKeyPath.ToString());
		}
	}

	[Fact]
	public void CanGenerateRealKeys()
	{
		string password = "password";
		var network = Network.Main;
		var manager = KeyManager.CreateNew(out _, password, network);

		var taprootKey = manager.GetNextReceiveKey();
		var silentPaymentScanKey = manager.GetNextReceiveKey(KeyPurpose.Scan, handOut: false);
		var silentPaymentSpendKey = manager.GetNextReceiveKey(KeyPurpose.Spend, handOut: false);
		Assert.Equal("86'/0'/0'/0/0", taprootKey.FullKeyPath.ToString());
		Assert.Equal("352'/0'/0'/1'/0", silentPaymentScanKey.FullKeyPath.ToString());
		Assert.Equal("352'/0'/0'/0'/0", silentPaymentSpendKey.FullKeyPath.ToString());
	}

	[Fact]
	public void EveryAddressIsATaprootAddress()
	{
		var keyManager = KeyManager.CreateNew(out _, "", Network.Main);

		var keysForChange = Enumerable
			.Range(0, 10)
			.Select(_ =>
			{
				var key = keyManager.GetNextChangeKey();
				keyManager.SetKeyState(KeyState.Used, key);
				return key;
			})
			.ToList();

		var keysForReceive = Enumerable
			.Range(0, 10)
			.Select(_ => keyManager.GetNextReceiveKey())
			.ToList();

		Assert.All(keysForChange, key => Assert.StartsWith("86'/0'/0'/1/", key.FullKeyPath.ToString()));
		Assert.All(keysForReceive, key => Assert.StartsWith("86'/0'/0'/0/", key.FullKeyPath.ToString()));
		Assert.Equal(10, keysForChange.Distinct().Count());
		Assert.Equal(10, keysForReceive.Distinct().Count());

		foreach (var key in keysForChange.Concat(keysForReceive))
		{
			var address = key.GetAddress(Network.Main);
			Assert.IsType<TaprootAddress>(address);
			Assert.StartsWith("bc1p", address.ToString());
			Assert.Equal(address.ScriptPubKey, key.GetAssumedScriptPubKey());
			Assert.Equal(address.ScriptPubKey, key.P2Taproot);

			Assert.True(keyManager.TryGetKeyForScriptPubKey(address.ScriptPubKey, out var foundKey));
			Assert.Equal(key, foundKey);

			// The SegWit v0 address of the very same key is not an address of the wallet.
			Assert.False(keyManager.TryGetKeyForScriptPubKey(key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit), out _));
			Assert.False(key.ContainsScript(key.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit)));
		}

		// No key of the wallet is outside the Taproot account, apart from the silent payment keys.
		Assert.All(
			keyManager.GetKeys(),
			key => Assert.Matches(@"^(86'/0'/0'/[01]/\d+|352'/0'/0'/[01]'/\d+)$", key.FullKeyPath.ToString()));
	}

	[Fact]
	public void WalletFilesWithKeysOfOtherAccountsAreRefused()
	{
		var filePath = "wallet-with-segwit-key.json";
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);

		// Handing out an address generates the keys of the account and writes them into the file.
		manager.GetNextReceiveKey();

		// A wallet file whose keys all belong to the Taproot account loads.
		Assert.Equal(manager.TaprootExtPubKey, KeyManager.FromFile(filePath).TaprootExtPubKey);

		// Rewriting the file as it is does no harm...
		var json = JsonNode.Parse(File.ReadAllText(filePath))!;
		File.WriteAllText(filePath, json.ToJsonString());
		Assert.Equal(manager.TaprootExtPubKey, KeyManager.FromFile(filePath).TaprootExtPubKey);

		// ... but turning one of its keys into a key of a SegWit v0 (BIP84) account does, as a file of another wallet software has them.
		var firstKey = json["HdPubKeys"]![0]!;
		Assert.StartsWith("86'/0'/0'/", firstKey["FullKeyPath"]!.GetValue<string>());
		firstKey["FullKeyPath"] = "84'/0'/0'/0/0";
		File.WriteAllText(filePath, json.ToJsonString());

		Assert.ThrowsAny<Exception>(() => KeyManager.FromFile(filePath));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	private static void DeleteFileAndDirectoryIfExists(string filePath)
	{
		var dir = Path.GetDirectoryName(filePath);

		if (File.Exists(filePath))
		{
			File.Delete(filePath);
		}

		if (dir is not null && Directory.Exists(dir))
		{
			if (Directory.GetFiles(dir).Length == 0)
			{
				Directory.Delete(dir);
			}
		}
	}
}
