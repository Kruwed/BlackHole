using System.Linq;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using WalletWasabi.FidelityBonds;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The script of a fidelity bond is a Taproot output that can only be spent by one script: a timelock by block height
/// and a signature.
/// </summary>
public class BondScriptTests
{
	private static readonly PubKey PubKey = new("0330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3c");
	private const string XOnly = "30d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3c";

	/// <remarks>
	/// The expected values were calculated apart from the wallet, with the curve arithmetic and the hashes written out
	/// after BIP340, BIP341 and BIP350. The first byte of the control block tells the parity of the output key.
	/// </remarks>
	[Theory]
	[InlineData(1u, "51", "f5aff74467ad753ff0cdce74f678ddbd88f8e11643152caa5df225c9d1757d79", "bc1p7khlw3r8446nluxdee60v7xahky03cgkgv2je2ja7gjun5t404us3246tf", 0xc1)]
	[InlineData(16u, "60", "0532e94c7d721ee2d07c6f6eb1ced3422eb1ac2933fb3555dbfa238f00ac22bd", "bc1pq5ewjnrawg0w95rudahtrnkngghtrtpfx0an24wmlg3c7q9vy27s5w0wgg", 0xc1)]
	[InlineData(17u, "0111", "d23dab798717879eca1ef183e084bc6f6314df486355d25d8686d97299661dbc", "bc1p6g76k7v8z7reajs77xp7pp9uda33fh6gvd2ayhvxsmvh9xtxrk7qyrgfp9", 0xc0)]
	[InlineData(127u, "017f", "070b23d2d8a0579be436ea50a8ec25bd551bd18d26236e9634c0f7c09f681bf0", "bc1pqu9j85kc5ptehepkafg23mp9h423h5vdyc3ka935crmup8mgr0cq0568zy", 0xc0)]
	[InlineData(128u, "028000", "e7510cea16077824eb1ecf25bd00b4c211d38bbe3e8d56d73ccffab20e38254c", "bc1puagse6skqauzf6c7eujm6q95cgga8za786x4d4euelatyr3cy4xqggetw3", 0xc1)]
	[InlineData(150u, "029600", "6f8810c12a3a469cb872e02220dc436444aa6340a953050a8ea176bd81434135", "bc1pd7yppsf28frfewrjuq3zphzrv3z25c6q49fs2z5w59mtmq2rgy6shenjgq", 0xc0)]
	[InlineData(32_768u, "03008000", "56f15ee8c552bf5aff30bef7e8a70a107b6b22cee112549a8910aa4dc00664c3", "bc1p2mc4a6x922l44leshmm73fc2zpakkgkwuyf9fx5fzz4ymsqxvnpsjyckhh", 0xc0)]
	[InlineData(880_000u, "03806d0d", "6a83e30bd2559d576efeb8d5679c23467fa4a2b955b46a6e8eda2bb15230b76c", "bc1pd2p7xz7j2kw4wmh7hr2k08prgel6fg4e2k6x5m5wmg4mz53skakqcnv02g", 0xc0)]
	[InlineData(1_028_732u, "037cb20f", "a9c5a0d76e5c23ce80e69c3940d0b6421aedfdff638675ee91934e2f2c47295a", "bc1p48z6p4mwts3uaq8xnsu5p59kggdwml0lvwr8tm53jd8z7tz899dqucn23s", 0xc1)]
	[InlineData(8_388_608u, "0400008000", "6d4b9623a9e18a1273a16c8648da4fe712a0416ac1f83ec4d03fe3880b3ad746", "bc1pd49evgafux9pyuapdjry3kj0uuf2qst2c8ura3xs8l3csze66arq62krg3", 0xc1)]
	[InlineData(499_999_999u, "04ff64cd1d", "5ccc5517cfe808475316d018c3ae40b44a186f4591922457c83477e65fc2e7ae", "bc1ptnx92970aqyyw5ck6qvv8tjqk39psm69jxfzg47gx3m7vh7zu7hqrpvl6d", 0xc0)]
	public void BondIsATaprootOutputThatCommitsToItsScript(uint unlockHeight, string heightPush, string outputKey, string address, byte controlByte)
	{
		var leaf = BondScript.CreateLeaf(PubKey, unlockHeight);

		// The height, OP_CHECKLOCKTIMEVERIFY, OP_DROP, the key by its x coordinate, OP_CHECKSIG.
		Assert.Equal($"{heightPush}b17520{XOnly}ac", leaf.ToHex());

		var scriptPubKey = BondScript.GetScriptPubKey(leaf);
		Assert.Equal($"5120{outputKey}", scriptPubKey.ToHex());
		Assert.True(scriptPubKey.IsScriptType(ScriptType.Taproot));
		Assert.Equal(address, BondScript.GetAddress(leaf, Network.Main).ToString());

		// The proof that the output commits to the script: the internal key and nothing else, as there is no other script.
		var controlBlock = BondScript.GetSpendInfo(leaf).GetControlBlock(BondScript.ToTapScript(leaf)).ToBytes();
		Assert.Equal(33, controlBlock.Length);
		Assert.Equal(controlByte, controlBlock[0]);
		Assert.Equal(BondScript.UnspendableKey.ToBytes(), controlBlock[1..]);
		Assert.Equal(scriptPubKey, BondScript.GetSpendInfo(leaf).OutputPubKey.ScriptPubKey);

		Assert.True(BondScript.TryParseLeaf(leaf, out var parsedHeight, out var parsedKey));
		Assert.Equal(unlockHeight, parsedHeight);
		Assert.Equal(XOnly, Encoders.Hex.EncodeData(parsedKey));
	}

	[Fact]
	public void NobodyKnowsThePrivateKeyOfTheInternalKey()
	{
		// It is the point of BIP341: its x coordinate is the hash of the generator of the curve, written out in full.
		using var one = new Key(Encoders.Hex.DecodeData("0000000000000000000000000000000000000000000000000000000000000001"));
		var generator = one.PubKey.Decompress().ToBytes();

		Assert.Equal(65, generator.Length);
		Assert.Equal(Hashes.SHA256(generator), BondScript.UnspendableKey.ToBytes());
	}

	[Fact]
	public void ScriptNamesTheKeyByItsXCoordinate()
	{
		// The two keys with this x coordinate make the same script. Whoever signs for one of them signs for the script.
		var even = new PubKey("0230d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3c");

		Assert.Equal(BondScript.CreateLeaf(PubKey, 880_000), BondScript.CreateLeaf(even, 880_000));
		Assert.Equal(BondScript.CreateLeaf(PubKey, 880_000), BondScript.CreateLeaf(Encoders.Hex.DecodeData(XOnly), 880_000));
	}

	[Fact]
	public void EveryHeightAndEveryKeyHasItsOwnAddress()
	{
		using var otherKey = new Key();

		var scripts = new[]
		{
			BondScript.GetScriptPubKey(BondScript.CreateLeaf(PubKey, 880_000)),
			BondScript.GetScriptPubKey(BondScript.CreateLeaf(PubKey, 880_001)),
			BondScript.GetScriptPubKey(BondScript.CreateLeaf(otherKey.PubKey, 880_000))
		};

		Assert.Equal(3, scripts.Distinct().Count());

		// And it is not the address that the key has by itself.
		Assert.DoesNotContain(PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), scripts);
	}

	[Theory]
	[InlineData(0u)]
	[InlineData(500_000_000u)] // From here on a locktime is a time.
	[InlineData(uint.MaxValue)]
	public void HeightThatALocktimeCannotNameIsRefused(uint unlockHeight)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => BondScript.CreateLeaf(PubKey, unlockHeight));
	}

	[Fact]
	public void KeyThatIsNoXCoordinateIsRefused()
	{
		Assert.Throws<ArgumentException>(() => BondScript.CreateLeaf(PubKey.ToBytes(), 880_000));
	}

	[Theory]
	[InlineData("0110b17520{0}ac")] // 16 as a number instead of OP_16
	[InlineData("0101b17520{0}ac")] // 1 as a number instead of OP_1
	[InlineData("04806d0d00b17520{0}ac")] // 880,000 with a byte too many
	[InlineData("00b17520{0}ac")] // Height 0
	[InlineData("4fb17520{0}ac")] // -1
	[InlineData("03806d8db17520{0}ac")] // A negative number
	[InlineData("050000000001b17520{0}ac")] // More than a locktime holds
	[InlineData("040065cd1db17520{0}ac")] // 500,000,000: a time
	[InlineData("03806d0db275 20{0}ac")] // OP_CHECKSEQUENCEVERIFY
	[InlineData("03806d0db120{0}ac")] // No OP_DROP
	[InlineData("03806d0db17520{0}ad")] // OP_CHECKSIGVERIFY
	[InlineData("03806d0db17520{0}ac51")] // Something after the signature check
	[InlineData("03806d0db175210330d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d91af3cac")] // The key in full, as before Taproot
	[InlineData("03806d0db17520{0}")] // Cut off
	[InlineData("03806d0db1752030d54fd0dd420a6e5f8d3624f5f3482cae350f79d5f0753bf5beef9c2d")] // Cut off in the middle of the key
	[InlineData("")]
	public void OnlyTheExactScriptIsTakenForABond(string pattern)
	{
		var script = new Script(Encoders.Hex.DecodeData(string.Format(pattern, XOnly).Replace(" ", "")));

		Assert.False(BondScript.TryParseLeaf(script, out _, out _));
	}
}
