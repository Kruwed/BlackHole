using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;
using NBitcoin.Secp256k1;
using NNostr.Client;
using WalletWasabi.JoinMarket;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// An offer is told on Nostr as an event of the kind for the discovery of coinjoins (15750), with the type "BlackHoleJoinMarket".
/// </summary>
public class MakerOfferEventTests
{
	private static readonly MakerOffer Offer = new(new FeeRate(2.5m), Bid: Money.Satoshis(100), Ask: Money.Satoshis(150), Liquidity: Money.Coins(0.5m));
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

	/// <summary>The content tells the fee rate in sat/vB, the two prices in sats per point and what the coins hold in sats. It names no coin.</summary>
	[Fact]
	public void ContentTellsTheTermsAndTheLiquidity()
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);

		Assert.Equal("""{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":50000000}""", nostrEvent.Content);

		using var json = JsonDocument.Parse(nostrEvent.Content!);
		Assert.Equal(["minFeeRate", "bid", "ask", "liquidity"], json.RootElement.EnumerateObject().Select(property => property.Name));
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

		var odd = new MakerOffer(new FeeRate(0.123m), Money.Zero, Money.Satoshis(2_099_999_997_690_000), Money.Satoshis(1));
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
		Assert.False(MakerOfferEvent.TryParse(coordinator, Network.Main, out _));

		var note = MakerOfferEvent.Create(Offer, Network.Main, Now);
		note.Kind = 1;
		Assert.False(MakerOfferEvent.TryParse(note, Network.Main, out _));

		var untagged = MakerOfferEvent.Create(Offer, Network.Main, Now);
		untagged.Tags.Clear();
		Assert.False(MakerOfferEvent.TryParse(untagged, Network.Main, out _));
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[]")]
	[InlineData("{}")]
	[InlineData("""{"minFeeRate":2.5,"bid":100,"ask":150}""")] // No liquidity
	[InlineData("""{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":0}""")]
	[InlineData("""{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":-5}""")]
	[InlineData("""{"minFeeRate":2.5,"bid":150,"ask":100,"liquidity":50000000}""")] // The ask is below the bid
	[InlineData("""{"minFeeRate":0,"bid":100,"ask":150,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":-1,"bid":100,"ask":150,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":1e30,"bid":100,"ask":150,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":"a lot","bid":100,"ask":150,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":2.5,"bid":-1,"ask":150,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":2.5,"bid":100,"ask":1.5,"liquidity":50000000}""")]
	[InlineData("""{"minFeeRate":2.5,"bid":100,"ask":150,"liquidity":99999999999999999999999}""")]
	public void ContentThatIsNoOfferTellsNoOffer(string content)
	{
		var nostrEvent = MakerOfferEvent.Create(Offer, Network.Main, Now);
		nostrEvent.Content = content;

		Assert.False(MakerOfferEvent.TryParse(nostrEvent, Network.Main, out _));
	}

	[Fact]
	public void OfferThatCannotBeMadeGetsNoEvent()
	{
		Assert.Throws<ArgumentException>(() => MakerOfferEvent.Create(Offer with { Liquidity = Money.Zero }, Network.Main, Now));
		Assert.Throws<ArgumentException>(() => MakerOfferEvent.Create(Offer with { Ask = Money.Satoshis(1) }, Network.Main, Now));
	}

	private static ECPrivKey CreateKey()
	{
		using var key = new Key();
		return ECPrivKey.Create(key.ToBytes());
	}
}
