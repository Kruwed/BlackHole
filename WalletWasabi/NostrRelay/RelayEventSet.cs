using WalletWasabi.Discoverability;
using WalletWasabi.JoinMarket;

namespace WalletWasabi.NostrRelay;

/// <summary>What the relay answers about an event that it was sent.</summary>
/// <param name="Message">Why the event is refused, in the words that the relay answers with. Nothing if it is taken.</param>
public readonly record struct RelayVerdict(bool IsAccepted, string Message)
{
	public static RelayVerdict Accept() => new(true, "");

	public static RelayVerdict Refuse(string message) => new(false, message);
}

/// <summary>
/// The events that a relay of the application takes. Its user chooses between two sets: the events of Black Hole, and
/// the kinds of events that the user names. Whose events the relay takes is another matter, see
/// <see cref="RelaySenderRules"/>.
/// </summary>
public abstract class RelayEventSet
{
	/// <summary>For how long the relay keeps an event after it took it. Then the event is dropped.</summary>
	public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

	/// <summary>How much the relay lets anybody ask of it while it takes these events.</summary>
	public abstract RelayLimits Limits { get; }

	/// <summary>
	/// The name of the file that keeps the events of this set, so that they are still there after a restart. Each set
	/// has a file of its own: what a relay took while it took other events is not handed out by this one.
	/// </summary>
	public abstract string StoreFileName { get; }

	/// <summary>What the relay is called when a client asks it about itself.</summary>
	public abstract string RelayName { get; }

	/// <summary>What the relay tells about the events that it takes when a client asks it about itself.</summary>
	public abstract string Description { get; }

	/// <summary>Tells whether an event that is what it says, see <see cref="RelayEvent"/>, is one that the relay takes at the given time.</summary>
	/// <param name="now">The time, in seconds since 1970.</param>
	public abstract RelayVerdict Judge(RelayEvent relayEvent, long now);

	/// <summary>An event whose maker's clock is further ahead than clocks differ is refused: it would count as the newest for as long as it is ahead.</summary>
	protected RelayVerdict? RefuseIfFromTheFuture(RelayEvent relayEvent, long now) =>
		relayEvent.CreatedAt > now + (long)Limits.MaxClockDifference.TotalSeconds
			? RelayVerdict.Refuse("invalid: the event is from further in the future than clocks differ")
			: null;
}

/// <summary>
/// The events of Black Hole: the offers of the makers of the points market, and the announcements of coordinators.
/// Both are of the kind for the discovery of coinjoins, and both name the network that they are for.
/// </summary>
public sealed class BlackHoleEventSet : RelayEventSet
{
	/// <summary>The kind of the events of Black Hole, see <see cref="MakerOfferEvent.Kind"/>.</summary>
	public const int Kind = MakerOfferEvent.Kind;

	private const string TypeTag = "type";
	private const string NetworkTag = "network";

	private readonly string _networkName;

	/// <param name="network">The network of the wallet. The relay takes the events for that network.</param>
	public BlackHoleEventSet(Network network)
	{
		_networkName = MakerOfferEvent.GetNetworkName(network);
	}

	public override RelayLimits Limits => RelayLimits.ForBlackHoleEvents;

	public override string StoreFileName => "BlackHoleEvents.sqlite";

	public override string RelayName => $"{Constants.AppName} relay";

	public override string Description =>
		$"A relay inside a {Constants.AppName} wallet. It takes the offers of makers and the announcements of coordinators for {_networkName} (kind {Kind}).";

	/// <summary>Whether a type of the kind is one of Black Hole: the offer of a maker, or the announcement of a coordinator.</summary>
	public static bool IsOfBlackHole(string? type) => type is MakerOfferEvent.Type or CoordinatorAnnouncer.Type;

	public override RelayVerdict Judge(RelayEvent relayEvent, long now)
	{
		if (relayEvent.Kind != Kind || !IsOfBlackHole(relayEvent.GetTagValue(TypeTag)))
		{
			return RelayVerdict.Refuse($"blocked: this relay takes only the offers and the coordinator announcements of {Constants.AppName} (kind {Kind})");
		}

		if (relayEvent.GetTagValue(NetworkTag) != _networkName)
		{
			return RelayVerdict.Refuse($"blocked: this relay takes events for {_networkName} only");
		}

		return RefuseIfFromTheFuture(relayEvent, now) ?? RelayVerdict.Accept();
	}
}

/// <summary>The kinds of events that the user named. The relay takes every event of these kinds.</summary>
public sealed class CustomEventSet : RelayEventSet
{
	public CustomEventSet(KindList kinds)
	{
		Kinds = kinds;
	}

	public KindList Kinds { get; }

	public override RelayLimits Limits => RelayLimits.ForCustomEvents;

	public override string StoreFileName => "Events.sqlite";

	// Nothing here tells which program runs the relay: whoever keeps notes on it need not tell that they have a wallet.
	public override string RelayName => "Nostr relay";

	public override string Description => $"A relay that takes events of these kinds: {Kinds}.";

	public override RelayVerdict Judge(RelayEvent relayEvent, long now)
	{
		if (!Kinds.Contains(relayEvent.Kind))
		{
			return RelayVerdict.Refuse($"blocked: this relay does not take events of kind {relayEvent.Kind}");
		}

		return RefuseIfFromTheFuture(relayEvent, now) ?? RelayVerdict.Accept();
	}
}
