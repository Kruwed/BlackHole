using System.Linq;
using System.Security;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The keys of the fidelity bonds are a chain of the wallet's account: one key for every month a bond can unlock in.
/// </summary>
public class FidelityBondKeysTests
{
	private const string Password = "password";

	[Fact]
	public void KeysAreTheThirdChainOfTheAccount()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password, ServiceFactory.FixedSeed);
		var master = ExtKey.CreateFromSeed(ServiceFactory.FixedSeed);

		foreach (var locktime in new[] { BondLocktime.First, BondLocktime.FromMonth(2027, 6), BondLocktime.Last })
		{
			var path = new KeyPath($"m/86'/0'/0'/2/{locktime.Index}");

			Assert.Equal(path, keyManager.FidelityBonds.GetKeyPath(locktime));
			Assert.Equal(master.Derive(path).PrivateKey.PubKey, keyManager.FidelityBonds.GetPubKey(locktime));

			// The private key takes the password, the public key does not.
			var secret = keyManager.GetFidelityBondSecret(Password, locktime);
			Assert.Equal(master.Derive(path).PrivateKey, secret);
			Assert.Equal(keyManager.FidelityBonds.GetPubKey(locktime), secret.PubKey);
		}

		Assert.Throws<SecurityException>(() => keyManager.GetFidelityBondSecret("wrong password", BondLocktime.First));
	}

	[Fact]
	public void SameSeedGivesTheSameBonds()
	{
		var first = ServiceFactory.CreateKeyManager(Password, ServiceFactory.FixedSeed);
		var second = ServiceFactory.CreateKeyManager("another password", ServiceFactory.FixedSeed);
		var other = ServiceFactory.CreateKeyManager(Password);
		var locktime = BondLocktime.FromMonth(2030, 1);

		Assert.Equal(first.FidelityBonds.GetAddress(locktime, Network.Main), second.FidelityBonds.GetAddress(locktime, Network.Main));
		Assert.NotEqual(first.FidelityBonds.GetAddress(locktime, Network.Main), other.FidelityBonds.GetAddress(locktime, Network.Main));
	}

	[Fact]
	public void EveryBondScriptOfTheWalletIsRecognized()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password, ServiceFactory.FixedSeed);
		var keys = keyManager.FidelityBonds;

		var scripts = BondLocktime.All.Select(locktime => (Locktime: locktime, ScriptPubKey: BondScript.GetScriptPubKey(keys.GetScript(locktime)))).ToArray();
		Assert.Equal(960, scripts.Select(x => x.ScriptPubKey).Distinct().Count());

		foreach (var (locktime, scriptPubKey) in scripts)
		{
			Assert.True(keys.TryGetLocktime(scriptPubKey, out var found));
			Assert.Equal(locktime, found.Value);
			Assert.Equal(scriptPubKey, keys.GetAddress(locktime, Network.Main).ScriptPubKey);
			Assert.True(scriptPubKey.IsScriptType(ScriptType.P2WSH));
		}
	}

	[Fact]
	public void OtherScriptsAreNoBondsOfTheWallet()
	{
		var keyManager = ServiceFactory.CreateKeyManager(Password, ServiceFactory.FixedSeed);
		var otherWallet = ServiceFactory.CreateKeyManager(Password);
		var keys = keyManager.FidelityBonds;
		var january = BondLocktime.FromMonth(2027, 1);
		var february = BondLocktime.FromMonth(2027, 2);

		// The bond of another wallet.
		Assert.False(keys.TryGetLocktime(BondScript.GetScriptPubKey(otherWallet.FidelityBonds.GetScript(january)), out _));

		// The key of one month with the locktime of another.
		Assert.False(keys.TryGetLocktime(BondScript.GetScriptPubKey(BondScript.Create(keys.GetPubKey(january), february)), out _));

		// The key of a bond as a Taproot or a SegWit v0 address.
		Assert.False(keys.TryGetLocktime(keys.GetPubKey(january).GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), out _));
		Assert.False(keys.TryGetLocktime(keys.GetPubKey(january).GetScriptPubKey(ScriptPubKeyType.Segwit), out _));

		// An address of the wallet.
		Assert.False(keys.TryGetLocktime(keyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), out _));

		// And no address of the wallet is a bond key.
		Assert.False(keyManager.TryGetKeyForScriptPubKey(BondScript.GetScriptPubKey(keys.GetScript(january)), out _));
		Assert.DoesNotContain(keyManager.GetKeys(), key => key.FullKeyPath.Parent == keys.ChainKeyPath);
	}

	[Fact]
	public void TestNetworksHaveTheirOwnAddresses()
	{
		var keyManager = KeyManager.CreateNew(out _, Password, Network.RegTest);
		var locktime = BondLocktime.FromMonth(2027, 1);

		Assert.StartsWith("bcrt1q", keyManager.FidelityBonds.GetAddress(locktime, Network.RegTest).ToString());
		Assert.Equal(KeyManager.GetAccountKeyPath(Network.RegTest).Derive(2).Derive((uint)locktime.Index), keyManager.FidelityBonds.GetKeyPath(locktime));
	}
}
