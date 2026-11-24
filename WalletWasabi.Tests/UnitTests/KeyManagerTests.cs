using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text.Json.Nodes;
using WalletWasabi.Blockchain.Keys;
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
		var widerManager = KeyManager.Recover(shares, password, Network.Main, filePath: null, minGapLimit: 550);
		Assert.Equal(manager.ChainCode, widerManager.ChainCode);
		Assert.Equal(manager.EncryptedSecret, widerManager.EncryptedSecret);
		Assert.Equal(manager.TaprootExtPubKey, widerManager.TaprootExtPubKey);
		Assert.Equal(manager.SilentPaymentScanExtPubKey, widerManager.SilentPaymentScanExtPubKey);
		Assert.Equal(manager.SilentPaymentSpendExtPubKey, widerManager.SilentPaymentSpendExtPubKey);

		var newKey = widerManager.GenerateNewKey(KeyState.Clean, true);
		Assert.Equal(newKey.Index, widerManager.MinGapLimit);
		Assert.Equal("86'/0'/0'/1/550", newKey.FullKeyPath.ToString());
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
	public void WalletsWatch210UnusedAddressesAhead()
	{
		// The receive screen gives out a new address every time it is opened, so many of them can stay unused in a row.
		Assert.Equal(210, KeyManager.AbsoluteMinGapLimit);

		var manager = KeyManager.CreateNew(out Share[] shares, "", Network.Main);
		Assert.Equal(210, manager.MinGapLimit);
		Assert.Equal(Enumerable.Range(0, 210), manager.GetKeys(KeyState.Clean, isInternal: false).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(0, 210), manager.GetKeys(KeyState.Clean, isInternal: true).Select(key => key.Index));

		// A recovered wallet looks as far ahead, also when it is asked for less.
		Assert.Equal(210, KeyManager.Recover(shares, "", Network.Main).MinGapLimit);

		var narrowManager = KeyManager.Recover(shares, "", Network.Main, minGapLimit: 21);
		Assert.Equal(210, narrowManager.MinGapLimit);
		Assert.Equal(210, narrowManager.GetKeys(isInternal: false).Length);

		// A payment to the last of 210 unused addresses in a row is noticed, and moves the range on.
		var lastKey = manager.GetKeys(isInternal: false).Last();
		Assert.Equal(209, lastKey.Index);
		Assert.True(manager.TryGetKeyForScriptPubKey(lastKey.P2Taproot, out _));

		manager.SetKeyState(KeyState.Used, lastKey);
		Assert.Equal(419, manager.GetKeys(isInternal: false).Last().Index);
	}

	/// <summary>
	/// The wallet file tells whether the wallet was recovered from its backup or created. A file from before it did
	/// is the file of a wallet that was created.
	/// </summary>
	[Fact]
	public void WalletFileTellsWhetherTheWalletWasRecovered()
	{
		var createdFilePath = "wallet-that-was-created.json";
		var recoveredFilePath = "wallet-that-was-recovered.json";
		DeleteFileAndDirectoryIfExists(createdFilePath);
		DeleteFileAndDirectoryIfExists(recoveredFilePath);

		var created = KeyManager.CreateNew(out Share[] shares, "", Network.Main, createdFilePath);
		created.ToFile();
		var recovered = KeyManager.Recover(shares, "", Network.Main, recoveredFilePath);

		Assert.False(created.IsRecovered);
		Assert.True(recovered.IsRecovered);

		// It is the same wallet either way, and the file keeps how it came about.
		Assert.Equal(created.TaprootExtPubKey, recovered.TaprootExtPubKey);
		Assert.False(KeyManager.FromFile(createdFilePath).IsRecovered);
		Assert.True(KeyManager.FromFile(recoveredFilePath).IsRecovered);

		// A file that does not say.
		var json = JsonNode.Parse(File.ReadAllText(recoveredFilePath))!.AsObject();
		Assert.True(json.Remove("IsRecovered"));
		File.WriteAllText(recoveredFilePath, json.ToJsonString());

		Assert.False(KeyManager.FromFile(recoveredFilePath).IsRecovered);

		DeleteFileAndDirectoryIfExists(createdFilePath);
		DeleteFileAndDirectoryIfExists(recoveredFilePath);
	}

	[Fact]
	public void WalletFilesWithANarrowerGapLimitAreWidened()
	{
		var filePath = "wallet-with-gap-limit-21.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.GetKeys();
		var usedKey = manager.GetNextReceiveKey();
		var waitingKey = manager.GetNextReceiveKey();
		manager.SetKeyState(KeyState.Used, usedKey);
		manager.ToFile();

		// Make the file one that was written while the gap limit was 21: on both chains it ends with 21 unused keys.
		var json = JsonNode.Parse(File.ReadAllText(filePath))!;
		json["MinGapLimit"] = 21;

		var keys = json["HdPubKeys"]!.AsArray();
		foreach (var key in keys.ToArray())
		{
			var keyPath = key!["FullKeyPath"]!.GetValue<string>();
			var index = int.Parse(keyPath[(keyPath.LastIndexOf('/') + 1)..]);
			var lastIndex = keyPath.StartsWith("86'/0'/0'/1/", StringComparison.Ordinal) ? 20 : 21;

			if (index > lastIndex)
			{
				keys.Remove(key);
			}
		}

		Assert.Equal(22 + 21, keys.Count);
		File.WriteAllText(filePath, json.ToJsonString());

		// Loading it widens the range to 210 unused keys on both chains ...
		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(210, sameManager.MinGapLimit);
		Assert.Equal(Enumerable.Range(0, 211), sameManager.GetKeys(isInternal: false).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(0, 210), sameManager.GetKeys(isInternal: true).Select(key => key.Index));

		// ... and keeps what the file knew about its keys.
		Assert.Equal(usedKey.PubKey, Assert.Single(sameManager.GetKeys(KeyState.Used)).PubKey);
		Assert.Equal(new[] { usedKey.PubKey, waitingKey.PubKey }, sameManager.GetKeys(key => key.IsHandedOut).Select(key => key.PubKey));
		Assert.Equal(2, sameManager.GetNextReceiveKey().Index);

		// The file is written with the wider range the next time the wallet is saved.
		json = JsonNode.Parse(File.ReadAllText(filePath))!;
		Assert.Equal(210, json["MinGapLimit"]!.GetValue<int>());
		Assert.Equal(211 + 210, json["HdPubKeys"]!.AsArray().Count);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void TheGapLimitDoesNotMultiplyTheSilentPaymentKeys()
	{
		var manager = KeyManager.CreateNew(out _, "password", Network.Main);
		manager.GetKeys();

		// Entering the password prepares the silent payment keys: 22 to scan with and 22 to spend with, however wide the gap limit is.
		manager.GetMasterExtKey("password");

		Assert.Equal(22, manager.GetKeys(key => key.FullKeyPath.ToString().StartsWith("352'/0'/0'/1'/", StringComparison.Ordinal)).Length);
		Assert.Equal(22, manager.GetKeys(key => key.FullKeyPath.ToString().StartsWith("352'/0'/0'/0'/", StringComparison.Ordinal)).Length);
		Assert.Equal(210 + 210 + 22 + 22, manager.GetKeyCount());
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

	/// <summary>
	/// The keys of the outputs of a coinjoin round are given out once. Whatever becomes of the round, the next one gets
	/// other keys: a round that failed has shown its addresses to the coordinator and to the other participants.
	/// </summary>
	[Fact]
	public void CoinJoinKeysAreGivenOutOnlyOnce()
	{
		var filePath = "wallet-coinjoin-keys.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);

		var firstRound = manager.GetNextCoinJoinKeys(4);
		var secondRound = manager.GetNextCoinJoinKeys(3);

		Assert.Equal([0, 1, 2, 3], firstRound.Select(key => key.Index));
		Assert.Equal([4, 5, 6], secondRound.Select(key => key.Index));

		// The keys were given out, and nothing was paid to them.
		Assert.All(firstRound.Concat(secondRound), key =>
		{
			Assert.True(key.IsInternal);
			Assert.Equal(KeyState.Locked, key.KeyState);
		});

		// The change of a payment does not go to one of them either.
		Assert.Equal(7, manager.GetNextChangeKey().Index);

		// The wallet goes on watching as many unused addresses after them as its gap limit tells.
		Assert.Equal(Enumerable.Range(7, manager.MinGapLimit), manager.GetKeys(KeyState.Clean, isInternal: true).Select(key => key.Index));

		// The wallet file knows as soon as the keys are given out. A wallet that is closed or that crashes in the middle of a
		// round does not give them out again.
		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(
			firstRound.Concat(secondRound).Select(key => key.PubKey),
			sameManager.GetKeys(KeyState.Locked, isInternal: true).Select(key => key.PubKey));
		Assert.Equal([7, 8], sameManager.GetNextCoinJoinKeys(2).Select(key => key.Index));

		// A round in which the wallet has no output of its own takes no key.
		Assert.Empty(sameManager.GetNextCoinJoinKeys(0));
		Assert.Equal(9, sameManager.GetNextChangeKey().Index);

		// A round does not ask for more keys than a wallet has outputs in it.
		Assert.Throws<ArgumentOutOfRangeException>(() => sameManager.GetNextCoinJoinKeys(KeyManager.MaxCoinJoinKeysPerRound + 1));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	/// <summary>
	/// Earlier versions kept the keys of a round locked until its transaction was broadcast, and gave them to the next round
	/// if it was not. A key that a wallet file has as locked may therefore have been in a round that failed: no round gets
	/// it as a new one.
	/// </summary>
	[Fact]
	public void KeysThatWereKeptForTheNextRoundAreNotGivenOut()
	{
		var filePath = "wallet-locked-coinjoin-keys.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		foreach (var key in manager.GetKeys(KeyState.Clean, isInternal: true).Take(3))
		{
			manager.SetKeyState(KeyState.Locked, key);
		}
		manager.ToFile();

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal([0, 1, 2], sameManager.GetKeys(KeyState.Locked, isInternal: true).Select(key => key.Index));

		Assert.Equal([3, 4], sameManager.GetNextCoinJoinKeys(2).Select(key => key.Index));

		// They are not addresses for change either.
		Assert.Equal(5, sameManager.GetNextChangeKey().Index);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	/// <summary>
	/// Rounds that fail must not take the wallet beyond its gap limit: a wallet that is recovered from the backup watches 210
	/// keys after the last one that was paid to, and does not find what is paid to a later one. When 200 keys in a row went
	/// to rounds that failed, the rounds share the ten that are left.
	/// </summary>
	[Fact]
	public void RoundsShareTheLastKeysOfTheGapLimit()
	{
		var filePath = "wallet-shared-coinjoin-keys.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out Share[] shares, "", Network.Main, filePath);

		// Twenty rounds fail one after the other. Each of them got ten keys that no round had.
		var givenOutOnce = Enumerable.Range(0, 20).SelectMany(_ => manager.GetNextCoinJoinKeys(10)).ToArray();
		Assert.Equal(Enumerable.Range(0, 200), givenOutOnce.Select(key => key.Index));

		// Ten keys are left within the gap limit. The rounds that follow get those, as often as it takes.
		var shared = manager.GetNextCoinJoinKeys(10);
		Assert.Equal(Enumerable.Range(200, 10), shared.Select(key => key.Index));
		Assert.Equal(shared, manager.GetNextCoinJoinKeys(10));
		Assert.Equal(shared.Take(4), manager.GetNextCoinJoinKeys(4));
		Assert.Equal(shared, manager.GetNextCoinJoinKeys(10));

		// A wallet that is opened again goes on like that.
		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(shared.Select(key => key.PubKey), sameManager.GetNextCoinJoinKeys(10).Select(key => key.PubKey));

		// A wallet that is recovered from the backup watches every key that a round got, and not the one after them.
		var recovered = KeyManager.Recover(shares, "", Network.Main);
		Assert.All(givenOutOnce.Concat(shared), key => Assert.True(recovered.TryGetKeyForScriptPubKey(key.P2Taproot, out _)));

		var keyBeyond = Assert.Single(manager.GetKeys(key => key.IsInternal && key.Index == 210));
		Assert.False(recovered.TryGetKeyForScriptPubKey(keyBeyond.P2Taproot, out _));

		// The change of a payment stays within the gap limit as well. Rounds had every key up to there, so it goes to the
		// last one.
		Assert.Equal(209, manager.GetNextChangeKey().Index);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	/// <summary>
	/// The keys are counted from the last one that was paid to. A round that succeeds pays to its keys, so the rounds after
	/// it get keys that no round had.
	/// </summary>
	[Fact]
	public void SharingKeysEndsWithARoundThatSucceeds()
	{
		var manager = KeyManager.CreateNew(out _, "", Network.Main);

		for (var round = 0; round < 20; round++)
		{
			manager.GetNextCoinJoinKeys(10);
		}

		// Two rounds share the last keys of the gap limit and fail as well. A third one, with six outputs, is broadcast.
		var shared = manager.GetNextCoinJoinKeys(10);
		Assert.Equal(shared, manager.GetNextCoinJoinKeys(10));

		var paid = manager.GetNextCoinJoinKeys(6);
		Assert.Equal(Enumerable.Range(200, 6), paid.Select(key => key.Index));

		manager.MarkAsPaid(paid.Select(key => key.P2Taproot));
		Assert.All(paid, key => Assert.Equal(KeyState.Used, key.KeyState));

		// The four keys that the round did not pay to were in rounds that failed. The next round gets none of them.
		Assert.Equal(210, manager.GetNextChangeKey().Index);
		Assert.Equal(Enumerable.Range(210, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));

		// The count started anew at the last key that was paid to, number 205: the keys up to number 405 are given out once,
		// and the four count among them.
		var givenOutOnce = Enumerable.Range(0, 18).SelectMany(_ => manager.GetNextCoinJoinKeys(10)).ToArray();
		Assert.Equal(Enumerable.Range(220, 180), givenOutOnce.Select(key => key.Index));

		Assert.Equal(Enumerable.Range(400, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(406, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(406, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
	}

	/// <summary>
	/// The change of a payment is paid to a key as well, so it ends the sharing like a round that succeeds.
	/// </summary>
	[Fact]
	public void SharingKeysEndsWithAPaymentThatLeavesChange()
	{
		var manager = KeyManager.CreateNew(out _, "", Network.Main);

		for (var round = 0; round < 21; round++)
		{
			manager.GetNextCoinJoinKeys(10);
		}

		Assert.Equal(Enumerable.Range(200, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));

		// The wallet learns from the transaction of the payment that its change address was paid to.
		var changeKey = manager.GetNextChangeKey();
		Assert.Equal(209, changeKey.Index);
		manager.SetKeyState(KeyState.Used, changeKey);

		Assert.Equal(210, manager.GetNextChangeKey().Index);
		Assert.Equal(Enumerable.Range(210, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
	}

	/// <summary>
	/// A round gets the keys that are still given out once before it gets shared ones, and the change of a payment goes to a
	/// key that no round had for as long as there is one within the gap limit.
	/// </summary>
	[Fact]
	public void ARoundTakesTheKeysThatAreLeftBeforeItSharesKeys()
	{
		var manager = KeyManager.CreateNew(out _, "", Network.Main);

		// 28 rounds with seven outputs each fail: 196 keys.
		for (var round = 0; round < 28; round++)
		{
			manager.GetNextCoinJoinKeys(7);
		}

		// Four keys are left that are given out once. The next round gets them, and three of the last ten.
		Assert.Equal(Enumerable.Range(196, 7), manager.GetNextCoinJoinKeys(7).Select(key => key.Index));
		Assert.Equal(203, manager.GetNextChangeKey().Index);

		// The rounds after it share the last ten, from the first of them on.
		Assert.Equal(Enumerable.Range(200, 7), manager.GetNextCoinJoinKeys(7).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(200, 7), manager.GetNextCoinJoinKeys(7).Select(key => key.Index));
		Assert.Equal(207, manager.GetNextChangeKey().Index);
	}

	/// <summary>
	/// A wallet with a wider gap limit watches more keys, but a wallet that is recovered from its backup watches 210 unless
	/// it is told otherwise. So the keys that rounds share are the last ten of 210 in every wallet.
	/// </summary>
	[Fact]
	public void AWiderGapLimitDoesNotMoveTheKeysThatRoundsShare()
	{
		var manager = KeyManager.Recover(WalletGenerator.GenerateShares(), "", Network.Main, filePath: null, minGapLimit: 550);
		Assert.Equal(550, manager.MinGapLimit);

		var givenOutOnce = Enumerable.Range(0, 20).SelectMany(_ => manager.GetNextCoinJoinKeys(10)).ToArray();
		Assert.Equal(Enumerable.Range(0, 200), givenOutOnce.Select(key => key.Index));

		Assert.Equal(Enumerable.Range(200, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(200, 10), manager.GetNextCoinJoinKeys(10).Select(key => key.Index));
	}

	/// <summary>
	/// The keys are counted from the last change address that was paid to, so the wallet file must not call an address paid
	/// to that nothing was paid to. The version before this one did that with the addresses of every round, and a round
	/// that was reported as broadcast may have a transaction that never came. A wallet that has its transactions puts that
	/// right.
	/// </summary>
	[Fact]
	public void ChangeAddressesThatWereNotPaidToCountAsGivenOut()
	{
		var filePath = "wallet-unpaid-coinjoin-keys.json";
		DeleteFileAndDirectoryIfExists(filePath);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		var changeKeys = manager.GetKeys(KeyState.Clean, isInternal: true).Take(30).ToArray();
		var receiveKey = manager.GetNextReceiveKey();

		foreach (var key in changeKeys.Append(receiveKey))
		{
			manager.SetKeyState(KeyState.Used, key);
		}
		manager.ToFile();

		// Three of the thirty change addresses were paid to.
		var paidKeys = changeKeys.Take(3).ToHashSet();

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(27, sameManager.ForgetPaymentsThatNeverCame(paidKeys.Contains));

		Assert.Equal([0, 1, 2], sameManager.GetKeys(KeyState.Used, isInternal: true).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(3, 27), sameManager.GetKeys(KeyState.Locked, isInternal: true).Select(key => key.Index));

		// Receive addresses are not counted like this and stay as they are.
		Assert.Equal(receiveKey.PubKey, Assert.Single(sameManager.GetKeys(KeyState.Used, isInternal: false)).PubKey);

		// The wallet file is put right, and there is nothing left to do the next time.
		var thirdManager = KeyManager.FromFile(filePath);
		Assert.Equal(Enumerable.Range(3, 27), thirdManager.GetKeys(KeyState.Locked, isInternal: true).Select(key => key.Index));
		Assert.Equal(0, thirdManager.ForgetPaymentsThatNeverCame(paidKeys.Contains));

		// The count is from the third address on, which is number 2: the keys up to number 202 are given out once. The 27
		// count among them, and 173 are left.
		Assert.Equal(30, thirdManager.GetNextChangeKey().Index);
		for (var round = 0; round < 17; round++)
		{
			thirdManager.GetNextCoinJoinKeys(10);
		}

		Assert.Equal(Enumerable.Range(200, 10), thirdManager.GetNextCoinJoinKeys(10).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(203, 10), thirdManager.GetNextCoinJoinKeys(10).Select(key => key.Index));
		Assert.Equal(Enumerable.Range(203, 10), thirdManager.GetNextCoinJoinKeys(10).Select(key => key.Index));

		DeleteFileAndDirectoryIfExists(filePath);
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
	public void SettingsThatAreGoneAreReadOverAndNotWrittenAgain()
	{
		var filePath = "wallet-settings-that-are-gone.json";

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.ToFile();

		// A wallet file of an older version has the settings of the automatic coinjoin, of the private coins and of
		// the isolation of the non-private ones, and the list of the coins that were kept out of coinjoins.
		var json = File.ReadAllText(filePath);
		Assert.DoesNotContain("\"AutoCoinJoin\"", json);
		Assert.DoesNotContain("\"PlebStopThreshold\"", json);
		Assert.DoesNotContain("\"AnonScoreTarget\"", json);
		Assert.DoesNotContain("\"OnlyUsePrivateFundsForPayments\"", json);
		Assert.DoesNotContain("\"RedCoinIsolation\"", json);
		Assert.DoesNotContain("\"ExcludedCoinsFromCoinJoin\"", json);

		var keptOut = Convert.ToHexString(new OutPoint(uint256.One, 3).ToBytes()).ToLowerInvariant();
		var start = json.IndexOf('{') + 1;
		File.WriteAllText(filePath, json.Insert(start, $"\"AutoCoinJoin\": true, \"PlebStopThreshold\": \"0.01\", \"AnonScoreTarget\": 50, \"OnlyUsePrivateFundsForPayments\": true, \"RedCoinIsolation\": true, \"ExcludedCoinsFromCoinJoin\": [\"{keptOut}\"],"));

		// The wallet opens as it did, and what it knows is as it was.
		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(manager.MasterFingerprint, sameManager.MasterFingerprint);
		Assert.Equal(manager.TaprootExtPubKey, sameManager.TaprootExtPubKey);

		// A coin that was kept out of coinjoins is not a coin that the user chose for them. Nor is any other.
		Assert.Empty(sameManager.CoinsSelectedForCoinJoin);

		// The next time the file is written, they are not in it any more.
		sameManager.ToFile();
		json = File.ReadAllText(filePath);
		Assert.DoesNotContain("\"AutoCoinJoin\"", json);
		Assert.DoesNotContain("\"PlebStopThreshold\"", json);
		Assert.DoesNotContain("\"AnonScoreTarget\"", json);
		Assert.DoesNotContain("\"OnlyUsePrivateFundsForPayments\"", json);
		Assert.DoesNotContain("\"RedCoinIsolation\"", json);
		Assert.DoesNotContain("\"ExcludedCoinsFromCoinJoin\"", json);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void CoinsChosenForCoinJoinsAreKeptInTheWalletFile()
	{
		var filePath = "wallet-coins-chosen-for-coinjoins.json";

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.ToFile();

		// No coin is chosen to begin with.
		Assert.Empty(manager.CoinsSelectedForCoinJoin);
		Assert.Empty(KeyManager.FromFile(filePath).CoinsSelectedForCoinJoin);

		var first = new OutPoint(uint256.One, 0);
		var second = new OutPoint(uint256.One, 1);

		manager.SetCoinsSelectedForCoinJoin([first, second, first]);
		Assert.Equal([first, second], manager.CoinsSelectedForCoinJoin);
		Assert.Equal([first, second], KeyManager.FromFile(filePath).CoinsSelectedForCoinJoin);

		// The same coins in another order are the same choice: the file is left as it is.
		File.WriteAllText(filePath, File.ReadAllText(filePath) + " ");
		var untouched = File.ReadAllText(filePath);
		manager.SetCoinsSelectedForCoinJoin([second, first]);
		Assert.Equal(untouched, File.ReadAllText(filePath));

		manager.SetCoinsSelectedForCoinJoin([second]);
		Assert.Equal([second], KeyManager.FromFile(filePath).CoinsSelectedForCoinJoin);

		manager.SetCoinsSelectedForCoinJoin([]);
		Assert.Empty(KeyManager.FromFile(filePath).CoinsSelectedForCoinJoin);

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void AnonymityPointsAreKeptInTheWalletFile()
	{
		var filePath = "wallet-anonymity-points.json";
		var transactionId = new uint256(7);
		var otherTransactionId = new uint256(8);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		Assert.Null(manager.GetAnonymityPoints(transactionId));

		// A transaction can be worth half a point more than a whole number.
		manager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [transactionId] = 3.5m, [otherTransactionId] = 0 });

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Equal(3.5m, sameManager.GetAnonymityPoints(transactionId));
		Assert.Equal(0, sameManager.GetAnonymityPoints(otherTransactionId));

		// Only the transactions that are named the last time are kept: the points of a coin are gone when it is spent.
		sameManager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [otherTransactionId] = 0 });

		var thirdManager = KeyManager.FromFile(filePath);
		Assert.Null(thirdManager.GetAnonymityPoints(transactionId));
		Assert.Equal(0, thirdManager.GetAnonymityPoints(otherTransactionId));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	/// <summary>
	/// A wallet file from before a pair of outputs was worth half a point each has the points of its transactions
	/// under another name. They followed the rule of that time, so the wallet does not take them and works them out again.
	/// </summary>
	[Fact]
	public void AnonymityPointsOfTheEarlierRuleAreNotTaken()
	{
		var filePath = "wallet-anonymity-points-of-the-earlier-rule.json";
		var transactionId = new uint256(7);
		var otherTransactionId = new uint256(8);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [otherTransactionId] = 4 });

		var walletFile = JsonNode.Parse(File.ReadAllText(filePath))!.AsObject();
		Assert.True(walletFile.ContainsKey("TransactionAnonymityPoints"));

		walletFile["AnonymityPoints"] = new JsonArray(new JsonObject { ["TransactionId"] = transactionId.ToString(), ["Points"] = 3 });
		File.WriteAllText(filePath, walletFile.ToJsonString());

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Null(sameManager.GetAnonymityPoints(transactionId));
		Assert.Equal(4, sameManager.GetAnonymityPoints(otherTransactionId));

		// The wallet does not write the earlier name again.
		sameManager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [otherTransactionId] = 5 });
		Assert.False(JsonNode.Parse(File.ReadAllText(filePath))!.AsObject().ContainsKey("AnonymityPoints"));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	/// <summary>A number that cannot be the points of a transaction was not written by the wallet, and is not taken from its file.</summary>
	[Fact]
	public void NumbersThatCannotBeAnonymityPointsAreNotTakenFromTheWalletFile()
	{
		var filePath = "wallet-anonymity-points-that-are-none.json";
		var transactionId = new uint256(7);
		var otherTransactionId = new uint256(8);
		var thirdTransactionId = new uint256(9);

		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		manager.SetAnonymityPoints(new Dictionary<uint256, decimal> { [transactionId] = 2, [otherTransactionId] = 2, [thirdTransactionId] = 2.5m });

		var text = File.ReadAllText(filePath);
		var walletFile = JsonNode.Parse(text)!.AsObject();
		var points = walletFile["TransactionAnonymityPoints"]!.AsArray();

		foreach (var entry in points)
		{
			var id = entry!["TransactionId"]!.GetValue<string>();

			if (id == transactionId.ToString())
			{
				entry["Points"] = 0.3;
			}
			else if (id == otherTransactionId.ToString())
			{
				entry["Points"] = -1;
			}
		}

		File.WriteAllText(filePath, walletFile.ToJsonString());

		var sameManager = KeyManager.FromFile(filePath);
		Assert.Null(sameManager.GetAnonymityPoints(transactionId));
		Assert.Null(sameManager.GetAnonymityPoints(otherTransactionId));
		Assert.Equal(2.5m, sameManager.GetAnonymityPoints(thirdTransactionId));

		DeleteFileAndDirectoryIfExists(filePath);
	}

	[Fact]
	public void MaxCoinjoinLeftoverIsKeptInTheWalletFile()
	{
		var filePath = "wallet-max-coinjoin-leftover.json";

		// Every wallet has a limit: 5,000 satoshis until the user sets another one.
		var manager = KeyManager.CreateNew(out _, "", Network.Main, filePath);
		Assert.Equal(Money.Satoshis(5_000), manager.MaxCoinjoinLeftover);
		manager.ToFile();
		Assert.Equal(Money.Satoshis(5_000), KeyManager.FromFile(filePath).MaxCoinjoinLeftover);

		manager.MaxCoinjoinLeftover = Money.Satoshis(1_234);
		manager.ToFile();
		Assert.Equal(Money.Satoshis(1_234), KeyManager.FromFile(filePath).MaxCoinjoinLeftover);

		// Zero is a limit, not the absence of one.
		manager.MaxCoinjoinLeftover = Money.Zero;
		manager.ToFile();
		Assert.Equal(Money.Zero, KeyManager.FromFile(filePath).MaxCoinjoinLeftover);

		// The user cannot allow more than 10,000 satoshis, whichever way the value gets into the wallet.
		manager.MaxCoinjoinLeftover = Money.Satoshis(10_001);
		Assert.Equal(Money.Satoshis(10_000), manager.MaxCoinjoinLeftover);

		var json = File.ReadAllText(filePath);
		var storedLimit = new System.Text.RegularExpressions.Regex("\"MaxCoinjoinLeftover\": \"[^\"]*\"");
		Assert.Matches(storedLimit, json);
		File.WriteAllText(filePath, storedLimit.Replace(json, "\"MaxCoinjoinLeftover\": \"0.5\""));
		Assert.Equal(Money.Satoshis(10_000), KeyManager.FromFile(filePath).MaxCoinjoinLeftover);

		// A wallet file with an empty limit, or one from before the limit existed, gets the default.
		File.WriteAllText(filePath, storedLimit.Replace(json, "\"MaxCoinjoinLeftover\": null"));
		Assert.Equal(Money.Satoshis(5_000), KeyManager.FromFile(filePath).MaxCoinjoinLeftover);
		File.WriteAllText(filePath, storedLimit.Replace(json, "\"Unrelated\": null"));
		Assert.Equal(Money.Satoshis(5_000), KeyManager.FromFile(filePath).MaxCoinjoinLeftover);

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
