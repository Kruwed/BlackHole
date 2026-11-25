namespace WalletWasabi.NostrRelay;

/// <summary>
/// How much a Nostr relay of the application lets anybody ask of it. Whoever knows the address of the relay can
/// connect to it, and nothing tells one visitor from another, so everything that a visitor can make the relay do or
/// keep has a limit.
/// </summary>
public sealed record RelayLimits
{
	/// <summary>How many visitors are connected at once. When another one comes, the one that was silent the longest has to go.</summary>
	public int MaxConnections { get; init; } = 64;

	/// <summary>How long a visitor has to say what it wants after it connected.</summary>
	public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>How many bytes the request may have that opens a connection.</summary>
	public int MaxRequestHeadBytes { get; init; } = 8 * 1024;

	/// <summary>How many bytes a message of a visitor may have. An event is one message.</summary>
	public int MaxMessageBytes { get; init; } = 16 * 1024;

	/// <summary>How many messages a visitor may send in a row before the pace below holds.</summary>
	public int MessageBurst { get; init; } = 30;

	/// <summary>How many messages per second a visitor may send in the long run.</summary>
	public double MessagesPerSecond { get; init; } = 1;

	/// <summary>How many events a visitor may send in a row before the pace below holds.</summary>
	public int EventBurst { get; init; } = 10;

	/// <summary>How many events per second a visitor may send in the long run.</summary>
	public double EventsPerSecond { get; init; } = 0.1;

	/// <summary>How many messages beyond its pace a visitor may send before it is disconnected.</summary>
	public int MaxPaceViolations { get; init; } = 20;

	/// <summary>How many subscriptions a visitor may have open at once.</summary>
	public int MaxSubscriptions { get; init; } = 8;

	/// <summary>How many characters the name of a subscription may have.</summary>
	public int MaxSubscriptionIdLength { get; init; } = 64;

	/// <summary>How many filters a subscription may have.</summary>
	public int MaxFilters { get; init; } = 4;

	/// <summary>How many values a filter may list for one of the things that it asks for.</summary>
	public int MaxFilterValues { get; init; } = 1_000;

	/// <summary>How many stored events a filter is answered with at most, the newest ones first.</summary>
	public int MaxLimit { get; init; } = 2_000;

	/// <summary>How many tags an event may have.</summary>
	public int MaxEventTags { get; init; } = 16;

	/// <summary>How many characters the content of an event may have.</summary>
	public int MaxContentLength { get; init; } = 2_000;

	/// <summary>How many events the relay keeps.</summary>
	public int MaxStoredEvents { get; init; } = 5_000;

	/// <summary>How many bytes the events that the relay keeps may have together.</summary>
	public long MaxStoredBytes { get; init; } = 16L * 1024 * 1024;

	/// <summary>
	/// How many events that came in from others may wait to be sent to a visitor that listens for them. A visitor that
	/// reads slower than that is disconnected.
	/// </summary>
	public int MaxQueuedMessages { get; init; } = 512;

	/// <summary>How many bytes the events may have together that wait to be sent to a visitor.</summary>
	public int MaxQueuedBytes { get; init; } = 1024 * 1024;

	/// <summary>How many stored events are taken out of the store at a time to be sent to a visitor: as many as have a megabyte at most.</summary>
	public int QueryPageSize => Math.Clamp(1024 * 1024 / MaxMessageBytes, 4, 128);

	/// <summary>How long sending one message to a visitor may take.</summary>
	public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>How far ahead of this computer's clock the time of an event may be.</summary>
	public TimeSpan MaxClockDifference { get; init; } = TimeSpan.FromMinutes(5);

	/// <summary>
	/// How many fidelity bonds the node is asked about in a row, for all visitors together, before the pace below
	/// holds. The node is asked when a key shows a bond that the relay does not know of that key.
	/// </summary>
	public int BondLookupBurst { get; init; } = 60;

	/// <summary>How many fidelity bonds per second the node is asked about in the long run, for all visitors together.</summary>
	public double BondLookupsPerSecond { get; init; } = 1;

	/// <summary>How many fidelity bonds one visitor may have the node asked about in a row before the pace below holds.</summary>
	public int VisitorBondLookupBurst { get; init; } = 5;

	/// <summary>How many fidelity bonds per second one visitor may have the node asked about in the long run: one in twenty seconds.</summary>
	public double VisitorBondLookupsPerSecond { get; init; } = 0.05;

	/// <summary>How long an event waits for the node to be free for the bond that the event shows. The node is asked about one bond at a time.</summary>
	public TimeSpan BondLookupPatience { get; init; } = TimeSpan.FromSeconds(10);

	/// <summary>
	/// The limits of a relay that takes the events of Black Hole only: small events. A maker posts a few offers every
	/// ten minutes, and whoever reads the order book asks once.
	/// </summary>
	public static RelayLimits ForBlackHoleEvents { get; } = new();

	/// <summary>
	/// The limits of a relay that keeps the kinds of events that its user named: events of any Nostr client, which are
	/// larger and come in bursts when a client connects.
	/// </summary>
	public static RelayLimits ForCustomEvents { get; } = new()
	{
		MaxMessageBytes = 64 * 1024,
		MessageBurst = 100,
		MessagesPerSecond = 5,
		EventBurst = 50,
		EventsPerSecond = 1,
		MaxSubscriptions = 20,
		MaxFilters = 10,
		MaxLimit = 500,
		MaxEventTags = 2_500,
		MaxContentLength = 60_000,
		MaxStoredEvents = 200_000,
		MaxStoredBytes = 100L * 1024 * 1024,
		MaxClockDifference = TimeSpan.FromMinutes(15)
	};
}
