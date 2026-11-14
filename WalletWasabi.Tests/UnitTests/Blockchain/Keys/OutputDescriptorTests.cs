using NBitcoin;
using NBitcoin.WalletPolicies;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Blockchain.Keys;

public class OutputDescriptorTests
{
	[Fact]
	public void DescribesTheTaprootAccount()
	{
		var keyManager = KeyManager.Recover(ServiceFactory.CreateBackup(ServiceFactory.FixedSeed), "", Network.Main);

		var descriptor = keyManager.GetOutputDescriptor(Network.Main);

		// The seed is the one of the first BIP32 test vector, whose master fingerprint is 3442193e.
		Assert.Equal($"tr([3442193e/86'/0'/0']{keyManager.TaprootExtPubKey.ToString(Network.Main)}/<0;1>/*)", descriptor[..descriptor.IndexOf('#')]);

		// It is a valid descriptor and it carries the right checksum.
		var withoutChecksum = descriptor[..descriptor.IndexOf('#')];
		Assert.Equal(Miniscript.AddChecksum(withoutChecksum), descriptor);
		Assert.Equal(withoutChecksum, WalletPolicy.Parse(withoutChecksum, Network.Main).FullDescriptor.ToString());
	}

	[Fact]
	public void DescribesTheAddressesOfTheWallet()
	{
		var keyManager = KeyManager.Recover(ServiceFactory.CreateBackup(ServiceFactory.FixedSeed), "", Network.Main);
		var accountKey = keyManager.TaprootExtPubKey;

		// What "tr(xpub/<0;1>/*)" stands for: receive addresses on chain 0 and change addresses on chain 1,
		// each one the BIP86 Taproot address of the derived key.
		var receiveKey = keyManager.GetNextReceiveKey(new LabelsArray("someone"));
		var changeKey = keyManager.GetNextChangeKey();

		Assert.Equal(
			accountKey.Derive(0).Derive((uint)receiveKey.Index).PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main),
			receiveKey.GetAddress(Network.Main));
		Assert.Equal(
			accountKey.Derive(1).Derive((uint)changeKey.Index).PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main),
			changeKey.GetAddress(Network.Main));
	}

	[Theory]
	[InlineData("Main", "86'/0'/0'")]
	[InlineData("TestNet4", "86'/1'/0'")]
	[InlineData("RegTest", "86'/0'/0'")]
	public void UsesTheBip86AccountOfTheNetwork(string networkName, string expectedAccountKeyPath)
	{
		var network = Network.GetNetwork(networkName)!;
		var keyManager = KeyManager.Recover(ServiceFactory.CreateBackup(ServiceFactory.FixedSeed), "", network);

		Assert.Equal(expectedAccountKeyPath, keyManager.TaprootAccountKeyPath.ToString());
		Assert.Contains($"/{expectedAccountKeyPath}]", keyManager.GetOutputDescriptor(network));
	}
}
