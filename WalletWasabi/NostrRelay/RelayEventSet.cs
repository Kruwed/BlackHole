using WalletWasabi.Discoverability;
using WalletWasabi.JoinMarket;

namespace WalletWasabi.NostrRelay;

/// <summary>What the relay answers about an event that it was sent.</summary>
/// <param name="Message">Why the event is refused, in the words that the relay answers with. Nothing if it is taken.</param>
/// <param name="ExpiresAt">When the relay forgets the event, in seconds since 1970. Null for an event that is kept until the room for events is used up.</param>
public readonly record struct RelayVerdict(bool IsAccepted, string Message, long? ExpiresAt)
{
	public static RelayVerdict Accept(long? expiresAt = null) => new(true, "", expiresAt);

	public static RelayVerdict Refuse(string message) => new(false, message, null);
}

/// <summary>
/// The events that a relay of the application takes. Its user chooses between two sets: the events of Black Hole, which
/// the relay keeps for a short time only, and the kinds of events that the user names, which it keeps.
/// </summary>
public abstract class RelayEventSet
{
	/// <summary>How much the relay lets anybody ask of it while it takes these events.</summary>
	public abstract RelayLimits Limits { get; }

	/// <summary>Whether the events are kept in a file, so that they are still there after a restart. Otherwise they are kept in memory only.</summary>
	public abstract bool IsKeptOnDisk { get; }

	/// <summary>
	/// What happens when the room for events is used up: the oldest events make way for new ones, or new ones are
	/// refused until old ones have expired.
	/// </summary>
	public abstract bool DropsOldestWhenFull { get; }

	/// <summary>How old an event may be when it comes. Null if events of any age are taken.</summary>
	public abstract TimeSpan? MaxEventAge { get; }

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
/// Both are of the kind for the discovery of coinjoins, both name the network that they are for, and both count for
/// a short time only: whoever posts one posts it again before that time is over. So the relay forgets them after that
/// time, and keeps them in memory only.
/// </summary>
public sealed class BlackHoleEventSet : RelayEventSet
{
	/// <summary>The kind of the events of Black Hole, see <see cref="MakerOfferEvent.Kind"/>.</summary>
	public const int Kind = MakerOfferEvent.Kind;

	/// <summary>For how long an offer is kept: for as long as it counts for whoever reads the order book.</summary>
	public static readonly TimeSpan OfferLifetime = MakerOfferEvent.MaxAge;

	/// <summary>
	/// For how long the announcement of a coordinator is kept: twice the time after which a coordinator announces
	/// itself again, so that the announcement of a coordinator that is still there is never missing.
	/// </summary>
	public static readonly TimeSpan CoordinatorLifetime = CoordinatorAnnouncer.AnnouncementPeriod * 2;

	private const string TypeTag = "type";
	private const string NetworkTag = "network";

	private readonly string _networkName;

	/// <param name="network">The network of the wallet. The relay takes the events for that network.</param>
	public BlackHoleEventSet(Network network)
	{
		_networkName = MakerOfferEvent.GetNetworkName(network);
	}

	public override RelayLimits Limits => RelayLimits.ForBlackHoleEvents;

	public override bool IsKeptOnDisk => false;

	public override bool DropsOldestWhenFull => false;

	public override TimeSpan? MaxEventAge => CoordinatorLifetime;

	public override string RelayName => $"{Constants.AppName} relay";

	public override string Description =>
		$"A relay inside a {Constants.AppName} wallet. It takes the offers of makers and the announcements of coordinators for {_networkName} (kind {Kind}), "
		+ $"and forgets them {OfferLifetime.TotalMinutes:0} and {CoordinatorLifetime.TotalMinutes:0} minutes after they were made.";

	/// <summary>For how long an event of the given type is kept. Null for a type that is none of Black Hole.</summary>
	public static TimeSpan? GetLifetime(string? type) =>
		type switch
		{
			MakerOfferEvent.Type => OfferLifetime,
			CoordinatorAnnouncer.Type => CoordinatorLifetime,
			_ => null
		};

	public override RelayVerdict Judge(RelayEvent relayEvent, long now)
	{
		if (relayEvent.Kind != Kind || GetLifetime(relayEvent.GetTagValue(TypeTag)) is not { } lifetime)
		{
			return RelayVerdict.Refuse($"blocked: this relay takes only the offers and the coordinator announcements of {Constants.AppName} (kind {Kind})");
		}

		if (relayEvent.GetTagValue(NetworkTag) != _networkName)
		{
			return RelayVerdict.Refuse($"blocked: this relay takes events for {_networkName} only");
		}

		if (RefuseIfFromTheFuture(relayEvent, now) is { } refusal)
		{
			return refusal;
		}

		var expiresAt = relayEvent.CreatedAt + (long)lifetime.TotalSeconds;

		return expiresAt > now
			? RelayVerdict.Accept(expiresAt)
			: RelayVerdict.Refuse("invalid: the event is too old to count");
	}
}

/// <summary>
/// The kinds of events that the user named. The relay takes every event of these kinds, from anybody, and keeps it in
/// a file until the room for events is used up: then the oldest events make way for new ones.
/// </summary>
public sealed class CustomEventSet : RelayEventSet
{
	public CustomEventSet(KindList kinds)
	{
		Kinds = kinds;
	}

	public KindList Kinds { get; }

	public override RelayLimits Limits => RelayLimits.ForCustomEvents;

	public override bool IsKeptOnDisk => true;

	public override bool DropsOldestWhenFull => true;

	public override TimeSpan? MaxEventAge => null;

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
