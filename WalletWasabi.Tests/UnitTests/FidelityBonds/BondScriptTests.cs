using NBitcoin;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The script of a fidelity bond is the one of JoinMarket, byte for byte.
/// </summary>
public class BondScriptTests
{
	/// <summary>The key of the test vector in the bond guide of JoinMarket NG: the first BIP84 key of the "abandon ... about" seed.</summary>
	private static readonly PubKey PubKey = new("0330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3c");

	[Fact]
	public void MatchesTheTestVectorOfJoinMarketNg()
	{
		var script = BondScript.Create(PubKey, BondLocktime.FromMonth(2026, 2));

		// The witness script of the unsigned transaction in the guide.
		Assert.Equal("0480977e69b175210330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3cac", script.ToHex());
		// The locktime, 1769904000, with its lowest byte first.
		Assert.Equal("80977e69 OP_CLTV OP_DROP 0330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3c OP_CHECKSIG", script.ToString());

		Assert.Equal("00201b5e4cdff98542146b4bf9d51213eed52b68252b810a4f15aae02d42c97f04ae", BondScript.GetScriptPubKey(script).ToHex());
		Assert.Equal("bc1qrd0yehles4ppg66tl823yylw654ksfftsy9y79d2uqk59jtlqjhqd7zpam", BondScript.GetAddress(script, Network.Main).ToString());
	}

	[Fact]
	public void LocktimeAfter2038TakesFiveBytes()
	{
		// A number in a script has a sign, so four bytes only reach the start of 2038.
		var last = BondScript.Create(PubKey, BondLocktime.FromMonth(2099, 12));
		Assert.Equal("0580785df400b175210330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3cac", last.ToHex());
		Assert.Equal(43, last.Length);

		Assert.Equal(42, BondScript.Create(PubKey, BondLocktime.FromMonth(2038, 1)).Length);
		Assert.Equal(43, BondScript.Create(PubKey, BondLocktime.FromMonth(2038, 2)).Length);
	}

	[Fact]
	public void EveryMonthAndEveryKeyHasItsOwnAddress()
	{
		using var otherKey = new Key();
		var january = BondScript.Create(PubKey, BondLocktime.FromMonth(2027, 1));
		var february = BondScript.Create(PubKey, BondLocktime.FromMonth(2027, 2));
		var otherJanuary = BondScript.Create(otherKey.PubKey, BondLocktime.FromMonth(2027, 1));

		Assert.NotEqual(BondScript.GetScriptPubKey(january), BondScript.GetScriptPubKey(february));
		Assert.NotEqual(BondScript.GetScriptPubKey(january), BondScript.GetScriptPubKey(otherJanuary));
	}

	[Fact]
	public void UncompressedKeyIsRefused()
	{
		using var key = new Key();
		var uncompressed = key.PubKey.Decompress();

		Assert.Throws<ArgumentException>(() => BondScript.Create(uncompressed, BondLocktime.FromMonth(2027, 1)));
	}
}
