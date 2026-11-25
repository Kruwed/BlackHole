using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NBitcoin;
using NNostr.Client;
using WalletWasabi.Discoverability;
using WalletWasabi.FidelityBonds;
using WalletWasabi.JoinMarket;
using WalletWasabi.NostrRelay;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.NostrRelay;

/// <summary>
/// The user of a relay chooses which events it takes: the events of Black Hole, or the kinds of events that the
/// user names.
/// </summary>
public class RelayEventSetTests
{
	private const long Now = TestRelayEvent.Now;

	private static readonly string[][] OfferTags = [["type", "BlackHoleJoinMarket"], ["network", "regtest"]];

	/// <summary>The offer that the application posts is one that the relay of another user takes.</summary>
	[Fact]
	public async Task OfferOfTheApplicationIsTakenAsync()
	{
		using var key = TestRelayEvent.NewKey();
		var offer = new MakerOffer(MinFeeRate: 1.5m, BuyRate: 120, SellRate: 150, Liquidity: 150_000_000, Points: 3, MatchedInputs: 3, ExtraInputs: 0, Bond: null);
		var signed = await MakerOfferEvent.Create(offer, Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(Now - 60)).ComputeIdAndSignAsync(key);

		using var json = JsonDocument.Parse(JsonSerializer.Serialize(signed));
		Assert.True(RelayEvent.TryParse(json.RootElement, RelayLimits.ForBlackHoleEvents, out var relayEvent, out var problem), problem);

		var verdict = new BlackHoleEventSet(Network.RegTest).Judge(relayEvent, Now);

		Assert.True(verdict.IsAccepted, verdict.Message);

		// The empty event that takes an offer back is taken the same way: it replaces the offer.
		var withdrawal = await MakerOfferEvent.CreateWithdrawal(Network.RegTest, DateTimeOffset.FromUnixTimeSeconds(Now)).ComputeIdAndSignAsync(key);
		using var withdrawalJson = JsonDocument.Parse(JsonSerializer.Serialize(withdrawal));
		Assert.True(RelayEvent.TryParse(withdrawalJson.RootElement, RelayLimits.ForBlackHoleEvents, out var relayWithdrawal, out problem), problem);
		Assert.True(new BlackHoleEventSet(Network.RegTest).Judge(relayWithdrawal, Now).IsAccepted);
	}

	/// <summary>The announcement of a coordinator is taken as well: it is of the same kind, with a type of its own.</summary>
	[Fact]
	public void AnnouncementOfACoordinatorIsTaken()
	{
		using var key = TestRelayEvent.NewKey();
		string[][] tags =
		[
			["name", "Coordinator"], ["type", "wabisabi"], ["network", "mainnet"], ["endpoint", "http://coordinator.example/"],
			["absolutemininputcount", "21"], ["readmore", "http://coordinator.example/"]
		];
		var announcement = TestRelayEvent.Create(key, 15750, "WabiSabi Coinjoin Coordinator", tags, createdAt: Now, RelayLimits.ForBlackHoleEvents);

		var verdict = new BlackHoleEventSet(Network.Main).Judge(announcement, Now);

		Assert.True(verdict.IsAccepted, verdict.Message);
		Assert.True(BlackHoleEventSet.IsOfBlackHole(CoordinatorAnnouncer.Type));
		Assert.True(BlackHoleEventSet.IsOfBlackHole(MakerOfferEvent.Type));
		Assert.False(BlackHoleEventSet.IsOfBlackHole("joinstr"));
		Assert.False(BlackHoleEventSet.IsOfBlackHole(null));
	}

	/// <summary>Everything else is refused: other kinds, other types, other networks, and events without these tags.</summary>
	[Fact]
	public void WhatIsNoEventOfBlackHoleIsRefused()
	{
		using var key = TestRelayEvent.NewKey();
		var events = new BlackHoleEventSet(Network.RegTest);

		RelayVerdict Judge(int kind, string[][] tags, long createdAt = Now) =>
			events.Judge(TestRelayEvent.Create(key, kind, "{}", tags, createdAt, RelayLimits.ForBlackHoleEvents), Now);

		Assert.True(Judge(15750, OfferTags).IsAccepted);

		const string NotOfBlackHole = "blocked: this relay takes only the offers and the coordinator announcements of Black Hole (kind 15750)";

		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(1, OfferTags));
		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(15751, OfferTags));
		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(15750, [["type", "joinstr"], ["network", "regtest"]]));
		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(15750, [["network", "regtest"]]));
		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(15750, [["type"], ["network", "regtest"]]));
		Assert.Equal(RelayVerdict.Refuse(NotOfBlackHole), Judge(15750, []));

		const string OtherNetwork = "blocked: this relay takes events for regtest only";

		Assert.Equal(RelayVerdict.Refuse(OtherNetwork), Judge(15750, [["type", "BlackHoleJoinMarket"], ["network", "mainnet"]]));
		Assert.Equal(RelayVerdict.Refuse(OtherNetwork), Judge(15750, [["type", "BlackHoleJoinMarket"]]));
		Assert.Equal(RelayVerdict.Refuse(OtherNetwork), Judge(15750, [["type", "wabisabi"], ["network", "signet"]]));
	}

	/// <summary>
	/// An event is taken whatever time its maker gives it, but for a time that is further ahead than clocks differ.
	/// For how long the relay keeps it does not hang on that time: it is a day from when the relay took it.
	/// </summary>
	[Fact]
	public void EventOfBlackHoleIsTakenUnlessItIsFromTheFuture()
	{
		using var key = TestRelayEvent.NewKey();
		var events = new BlackHoleEventSet(Network.RegTest);

		RelayVerdict Judge(long createdAt, string type = "BlackHoleJoinMarket") =>
			events.Judge(TestRelayEvent.Create(key, 15750, "{}", [["type", type], ["network", "regtest"]], createdAt, RelayLimits.ForBlackHoleEvents), Now);

		Assert.Equal(RelayVerdict.Accept(), Judge(Now));
		Assert.Equal(RelayVerdict.Accept(), Judge(Now - 15 * 60));
		Assert.Equal(RelayVerdict.Accept(), Judge(0));
		Assert.Equal(RelayVerdict.Accept(), Judge(Now - 30 * 60, "wabisabi"));

		Assert.Equal(RelayVerdict.Accept(), Judge(Now + 5 * 60));
		Assert.Equal(RelayVerdict.Refuse("invalid: the event is from further in the future than clocks differ"), Judge(Now + 5 * 60 + 1));

		Assert.Equal(TimeSpan.FromHours(24), RelayEventSet.Retention);
	}

	/// <summary>The relay of the events of Black Hole says what it is, and keeps its events in a file of its own.</summary>
	[Fact]
	public void EventsOfBlackHoleHaveAFileOfTheirOwn()
	{
		var events = new BlackHoleEventSet(Network.Main);

		Assert.Equal("BlackHoleEvents.sqlite", events.StoreFileName);
		Assert.NotEqual(events.StoreFileName, new CustomEventSet(KindList.Empty).StoreFileName);
		Assert.Same(RelayLimits.ForBlackHoleEvents, events.Limits);
		Assert.Equal("Black Hole relay", events.RelayName);
		Assert.Equal(
			"A relay inside a Black Hole wallet. It takes the offers of makers and the announcements of coordinators for mainnet (kind 15750).",
			events.Description);
	}

	/// <summary>With a list of the user's own, the relay takes the kinds of the list and no others, from any time but the future.</summary>
	[Fact]
	public void KindsThatTheUserNamedAreTaken()
	{
		using var key = TestRelayEvent.NewKey();
		Assert.True(KindList.TryParse("0, 1, 30000-39999", out var kinds, out _));
		var events = new CustomEventSet(kinds);

		RelayVerdict Judge(int kind, long createdAt = Now) => events.Judge(TestRelayEvent.Create(key, kind, "text", [], createdAt), Now);

		Assert.Equal(RelayVerdict.Accept(), Judge(0));
		Assert.Equal(RelayVerdict.Accept(), Judge(1));
		Assert.Equal(RelayVerdict.Accept(), Judge(30023));
		Assert.Equal(RelayVerdict.Accept(), Judge(1, createdAt: 0));
		Assert.Equal(RelayVerdict.Refuse("blocked: this relay does not take events of kind 2"), Judge(2));
		Assert.Equal(RelayVerdict.Refuse("blocked: this relay does not take events of kind 15750"), Judge(15750));

		Assert.Equal(RelayVerdict.Accept(), Judge(1, Now + 15 * 60));
		Assert.Equal(RelayVerdict.Refuse("invalid: the event is from further in the future than clocks differ"), Judge(1, Now + 15 * 60 + 1));

		Assert.Equal("Events.sqlite", events.StoreFileName);
		Assert.Same(RelayLimits.ForCustomEvents, events.Limits);
		Assert.Equal("A relay that takes events of these kinds: 0, 1, 30000-39999.", events.Description);

		// The relay of a custom list does not tell which program runs it.
		Assert.DoesNotContain("Black Hole", events.RelayName, StringComparison.Ordinal);
		Assert.DoesNotContain("Black Hole", events.Description, StringComparison.Ordinal);
	}
}

/// <summary>The kinds of events that the user of a relay names.</summary>
public class KindListTests
{
	[Theory]
	[InlineData("1", "1")]
	[InlineData("1, 7,30023", "1, 7, 30023")]
	[InlineData("7 1\n30023;0", "0, 1, 7, 30023")]
	[InlineData("30000-39999", "30000-39999")]
	[InlineData("5-5", "5")]
	[InlineData("1,1,1", "1")]
	[InlineData("1-5, 3-9", "1-9")]
	[InlineData("1-5, 6-9", "1-5, 6-9")]
	[InlineData("5, 1-9", "1-9")]
	[InlineData("1-9, 1", "1-9")]
	[InlineData("1, 2, 3", "1, 2, 3")]
	[InlineData("1-5, 7-9", "1-5, 7-9")]
	[InlineData("10-20, 1-30", "1-30")]
	[InlineData("0-65535", "0-65535")]
	[InlineData("", "")]
	[InlineData(" , ", "")]
	public void KindsAreReadAndPutInOrder(string text, string expected)
	{
		Assert.True(KindList.TryParse(text, out var kinds, out var problem), problem);
		Assert.Equal(expected, kinds.ToString());
		Assert.Equal(expected.Length == 0, kinds.IsEmpty);

		// What is written is read back as the same list.
		Assert.Equal(kinds, KindList.FromStrings(kinds.ToStrings()));
		Assert.Equal(kinds.GetHashCode(), KindList.FromStrings(kinds.ToStrings()).GetHashCode());
	}

	[Theory]
	[InlineData("note")]
	[InlineData("-1")]
	[InlineData("65536")]
	[InlineData("1.5")]
	[InlineData("1-2-3")]
	[InlineData("1-")]
	[InlineData("0x10")]
	[InlineData("+5")]
	public void WhatIsNoKindIsTold(string text)
	{
		Assert.False(KindList.TryParse(text, out var kinds, out var problem));
		Assert.Null(kinds);
		Assert.Contains("is not a kind of event", problem, StringComparison.Ordinal);
		Assert.True(KindList.FromStrings([text]).IsEmpty);
	}

	[Fact]
	public void SpanThatBeginsAfterItsEndIsTold()
	{
		Assert.False(KindList.TryParse("9-1", out _, out var problem));
		Assert.Equal("The span 9-1 begins after its end.", problem);
	}

	[Fact]
	public void ListKnowsItsKinds()
	{
		Assert.True(KindList.TryParse("1, 5-7, 30000-39999", out var kinds, out _));

		Assert.Equal([1, 5, 6, 7, 30000, 39999], new[] { 0, 1, 2, 4, 5, 6, 7, 8, 29999, 30000, 39999, 40000 }.Where(kinds.Contains));
		Assert.Equal([(1, 1), (5, 7), (30000, 39999)], kinds.GetSpans());
		Assert.Equal(["1", "5-7", "30000-39999"], kinds.ToStrings());
		Assert.False(KindList.Empty.Contains(1));
		Assert.NotEqual(kinds, KindList.Empty);
		Assert.False(kinds.Equals(null));
	}
}
