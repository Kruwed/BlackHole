using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Extensions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets.Slip39;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The keys of the fidelity bonds are a chain of the wallet's account, one key for every block height that a bond can
/// unlock at. The wallet watches the addresses that it gave out, and looks for the others when it goes through the chain again.
/// </summary>
public class FidelityBondKeysTests
{
	private const string Password = "password";

	[Fact]
	public void KeysAreTheThirdChainOfTheAccountByUnlockHeight()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password, ServiceFactory.FixedSeed);
		var master = ExtKey.CreateFromSeed(ServiceFactory.FixedSeed);

		foreach (var unlockHeight in new uint[] { 1, 150, 880_000, 1_028_732, BondScript.MaxUnlockHeight })
		{
			var path = new KeyPath($"m/86'/0'/0'/2/{unlockHeight}");

			Assert.Equal(path, keyManager.FidelityBonds.GetKeyPath(unlockHeight));
			Assert.Equal(master.Derive(path).PrivateKey.PubKey, keyManager.FidelityBonds.GetPubKey(unlockHeight));

			// The private key takes the password, the public key does not.
			using var secret = keyManager.GetFidelityBondSecret(Password, unlockHeight);
			Assert.Equal(master.Derive(path).PrivateKey, secret);
			Assert.Equal(keyManager.FidelityBonds.GetPubKey(unlockHeight), secret.PubKey);

			// The script of the height is made of the height and of its key.
			Assert.Equal(BondScript.CreateLeaf(secret.PubKey, unlockHeight), keyManager.FidelityBonds.GetLeaf(unlockHeight));
			Assert.Equal(BondScript.GetScriptPubKey(BondScript.CreateLeaf(secret.PubKey, unlockHeight)), keyManager.FidelityBonds.GetScriptPubKey(unlockHeight));
		}

		Assert.Throws<SecurityException>(() => keyManager.GetFidelityBondSecret("wrong password", 880_000));
	}

	[Fact]
	public void HeightThatALocktimeCannotNameHasNoKey()
	{
		var keys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;

		Assert.Throws<ArgumentOutOfRangeException>(() => keys.GetPubKey(0));
		Assert.Throws<ArgumentOutOfRangeException>(() => keys.GetKeyPath(BondScript.MaxUnlockHeight + 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => keys.GetScriptPubKey(uint.MaxValue));
		Assert.Throws<ArgumentOutOfRangeException>(() => keys.Register(0));
	}

	[Fact]
	public void AddressOfABondIsATaprootAddressOfItsOwn()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password);
		var keys = keyManager.FidelityBonds;

		Assert.StartsWith("bc1p", keys.GetAddress(880_000, Network.Main).ToString());
		Assert.StartsWith("bcrt1p", keys.GetAddress(880_000, Network.RegTest).ToString());
		Assert.NotEqual(keys.GetAddress(880_000, Network.Main), keys.GetAddress(880_001, Network.Main));

		// It is no address of the wallet: not the one that its key has as a receive address, and no key of the wallet answers for it.
		Assert.NotEqual(keys.GetPubKey(880_000).GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), keys.GetScriptPubKey(880_000));
		Assert.False(keyManager.TryGetKeyForScriptPubKey(keys.GetScriptPubKey(880_000), out _));
	}

	[Fact]
	public void WalletWatchesTheAddressesThatItGaveOut()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password);
		var keys = keyManager.FidelityBonds;
		var registered = new List<uint>();
		keys.HeightRegistered += registered.Add;

		// Nobody was given this address, so nothing can have been paid to it.
		Assert.Empty(keys.KnownHeights);
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(880_000), out _));

		var version = keys.Version;
		Assert.True(keys.Register(880_000));
		Assert.NotEqual(version, keys.Version);

		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(880_000), out var unlockHeight));
		Assert.Equal(880_000u, unlockHeight);
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(880_001), out _));

		// Asking for the address of a height is what makes the wallet watch it.
		var address = keyManager.GetFidelityBondAddress(900_000);
		Assert.Equal(keys.GetAddress(900_000, Network.Main), address);
		Assert.True(keys.TryGetUnlockHeight(address.ScriptPubKey, out unlockHeight));
		Assert.Equal(900_000u, unlockHeight);

		// Once is enough.
		version = keys.Version;
		Assert.False(keys.Register(880_000));
		keyManager.GetFidelityBondAddress(900_000);
		Assert.Equal(version, keys.Version);

		keys.Register(100);
		Assert.Equal([100u, 880_000u, 900_000u], keys.KnownHeights);
		Assert.Equal([880_000u, 900_000u, 100u], registered);
	}

	[Fact]
	public void OnlyTheBondsOfTheWalletAreItsBonds()
	{
		var keys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;
		var otherKeys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;
		keys.Register(880_000);

		// The same height in another wallet.
		Assert.False(keys.TryGetUnlockHeight(otherKeys.GetScriptPubKey(880_000), out _));

		// The key of the height with another height.
		Assert.False(keys.TryGetUnlockHeight(BondScript.GetScriptPubKey(BondScript.CreateLeaf(keys.GetPubKey(880_000), 880_001)), out _));

		// What the key is by itself, and what is no Taproot output at all.
		Assert.False(keys.TryGetUnlockHeight(keys.GetPubKey(880_000).GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), out _));
		Assert.False(keys.TryGetUnlockHeight(keys.GetPubKey(880_000).GetScriptPubKey(ScriptPubKeyType.Segwit), out _));
		Assert.False(keys.TryGetUnlockHeight(Script.Empty, out _));
	}

	[Fact]
	public void SearchFindsABondAtAHeightThatTheWalletDoesNotRemember()
	{
		var keys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;
		var otherKeys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;
		var registered = new List<uint>();
		keys.HeightRegistered += registered.Add;

		Assert.False(keys.IsSearching);
		keys.PrepareSearch(100, 300, CancellationToken.None);
		Assert.True(keys.IsSearching);

		// Nothing is watched because of the search alone.
		Assert.Empty(keys.KnownHeights);

		// A bond at one of the searched heights is found, and its address is watched from then on.
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(250), out var unlockHeight));
		Assert.Equal(250u, unlockHeight);
		Assert.Equal([250u], keys.KnownHeights);
		Assert.Equal([250u], registered);

		// The first and the last height are searched, the ones next to them are not.
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(100), out _));
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(300), out _));
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(99), out _));
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(301), out _));

		// The bond of another wallet at a searched height is none of this wallet.
		Assert.False(keys.TryGetUnlockHeight(otherKeys.GetScriptPubKey(250), out _));

		// The chain grew: more heights, at the upper end.
		keys.PrepareSearch(100, 400, CancellationToken.None);
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(301), out _));
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(400), out _));
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(401), out _));

		// Asked for less, the search keeps what it has.
		keys.PrepareSearch(200, 350, CancellationToken.None);
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(150), out _));

		// A search that starts earlier covers the earlier heights too.
		keys.PrepareSearch(50, 400, CancellationToken.None);
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(50), out _));
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(399), out _));

		// After the search the wallet watches what it found, and nothing else.
		var found = keys.KnownHeights.ToArray();
		keys.EndSearch();
		Assert.False(keys.IsSearching);
		Assert.Equal(found, keys.KnownHeights);
		Assert.True(keys.TryGetUnlockHeight(keys.GetScriptPubKey(250), out _));
		Assert.False(keys.TryGetUnlockHeight(keys.GetScriptPubKey(251), out _));
	}

	[Fact]
	public void SearchCanBeCalledOff()
	{
		var keys = ServiceFactory.CreateKeyManager(Password).FidelityBonds;
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		Assert.ThrowsAny<OperationCanceledException>(() => keys.PrepareSearch(1, 100_000, cts.Token));
		Assert.False(keys.IsSearching);
	}

	[Fact]
	public void WalletFileRemembersTheWatchedHeights()
	{
		var filePath = Path.Combine(Path.GetTempPath(), $"bonds-{Path.GetRandomFileName()}.json");

		try
		{
			var keyManager = KeyManager.CreateNew(out _, Password, Network.Main, filePath);
			Assert.Empty(KeyManager.FromFile(filePath).FidelityBonds.KnownHeights);

			// The file knows the height before anyone knows the address.
			var address = keyManager.GetFidelityBondAddress(880_000);
			keyManager.FidelityBonds.Register(1_028_732);

			var loaded = KeyManager.FromFile(filePath);
			Assert.Equal([880_000u, 1_028_732u], loaded.FidelityBonds.KnownHeights);
			Assert.True(loaded.FidelityBonds.TryGetUnlockHeight(address.ScriptPubKey, out var unlockHeight));
			Assert.Equal(880_000u, unlockHeight);
			Assert.Null(loaded.GetFidelityBondSearchStart());
		}
		finally
		{
			File.Delete(filePath);
		}
	}

	[Fact]
	public void WalletThatGoesThroughTheChainAgainSearchesForItsBonds()
	{
		var filePath = Path.Combine(Path.GetTempPath(), $"bonds-{Path.GetRandomFileName()}.json");
		var shares = WalletGenerator.GenerateShares().Take(WalletGenerator.DefaultShamirThreshold).ToArray();

		try
		{
			// A new wallet has no bond that it does not know of.
			Assert.Null(KeyManager.CreateNew(out _, Password, Network.Main).GetFidelityBondSearchStart());

			// The backup does not tell the unlock heights. A recovered wallet looks for them from its first block on.
			var recovered = KeyManager.Recover(shares, Password, Network.Main, filePath, birthHeight: new Height.ChainHeight(850_000));
			Assert.Equal(850_000u, recovered.GetFidelityBondSearchStart());
			Assert.Equal(850_000u, KeyManager.FromFile(filePath).GetFidelityBondSearchStart());
			Assert.Equal(Network.Main.TaprootActivationHeight.Height, KeyManager.Recover(shares, Password, Network.Main).GetFidelityBondSearchStart());
			Assert.Equal(1u, KeyManager.Recover(shares, Password, Network.RegTest).GetFidelityBondSearchStart());

			// The search goes on over a restart, until the wallet went through the chain.
			recovered.FidelityBonds.PrepareSearch(850_000, 850_100, CancellationToken.None);
			Assert.True(recovered.FidelityBonds.TryGetUnlockHeight(recovered.FidelityBonds.GetScriptPubKey(850_050), out _));

			recovered.EndFidelityBondSearch();
			Assert.Null(recovered.GetFidelityBondSearchStart());
			Assert.False(recovered.FidelityBonds.IsSearching);

			var loaded = KeyManager.FromFile(filePath);
			Assert.Null(loaded.GetFidelityBondSearchStart());
			Assert.Equal([850_050u], loaded.FidelityBonds.KnownHeights);

			// A resync searches again: another copy of the wallet may have made a bond that this file does not remember.
			loaded.SetResyncParameters(900_000, loaded.MinGapLimit);
			Assert.Equal(900_000u, loaded.GetFidelityBondSearchStart());

			// Of two starts, the earlier one counts.
			loaded.SetResyncParameters(950_000, loaded.MinGapLimit);
			Assert.Equal(900_000u, loaded.GetFidelityBondSearchStart());
			loaded.SetResyncParameters(0, loaded.MinGapLimit);
			Assert.Equal(1u, loaded.GetFidelityBondSearchStart());
			Assert.Equal(1u, KeyManager.FromFile(filePath).GetFidelityBondSearchStart());
		}
		finally
		{
			File.Delete(filePath);
		}
	}
}
