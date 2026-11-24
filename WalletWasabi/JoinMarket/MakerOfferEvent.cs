using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using NNostr.Client;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Serialization;

namespace WalletWasabi.JoinMarket;

/// <summary>
/// How the offer of a maker is told on Nostr: as an event of the kind for the discovery of coinjoins, with a type
/// of its own. The kind is replaceable, which means that a relay keeps the newest event of a key and forgets the ones before.
/// So every offer has a key of its own: it is posted again to keep it, and replaced with an empty event to take it back.
/// </summary>
/// <remarks>
/// The tags are the ones that the kind has: <c>type</c> and <c>network</c>. Its third tag, <c>endpoint</c>, names a place
/// where a coinjoin is reached, and a maker has no such place yet. The content tells the offer as JSON:
/// <c>minFeeRate</c> in sat/vB, <c>buy</c> and <c>sell</c> in sats per anonymity point, <c>liquidity</c> in sats,
/// <c>points</c>, the anonymity points of the coins behind the offer, <c>inputs</c>, how many of those coins count as
/// matched, and <c>extraInputs</c>, how many more there are. With a fidelity bond there is <c>bond</c>, the proof of
/// it: <c>txid</c> and <c>vout</c> name the output, <c>unlockHeight</c> and <c>key</c> are what its script is made of,
/// and <c>signature</c> is the signature of that key over the key of the event, see <see cref="BondProof"/>. The coins
/// of the maker are not told.
/// </remarks>
public static class MakerOfferEvent
{
	/// <summary>The kind of the events that announce where coinjoins can be had, see the draft "Nostr Coinjoin Discovery" (kind 15750).</summary>
	public const int Kind = 15750;

	/// <summary>What the <c>type</c> tag of the kind says for the offers of Black Hole.</summary>
	public const string Type = "BlackHoleJoinMarket";

	/// <summary>No offer tells more than this many characters. What is longer is not read.</summary>
	public const int MaxContentLength = 2_000;

	/// <summary>
	/// For how long an event of the kind counts. Whoever announces with it posts again before this time is over,
	/// to tell that it is still there.
	/// </summary>
	public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

	/// <summary>How far ahead of this computer's clock the clock of whoever made an event may be.</summary>
	public static readonly TimeSpan MaxClockDifference = TimeSpan.FromMinutes(5);

	private const string TypeTag = "type";
	private const string NetworkTag = "network";

	private static readonly Encoder<BondProof> BondEncoder = bond =>
		Encode.Object([
			("txid", Encode.UInt256(bond.OutPoint.Hash)),
			("vout", Encode.UInt(bond.OutPoint.N)),
			("unlockHeight", Encode.UInt(bond.UnlockHeight)),
			("key", Encode.String(bond.Key)),
			("signature", Encode.String(bond.Signature))
		]);

	private static readonly Decoder<BondProof> BondDecoder =
		Decode.Object(get => new BondProof(
			new OutPoint(get.Required("txid", Decode.UInt256), get.Required("vout", Decode.UInt)),
			get.Required("unlockHeight", Decode.UInt),
			get.Required("key", Decode.String),
			get.Required("signature", Decode.String)));

	// An offer without a bond says nothing about a bond.
	private static readonly Encoder<MakerOffer> Encoder = offer =>
		Encode.Object(
			new (string, JsonNode?)[]
			{
				("minFeeRate", Encode.Decimal(offer.MinFeeRate)),
				("buy", Encode.Decimal(offer.BuyRate)),
				("sell", Encode.Decimal(offer.SellRate)),
				("liquidity", Encode.Int64(offer.Liquidity)),
				("points", Encode.Decimal(offer.Points)),
				("inputs", Encode.Int(offer.MatchedInputs)),
				("extraInputs", Encode.Int(offer.ExtraInputs)),
				("bond", Encode.Optional(offer.Bond, BondEncoder))
			}.Where(field => field.Item2 is not null));

	private static readonly Decoder<MakerOffer> Decoder =
		Decode.Object(get => new MakerOffer(
			get.Required("minFeeRate", Decode.Decimal),
			get.Required("buy", Decode.Decimal),
			get.Required("sell", Decode.Decimal),
			get.Required("liquidity", Decode.Int64),
			get.Required("points", Decode.Decimal),
			get.Required("inputs", Decode.Int),
			get.Optional("extraInputs", Decode.Int, 0),
			get.Optional("bond", BondDecoder)));

	/// <summary>The name that the kind has for a network: always lowercase, <c>mainnet</c>, <c>signet</c> or <c>regtest</c>.</summary>
	public static string GetNetworkName(Network network) =>
		network.ChainName.ToString().ToLowerInvariant();

	/// <summary>Makes the event that tells an offer. It is not signed yet.</summary>
	public static NostrEvent Create(MakerOffer offer, Network network, DateTimeOffset createdAt)
	{
		if (offer.Validate() is { } reason)
		{
			throw new ArgumentException(reason, nameof(offer));
		}

		return Create(JsonEncoder.ToString(offer, Encoder), network, createdAt);
	}

	/// <summary>Makes the event that takes the offer of its key back: it replaces the offer on the relays and tells nothing. It is not signed yet.</summary>
	public static NostrEvent CreateWithdrawal(Network network, DateTimeOffset createdAt) =>
		Create("", network, createdAt);

	/// <summary>Whether an event is about an offer of Black Hole on the given network: one that tells an offer, or one that takes an offer back.</summary>
	public static bool IsAboutAnOffer(NostrEvent nostrEvent, Network network) =>
		nostrEvent.Kind == Kind
		&& GetTagValue(nostrEvent, TypeTag) == Type
		&& GetTagValue(nostrEvent, NetworkTag) == GetNetworkName(network);

	/// <summary>Whether an event counts at the given moment: it is not older than <see cref="MaxAge"/>, and not from further ahead than clocks differ.</summary>
	public static bool IsCurrent(NostrEvent nostrEvent, DateTimeOffset now) =>
		nostrEvent.CreatedAt is { } createdAt && createdAt >= now - MaxAge && createdAt <= now + MaxClockDifference;

	/// <summary>
	/// Reads the offer that an event tells on a network. It is for the caller to check who signed the event and how old it is.
	/// </summary>
	/// <returns>False if the event tells no offer of that network: it is of another kind, type or network, it takes an offer back, or what it tells is no offer.</returns>
	public static bool TryParse(NostrEvent nostrEvent, Network network, [NotNullWhen(true)] out MakerOffer? offer)
	{
		offer = null;

		if (!IsAboutAnOffer(nostrEvent, network)
			|| string.IsNullOrWhiteSpace(nostrEvent.Content)
			|| nostrEvent.Content.Length > MaxContentLength)
		{
			return false;
		}

		try
		{
			var decoded = JsonDecoder.FromString(Decoder)(nostrEvent.Content);

			if (!decoded.IsOk)
			{
				return false;
			}

			// A proof that cannot be read proves nothing. Its maker is listed without a bond, like every maker whose
			// bond is not found.
			var read = decoded.Value.Bond is { IsWellFormed: false } ? decoded.Value with { Bond = null } : decoded.Value;

			if (read.Validate() is not null)
			{
				return false;
			}

			offer = read;
			return true;
		}
		catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
		{
			// Anybody can post an event with anything in it. What cannot be read is no offer.
			return false;
		}
	}

	private static NostrEvent Create(string content, Network network, DateTimeOffset createdAt) =>
		new()
		{
			Kind = Kind,
			CreatedAt = createdAt,
			Content = content,
			Tags =
			[
				new NostrEventTag { TagIdentifier = TypeTag, Data = [Type] },
				new NostrEventTag { TagIdentifier = NetworkTag, Data = [GetNetworkName(network)] }
			]
		};

	private static string? GetTagValue(NostrEvent nostrEvent, string identifier) =>
		nostrEvent.Tags?.FirstOrDefault(tag => tag.TagIdentifier == identifier)?.Data?.FirstOrDefault();
}
