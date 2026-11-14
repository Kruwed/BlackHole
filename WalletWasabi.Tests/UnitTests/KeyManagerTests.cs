using NBitcoin;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
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

		// Unless it is split, the backup is a single SLIP-39 share: 20 words that recover the wallet on their own.
		foreach (var backup in new[] { shares, shares2, shares3 })
		{
			var share = Assert.Single(backup);
			Assert.Equal(1, share.MemberThreshold);
			Assert.Equal(20, share.ToMnemonic(WordList.Wordlist).Split(' ').Length);
		}

		Assert.Equal(1, WalletGenerator.DefaultShamirShares);
		Assert.Equal(1, WalletGenerator.DefaultShamirThreshold);

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

		// The backup is one share, and its 20 words are all it takes: written down and typed in again, they give the same wallet.
		var words = Assert.Single(shares).ToMnemonic(WordList.Wordlist);
		Assert.Equal(20, words.Split(' ').Length);

		var sameManager = KeyManager.Recover([Share.FromMnemonic(words)], password, Network.Main);

		Assert.Equal(manager.ChainCode, sameManager.ChainCode);
		Assert.Equal(manager.EncryptedSecret, sameManager.EncryptedSecret);
		Assert.Equal(manager.TaprootExtPubKey, sameManager.TaprootExtPubKey);
		Assert.Equal(manager.SilentPaymentScanExtPubKey, sameManager.SilentPaymentScanExtPubKey);
		Assert.Equal(manager.SilentPaymentSpendExtPubKey, sameManager.SilentPaymentSpendExtPubKey);

		// The share of another wallet gives another wallet.
		var otherManager = KeyManager.Recover(WalletGenerator.GenerateShares(), password, Network.Main);
		Assert.NotEqual(manager.TaprootExtPubKey, otherManager.TaprootExtPubKey);

		// A wider gap limit only means more keys of the same account.
		var widerManager = KeyManager.Recover(shares, password, Network.Main, filePath: null, minGapLimit: 55);
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
	public void CanRecoverFromSplitBackup()
	{
		string password = "password";

		// Whoever wants to can split the backup: here into three shares, any two of which recover the wallet.
		var shares = WalletGenerator.GenerateShares(threshold: 2, shares: 3);
		Assert.Equal(3, shares.Length);
		Assert.All(shares, share => Assert.Equal(2, share.MemberThreshold));
		Assert.All(shares, share => Assert.Equal(20, share.ToMnemonic(WordList.Wordlist).Split(' ').Length));

		var manager = KeyManager.CreateNew(shares[..2], password, Network.Main);

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

		// A threshold of one goes with a single share only.
		Assert.Throws<ArgumentException>(() => WalletGenerator.GenerateShares(threshold: 1, shares: 2));
	}

	[Fact]
	public void PasswordIsNotPartOfTheBackup()
	{
		var manager = KeyManager.CreateNew(out Share[] shares, "password", Network.Main);

		// The shares alone decide the keys: recovering with another password gives the very same wallet...
		foreach (var newPassword in new[] { "differentPassword", "" })
		{
			var recovered = KeyManager.Recover(shares, newPassword, Network.Main);

			Assert.Equal(manager.MasterFingerprint, recovered.MasterFingerprint);
			Assert.Equal(manager.ChainCode, recovered.ChainCode);
			Assert.Equal(manager.TaprootExtPubKey, recovered.TaprootExtPubKey);
			Assert.Equal(manager.SilentPaymentScanExtPubKey, recovered.SilentPaymentScanExtPubKey);
			Assert.Equal(manager.SilentPaymentSpendExtPubKey, recovered.SilentPaymentSpendExtPubKey);
			Assert.Equal(manager.GetMasterExtKey("password").GetWif(Network.Main), recovered.GetMasterExtKey(newPassword).GetWif(Network.Main));

			// ... whose secret is encrypted with the new password only.
			Assert.NotEqual(manager.EncryptedSecret, recovered.EncryptedSecret);
			var freshlyLoaded = KeyManager.Recover(shares, newPassword, Network.Main);
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
	public void NewWalletLearnsWhereItStartsFromTheTipOfTheChain()
	{
		var filePath = "wallet-start-height.json";
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);

		// It was created before the node told the height of the chain.
		Assert.Null(manager.GetBestHeight());
		Assert.Null(manager.GetBirthHeight());
		Assert.Null(KeyManager.FromFile(filePath).GetBestHeight());

		// Nothing concerns a new wallet up to the tip of the chain.
		manager.StartAt(900_000);
		Assert.Equal(new Height.ChainHeight(900_000), manager.GetBestHeight());
		Assert.Equal(new Height.ChainHeight(900_000), manager.GetBirthHeight());

		// That is told once.
		manager.StartAt(950_000);
		Assert.Equal(new Height.ChainHeight(900_000), manager.GetBestHeight());
		Assert.Equal(new Height.ChainHeight(900_000), manager.GetBirthHeight());

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(new Height.ChainHeight(900_000), sameManager.GetBestHeight());
		Assert.Equal(new Height.ChainHeight(900_000), sameManager.GetBirthHeight());
		Assert.Empty(sameManager.GetProcessedBlocks().BlockHashes);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void RecoveredWalletStartsAtItsBirthHeight()
	{
		var shares = WalletGenerator.GenerateShares().Take(WalletGenerator.DefaultShamirThreshold).ToArray();

		// Wallets are Taproot only, so by default the block that Taproot activated at is the first one to look into.
		var manager = KeyManager.Recover(shares, "", Network.Main);
		Assert.Equal(new Height.ChainHeight(709_632), manager.GetBirthHeight());
		Assert.Equal(new Height.ChainHeight(709_631), manager.GetBestHeight());

		manager = KeyManager.Recover(shares, "", Network.Main, birthHeight: new Height.ChainHeight(850_000));
		Assert.Equal(new Height.ChainHeight(850_000), manager.GetBirthHeight());
		Assert.Equal(new Height.ChainHeight(849_999), manager.GetBestHeight());

		// The other networks are looked through from their first block. Nothing concerns a wallet in the genesis block.
		manager = KeyManager.Recover(shares, "", Network.RegTest);
		Assert.Equal(new Height.ChainHeight(0), manager.GetBirthHeight());
		Assert.Equal(new Height.ChainHeight(0), manager.GetBestHeight());
	}

	[Fact]
	public void RemembersTheLastProcessedBlocks()
	{
		static uint256 BlockHash(uint height) => new(height);

		var filePath = "wallet-processed-blocks.json";
		var manager = KeyManager.CreateNew(out _, "", Network.RegTest, filePath);
		manager.StartAt(10);

		for (uint height = 11; height <= 15; height++)
		{
			manager.SetBestBlock(height, BlockHash(height), toFile: false);
		}

		var (bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(15), bestHeight);
		Assert.Equal(new[] { BlockHash(11), BlockHash(12), BlockHash(13), BlockHash(14), BlockHash(15) }, blockHashes);

		// The height is exactly what gets saved, and only when asked for.
		Assert.Equal(new Height.ChainHeight(10), KeyManager.FromFile(filePath).GetBestHeight());
		manager.ToFile();

		var (savedHeight, savedBlockHashes) = KeyManager.FromFile(filePath).GetProcessedBlocks();
		Assert.Equal(bestHeight, savedHeight);
		Assert.Equal(blockHashes, savedBlockHashes);

		// Only the newest blocks are remembered.
		for (uint height = 16; height <= 200; height++)
		{
			manager.SetBestBlock(height, BlockHash(height), toFile: false);
		}

		(bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(200), bestHeight);
		Assert.Equal(BlockchainState.MaxRememberedBlocks, blockHashes.Length);
		Assert.Equal(BlockHash(100), blockHashes[0]);
		Assert.Equal(BlockHash(200), blockHashes[^1]);

		// Going back forgets the blocks above, e.g. those that a reorganization of the chain dropped.
		manager.SetMaxBestHeight(197);

		(bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(197), bestHeight);
		Assert.Equal(BlockchainState.MaxRememberedBlocks - 3, blockHashes.Length);
		Assert.Equal(BlockHash(197), blockHashes[^1]);
		Assert.Equal(new Height.ChainHeight(197), KeyManager.FromFile(filePath).GetBestHeight());

		// It never raises the height.
		manager.SetMaxBestHeight(500);
		Assert.Equal(new Height.ChainHeight(197), manager.GetBestHeight());

		// Blocks that do not follow each other tell nothing about what is between them.
		manager.SetBestBlock(300, BlockHash(300));
		(bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(300), bestHeight);
		Assert.Equal(new[] { BlockHash(300) }, blockHashes);

		// Going back further than what is remembered leaves nothing to compare.
		manager.SetMaxBestHeight(250);
		(bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(250), bestHeight);
		Assert.Empty(blockHashes);

		// A resync starts with the given block.
		manager.SetResyncParameters(50, manager.MinGapLimit);
		(bestHeight, blockHashes) = manager.GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(49), bestHeight);
		Assert.Empty(blockHashes);

		manager.SetResyncParameters(0, manager.MinGapLimit);
		Assert.Equal(new Height.ChainHeight(0), manager.GetBestHeight());

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void LoadsTheHeightOfWalletFilesWithoutBlockHashes()
	{
		// That is how the wallet file tells the height when no block was processed since it was set.
		var filePath = "wallet-height-only.json";
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.StartAt(800_000);
		manager.SetBestBlock(800_001, uint256.One);

		var json = JsonNode.Parse(File.ReadAllText(filePath))!;
		var blockchainState = json["BlockchainState"]!.AsObject();
		Assert.Equal("800001", blockchainState["Height"]!.GetValue<string>());
		Assert.Single(blockchainState["BlockHashes"]!.AsArray());

		blockchainState.Remove("BlockHashes");
		File.WriteAllText(filePath, json.ToJsonString());

		var (bestHeight, blockHashes) = KeyManager.FromFile(filePath).GetProcessedBlocks();
		Assert.Equal(new Height.ChainHeight(800_001), bestHeight);
		Assert.Empty(blockHashes);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void UnreadableHeightIsNotTakenForANewWallet()
	{
		// A wallet without a height starts at the tip of the chain. A wallet file with a damaged height must not end up there:
		// it would never look at the blocks that have its transactions.
		var filePath = "wallet-damaged-height.json";
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.StartAt(800_000);

		var json = JsonNode.Parse(File.ReadAllText(filePath))!;
		json["BlockchainState"]!["Height"] = "eight hundred thousand";
		File.WriteAllText(filePath, json.ToJsonString());

		Assert.ThrowsAny<Exception>(() => KeyManager.FromFile(filePath));

		DeleteFileAndDirectoryIfExists(filePath);
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
