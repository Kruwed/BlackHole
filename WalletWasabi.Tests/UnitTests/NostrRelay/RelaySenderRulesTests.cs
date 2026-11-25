using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using NNostr.Client.Protocols;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The user of a relay says whose events it takes: keys with a fidelity bond of a size behind them, and keys that
/// the user names. Keys are written as people hand them to each other, as npubs, and a bond is shown with its proof.
/// </summary>
public class RelaySenderRulesTests
{
	/// <summary>A key is read as an npub or as hex, whatever case it is written in, and is the hex in lowercase afterwards.</summary>
	[Fact]
	public void KeyIsReadAsNpubOrHex()
	{
		using var key = TestRelayEvent.NewKey();
		var hex = TestRelayEvent.GetPublicKey(key);
		var npub = key.CreateXOnlyPubKey().ToNIP19();

		Assert.StartsWith("npub1", npub, StringComparison.Ordinal);
		Assert.Equal(63, npub.Length);

		Assert.True(RelayKey.TryParse(npub, out var read));
		Assert.Equal(hex, read);
		Assert.True(RelayKey.TryParse($"  {npub.ToUpperInvariant()}\t", out read));
		Assert.Equal(hex, read);
		Assert.True(RelayKey.TryParse(hex.ToUpperInvariant(), out read));
		Assert.Equal(hex, read);
		Assert.Equal(npub, RelayKey.ToNpub(hex));

		// A character that is wrong or missing, another kind of name, and what is no point of the curve.
		var wrong = npub[..^1] + (npub[^1] == 'q' ? 'p' : 'q');

		foreach (var text in new[] { "", "npub1", wrong, npub[..^1], npub + "q", "nsec1" + npub[5..], hex[..^1], hex + "0", new string('f', 64), new string('z', 64), "somebody" })
		{
			Assert.False(RelayKey.TryParse(text, out read), text);
			Assert.Null(read);
		}
	}

	/// <summary>The list of the user is read with one key on a line, or with commas or blanks between the keys. One key that is none spoils it.</summary>
	[Fact]
	public void WhitelistIsReadAsTheUserWritesIt()
	{
		using var first = TestRelayEvent.NewKey();
		using var second = TestRelayEvent.NewKey();
		var firstHex = TestRelayEvent.GetPublicKey(first);
		var secondHex = TestRelayEvent.GetPublicKey(second);
		var firstNpub = RelayKey.ToNpub(firstHex);
		var secondNpub = RelayKey.ToNpub(secondHex);

		Assert.True(RelaySenderRules.TryParseWhitelist("", out var keys, out var problem));
		Assert.Empty(keys);
		Assert.Null(problem);

		Assert.True(RelaySenderRules.TryParseWhitelist($" {firstNpub}\r\n{secondHex}\n\n{firstHex}, {secondNpub};", out keys, out _));
		Assert.Equal(new[] { firstHex, secondHex }.Order(StringComparer.Ordinal).ToArray(), keys.ToArray());

		Assert.False(RelaySenderRules.TryParseWhitelist($"{firstNpub}\nnpub1somebody", out keys, out problem));
		Assert.Null(keys);
		Assert.Equal("'npub1somebody' is not a key: a key is an npub, or 64 characters of hex.", problem);

		Assert.False(RelaySenderRules.TryParseWhitelist(firstNpub[..^1], out _, out problem));
		Assert.Equal($"'{firstNpub[..20]}…' is not a key: a key is an npub, or 64 characters of hex.", problem);

		// The settings keep npubs. What is no key there is left out.
		var rules = new RelaySenderRules(true, 5, RelaySenderRules.ReadWhitelist([secondNpub, "nonsense", firstHex]));
		Assert.Equal(new[] { firstHex, secondHex }.Order(StringComparer.Ordinal).ToArray(), rules.Whitelist.ToArray());
		Assert.Equal(rules.Whitelist.Select(RelayKey.ToNpub), rules.WhitelistToNpubs());
	}

	/// <summary>Where a bond is asked for, a key needs the minimum behind it, and a sat at least. Where none is asked for, it needs nothing.</summary>
	[Fact]
	public void RequiredShareIsTheMinimumWhereABondIsAskedFor()
	{
		Assert.Equal(0, RelaySenderRules.Anyone.RequiredShare);
		Assert.Equal(0, new RelaySenderRules(false, 50_000_000, []).RequiredShare);
		Assert.Equal(50_000_000, new RelaySenderRules(true, 50_000_000, []).RequiredShare);
		Assert.Equal(1, new RelaySenderRules(true, 0, []).RequiredShare);

		// Rules with the same keys are the same rules.
		ImmutableSortedSet<string> keys = ["b", "a"];
		Assert.Equal(new RelaySenderRules(true, 7, keys), new RelaySenderRules(true, 7, ["a", "b"]));
		Assert.Equal(new RelaySenderRules(true, 7, keys).GetHashCode(), new RelaySenderRules(true, 7, ["a", "b"]).GetHashCode());
		Assert.NotEqual(new RelaySenderRules(true, 7, keys), new RelaySenderRules(true, 7, ["a"]));
		Assert.NotEqual(new RelaySenderRules(true, 7, keys), new RelaySenderRules(true, 8, keys));
		Assert.NotEqual(new RelaySenderRules(true, 7, keys), new RelaySenderRules(false, 7, keys));
	}

	/// <summary>The least that a bond has to hold is written in bitcoin, and read to the sat.</summary>
	[Theory]
	[InlineData("", 0)]
	[InlineData("0", 0)]
	[InlineData(" 0.5 ", 50_000_000)]
	[InlineData("1", 100_000_000)]
	[InlineData("0.00000001", 1)]
	[InlineData("3.00000000", 300_000_000)]
	[InlineData(".25", 25_000_000)]
	[InlineData("21000000", 2_100_000_000_000_000)]
	public void MinimumIsWrittenInBitcoin(string text, long sats)
	{
		Assert.True(NostrRelaySettings.TryParseBond(text, out var read, out var problem), problem);
		Assert.Equal(sats, read);
		Assert.Null(problem);

		// What is written is read back as it was meant.
		Assert.True(NostrRelaySettings.TryParseBond(NostrRelaySettings.FormatBond(sats), out var again, out _));
		Assert.Equal(sats, again);
	}

	[Theory]
	[InlineData("0,5", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	[InlineData("-1", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	[InlineData("1e3", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	[InlineData("1 000", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	[InlineData("half a coin", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	[InlineData("0.000000001", "A bitcoin has 8 places after the point, and no more.")]
	[InlineData("21000000.00000001", "There are 21,000,000 bitcoin and no more.")]
	[InlineData("100000000", "There are 21,000,000 bitcoin and no more.")]
	[InlineData("99999999999999999999999999999", "Write the amount in bitcoin, with a point before the parts of a coin, like 0.5.")]
	public void MinimumThatIsNoAmountIsNotRead(string text, string expectedProblem)
	{
		Assert.False(NostrRelaySettings.TryParseBond(text, out var sats, out var problem));
		Assert.Equal(0, sats);
		Assert.Equal(expectedProblem, problem);
	}

	[Fact]
	public void MinimumIsWrittenWithoutZerosThatSayNothing()
	{
		Assert.Equal("0", NostrRelaySettings.FormatBond(0));
		Assert.Equal("0.5", NostrRelaySettings.FormatBond(50_000_000));
		Assert.Equal("1", NostrRelaySettings.FormatBond(100_000_000));
		Assert.Equal("0.00000001", NostrRelaySettings.FormatBond(1));
		Assert.Equal("0.5 BTC", RelaySenders.FormatAmount(50_000_000));
	}

	/// <summary>The settings keep the two filters beside the choice of events. What cannot be read there is no filter.</summary>
	[Fact]
	public void SettingsKeepTheFilters()
	{
		using var key = TestRelayEvent.NewKey();
		var hex = TestRelayEvent.GetPublicKey(key);

		var settings = NostrRelaySettings.Parse("BlackHole", [], isBondRequired: true, minBond: "0.25", whitelist: [RelayKey.ToNpub(hex), "nonsense"]);

		Assert.Equal(new RelaySenderRules(true, 25_000_000, [hex]), settings.Senders);
		Assert.Null(settings.Problem);

		Assert.Equal(RelaySenderRules.Anyone, NostrRelaySettings.Parse("BlackHole", []).Senders);
		Assert.Equal(new RelaySenderRules(true, 0, []), NostrRelaySettings.Parse("BlackHole", [], isBondRequired: true, minBond: "a lot").Senders);
		Assert.Equal(RelaySenderRules.Anyone, NostrRelaySettings.Default.Senders);
	}

	/// <summary>
	/// A key shows its bond with the proof of it in an event that the key signed: in a tag of any event, or in the
	/// content of an offer of Black Hole, where the wallet puts it.
	/// </summary>
	[Fact]
	public async Task BondIsShownInATagOrInAnOfferAsync()
	{
		using var node = new TestBondNode();
		using var key = TestRelayEvent.NewKey();
		using var otherKey = TestRelayEvent.NewKey();
		var bond = node.Lock(30_000_000);
		var proof = bond.Prove(key);

		// In a tag, of an event of any kind.
		string[] tag = RelayBondClaim.CreateTag(proof);
		var note = TestRelayEvent.Create(key, 1, "a note", [["t", "x"], tag]);
		Assert.True(RelayBondClaim.TryRead(note, out var read));
		Assert.Equal(proof, read);
		Assert.Equal(["bond", bond.OutPoint.Hash.ToString(), "0", "801000", proof.Key, proof.Signature], tag);
		Assert.Equal($"{bond.OutPoint.Hash}:0", RelayBondClaim.GetBondId(bond.OutPoint));

		// In the offer that the application posts.
		var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 150_000_000, Points: 3, MatchedInputs: 3, ExtraInputs: 0, Bond: proof);
		var signed = await MakerOfferEvent.Create(offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(TestRelayEvent.Now)).ComputeIdAndSignAsync(key);
		using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(signed));
		Assert.True(RelayEvent.TryParse(json.RootElement, RelayLimits.ForBlackHoleEvents, out var offerEvent, out var problem), problem);
		Assert.True(RelayBondClaim.TryRead(offerEvent, out read));
		Assert.Equal(proof, read);

		// An offer without a bond, and the event that takes an offer back, show none.
		var plain = await MakerOfferEvent.Create(offer with { Bond = null }, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(TestRelayEvent.Now)).ComputeIdAndSignAsync(key);
		using var plainJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(plain));
		Assert.True(RelayEvent.TryParse(plainJson.RootElement, RelayLimits.ForBlackHoleEvents, out var plainEvent, out _));
		Assert.False(RelayBondClaim.TryRead(plainEvent, out read));
		Assert.Null(read);

		// The proof of another key is of no use: the key of the bond signed for one key.
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(otherKey, 1, "a note", [tag]), out _));
		Assert.True(RelayBondClaim.TryRead(TestRelayEvent.Create(otherKey, 1, "a note", [bond.Tag(otherKey)]), out _));

		// What looks like a proof and is none.

		foreach (var wrong in new string[][]
		{
			["bond"],
			tag[..5],
			[.. tag, "more"],
			[tag[0], tag[1][..^1], tag[2], tag[3], tag[4], tag[5]],
			[tag[0], tag[1], "-1", tag[3], tag[4], tag[5]],
			[tag[0], tag[1], tag[2], "soon", tag[4], tag[5]],
			[tag[0], tag[1], tag[2], tag[3], tag[4][..^2], tag[5]],
			[tag[0], tag[1], tag[2], tag[3], tag[4], new string('0', 128)],
			[tag[0], tag[1], "1", tag[3], tag[4], tag[5]],
			[tag[0], tag[1], tag[2], "801001", tag[4], tag[5]]
		})
		{
			Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 1, "a note", [wrong]), out _), string.Join(' ', wrong));
		}

		// The first tag of the name is the one that counts.
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 1, "a note", [["bond"], tag]), out _));

		// In the content of an event that is no offer, a proof is text like any other.
		var content = offerEvent.Content;
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 1, content), out _));
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 15750, content, [["type", "wabisabi"], ["network", "regtest"]]), out _));
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 15750, "no JSON {", [["type", "BlackHoleJoinMarket"], ["network", "regtest"]]), out _));
		Assert.False(RelayBondClaim.TryRead(TestRelayEvent.Create(key, 15750, "[1,2]", [["type", "BlackHoleJoinMarket"], ["network", "regtest"]]), out _));
	}

	/// <summary>A pace lets work be done so many times in a row, and then so many times per second.</summary>
	[Fact]
	public void PaceHoldsAfterTheBurst()
	{
		long clock = 1_000;
		var pace = new RelayPace(burst: 3, perSecond: 0.5, () => clock);

		Assert.True(pace.TryTake());
		Assert.True(pace.TryTake());
		Assert.True(pace.TryTake());
		Assert.False(pace.TryTake());

		// Half a turn per second: one after two seconds.
		clock += 1_999;
		Assert.False(pace.TryTake());
		clock += 1;
		Assert.True(pace.TryTake());
		Assert.False(pace.TryTake());

		// However long nothing was asked, no more than the burst is saved up.
		clock += 3_600_000;
		Assert.True(pace.TryTake());
		Assert.True(pace.TryTake());
		Assert.True(pace.TryTake());
		Assert.False(pace.TryTake());
	}
}
