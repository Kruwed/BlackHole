using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// An offer is told on Nostr as an event of the kind for the discovery of coinjoins (15750), with the type "BlackHoleJoinMarket".
/// </summary>
public class MakerOfferEventTests
{
	private static readonly MakerOffer Offer = new(MinFeeRate: 2.5m, BuyRate: 100m, SellRate: 150.5m, Liquidity: 50_000_000, Points: 2.5m, MatchedInputs: 3, ExtraInputs: 1);
	private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

	[Fact]
	public void EventIsOfTheCoinjoinDiscoveryKindWithTheTypeOfBlackHole()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);

		Assert.Equal(15750, nostrEvent.Kind);
		Assert.Equal(Now, nostrEvent.CreatedAt);
		Assert.Equal(["type", "network"], nostrEvent.Tags.Select(tag => tag.TagIdentifier));
		Assert.Equal(["BlackHoleJoinMarket"], nostrEvent.Tags[0].Data);
		Assert.Equal(["mainnet"], nostrEvent.Tags[1].Data);
	}

	/// <summary>
	/// The content tells the fee rate in sat/vB, the two rates in sats per point, the amount in sats, the points of
	/// the offer and how many coins it has. It names no coin.
	/// </summary>
	[Fact]
	public void ContentTellsTheTermsTheAmountThePointsAndTheNumberOfCoins()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);

		Assert.Equal(
			"""{"minFeeRate":2.5,"buy":100,"sell":150.5,"liquidity":50000000,"points":2.5,"inputs":3,"extraInputs":1}""",
			nostrEvent.Content);
	}

	/// <summary>An offer with a bond names the bond, the key of the bond, and carries what that key signed.</summary>
	[Fact]
	public void ContentOfABondedOfferCarriesTheProofOfTheBond()
	{
		using var bondKey = new Key();
		var outPoint = new OutPoint(uint256.Parse("00000000000000000000000000000000000000000000000000000000000000ab"), 3);
		var proof = BondProof.Create(bondKey, outPoint, 900_000, new byte[32]);

		var nostrEvent = MakerOfferEvent.Create(Offer with { Bond = proof }, Network.Main, Now);

		using var json = JsonDocument.Parse(nostrEvent.Content!);
		var bond = json.RootElement.GetProperty("bond");

		Assert.Equal(["txid", "vout", "unlockHeight", "key", "signature"], bond.EnumerateObject().Select(property => property.Name));
		Assert.Equal("00000000000000000000000000000000000000000000000000000000000000ab", bond.GetProperty("txid").GetString());
		Assert.Equal(3, bond.GetProperty("vout").GetInt32());
		Assert.Equal(900_000, bond.GetProperty("unlockHeight").GetInt32());
		Assert.Equal(proof.Key, bond.GetProperty("key").GetString());
		Assert.Equal(proof.Signature, bond.GetProperty("signature").GetString());

		Assert.True(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out var offer));
		Assert.Equal(Offer with { Bond = proof }, offer);
		Assert.True(nostrEvent.Content!.Length < MakerOfferEvent.MaxContentLength);
	}

	[Theory]
	[InlineData("Main", "mainnet")]
	[InlineData("signet", "signet")]
	[InlineData("RegTest", "regtest")]
	public void NetworkIsNamedAsTheKindNamesIt(string networkName, string expected)
	{
		var network = Network.GetNetwork(networkName)!;

		Assert.Equal(expected, MakerOfferEvent.GetNetworkName(network));
		Assert.Equal([expected], MakerOfferEvent.Create(Offer, network, Now).Tags[1].Data);
	}

	[Fact]
	public void OfferIsReadBackFromItsEvent()
	{
		Assert.True(MakerOfferEvent.TryParse(MakerOfferEvent.Create(Offer, Network.Main, Now), Network.Main, out var offer));
		Assert.Equal(Offer, offer);

		var odd = new MakerOffer(0.123m, 0m, 100_000_000m, 330, 0m, 0, 20);
		Assert.True(MakerOfferEvent.TryParse(MakerOfferEvent.Create(odd, Bitcoin.Instance.Signet, Now), Bitcoin.Instance.Signet, out offer));
		Assert.Equal(odd, offer);
	}

	[Fact]
	public async Task SignedEventIsValidAndKeepsWhatItTellsAsync()
	{
		using var key = CreateKey();

		var nostrEvent = await MakerOfferEvent.Create(Offer, Network.RegTest, Now).ComputeIdAndSignAsync(key);

		Assert.True(nostrEvent.Verify());
		Assert.Equal(key.CreateXOnlyPubKey().ToHex(), nostrEvent.PublicKey);

		// As a relay gets it and gives it out again.
		var received = JsonSerializer.Deserialize<NostrEvent>(JsonSerializer.Serialize(nostrEvent))!;

		Assert.True(received.Verify());
		Assert.True(MakerOfferEvent.TryParse(received, Network.RegTest, out var offer));
		Assert.Equal(Offer, offer);
	}

	/// <summary>An offer is taken back by an event that replaces it on the relays and tells nothing.</summary>
	[Fact]
	public void WithdrawalTellsNoOffer()
	{
		var withdrawal = MakerOfferEvent.CreateWithdrawal(Network.Main, Now);

		Assert.Equal(15750, withdrawal.Kind);
		Assert.Equal("", withdrawal.Content);
		Assert.Equal(["BlackHoleJoinMarket"], withdrawal.Tags[0].Data);
		Assert.Equal(["mainnet"], withdrawal.Tags[1].Data);
		Assert.True(MakerOfferEvent.IsAboutAnOffer(withdrawal, Network.Main));
		Assert.False(MakerOfferEvent.TryParse(withdrawal, Network.Main, out _));
	}

	[Fact]
	public void EventOfAnotherNetworkTypeOrKindTellsNoOffer()
	{
		// Another network.
		Assert.False(MakerOfferEvent.TryParse(MakerOfferEvent.Create(Offer, Network.RegTest, Now), Network.Main, out _));

		// A WabiSabi coordinator announces itself with the same kind.
		var coordinator = MakerOfferEvent.Create(Offer, Network.Main, Now);
		coordinator.Tags[0].Data = ["wabisabi"];
		Assert.False(MakerOfferEvent.IsAboutAnOffer(coordinator, Network.Main));
		Assert.False(MakerOfferEvent.TryParse(coordinator, Network.Main, out _));

		var note = MakerOfferEvent.Create(Offer, Network.Main, Now);
		note.Kind = 1;
		Assert.False(MakerOfferEvent.TryParse(note, Network.Main, out _));

		var untagged = MakerOfferEvent.Create(Offer, Network.Main, Now);
		untagged.Tags.Clear();
		Assert.False(MakerOfferEvent.TryParse(untagged, Network.Main, out _));
	}

	/// <summary>An offer counts for a quarter of an hour. A clock that is a little ahead of this one is not held against its owner.</summary>
	[Theory]
	[InlineData(0, true)]
	[InlineData(-899, true)]
	[InlineData(-900, true)]
	[InlineData(-901, false)]
	[InlineData(300, true)]
	[InlineData(301, false)]
	public void EventCountsForAQuarterOfAnHour(int secondsFromNow, bool isCurrent)
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now.AddSeconds(secondsFromNow));

		Assert.Equal(isCurrent, MakerOfferEvent.IsCurrent(nostrEvent, Now));
	}

	[Fact]
	public void EventWithoutATimeIsNotCurrent()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		nostrEvent.CreatedAt = null;

		Assert.False(MakerOfferEvent.IsCurrent(nostrEvent, Now));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"points":1,"inputs":1}""")] // No liquidity
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"inputs":1}""")] // No points
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"points":1}""")] // No coins
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":0,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":-5,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":150,"sell":100,"liquidity":50000000,"points":1,"inputs":1}""")] // The sell rate is below the buy rate
	[InlineData("""{"minFeeRate":0,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":-1,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":1e30,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":"a lot","buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":-1,"sell":150,"liquidity":50000000,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":1.5,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":99999999999999999999999,"points":1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"points":-1,"inputs":1}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":4}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":0}""")]
	[InlineData("""{"minFeeRate":2.5,"buy":100,"sell":150,"liquidity":50000000,"points":1,"inputs":1,"extraInputs":20}""")]
	public void ContentThatIsNoOfferTellsNoOffer(string content)
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		nostrEvent.Content = content;

		Assert.False(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out _));
	}

	/// <summary>
	/// A proof that cannot be read proves nothing. The offer is an offer all the same, of a maker without a bond,
	/// like the offer of every maker whose bond the node does not find.
	/// </summary>
	[Theory]
	[InlineData("\"mine\"")]
	[InlineData("""{}""")]
	[InlineData("""{"txid":"ab","vout":0,"unlockHeight":900000,"key":"00","signature":"00"}""")]
	[InlineData("""{"txid":"00000000000000000000000000000000000000000000000000000000000000ab","vout":0,"unlockHeight":900000,"key":"00","signature":"00"}""")]
	[InlineData("""{"txid":"00000000000000000000000000000000000000000000000000000000000000ab","vout":0,"unlockHeight":0,"key":"0000000000000000000000000000000000000000000000000000000000000001","signature":"00"}""")]
	public void OfferWithABondThatCannotBeReadIsAnOfferWithoutABond(string bond)
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		nostrEvent.Content = nostrEvent.Content![..^1] + ",\"bond\":" + bond + "}";

		Assert.True(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out var offer));
		Assert.Equal(Offer, offer);
		Assert.Null(offer.Bond);
	}

	/// <summary>An offer is a few numbers. Whoever sends a book in their place is not read.</summary>
	[Fact]
	public void ContentThatIsTooLongIsNotRead()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		var padding = new string(' ', MakerOfferEvent.MaxContentLength);

		nostrEvent.Content = nostrEvent.Content![..^1] + padding + "}";

		Assert.False(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out _));
	}

	/// <summary>What a later version adds to the content does not make the offer unreadable for this one.</summary>
	[Fact]
	public void FieldsThatAreNotKnownAreLeftAside()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		nostrEvent.Content = nostrEvent.Content![..^1] + ""","endpoint":"somewhere"}""";

		Assert.True(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out var offer));
		Assert.Equal(Offer, offer);
	}

	[Fact]
	public void OfferThatCannotBeMadeGetsNoEvent()
	{
		Assert.Throws<ArgumentException>(() => MakerOfferEvent.Create(Offer with { Liquidity = 0 }, Network.Main, Now));
		Assert.Throws<ArgumentException>(() => MakerOfferEvent.Create(Offer with { SellRate = 1m }, Network.Main, Now));
		Assert.Throws<ArgumentException>(() => MakerOfferEvent.Create(Offer with { MatchedInputs = 4 }, Network.Main, Now));
	}

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}
}
