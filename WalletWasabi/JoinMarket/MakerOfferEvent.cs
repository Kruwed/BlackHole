using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NBitcoin;
using NNostr.Client;
using WalletWasabi.Serialization;

namespace WalletWasabi.JoinMarket;

/// <summary>
/// How the offer of a market maker is told on Nostr: as an event of the kind for the discovery of coinjoins, with a type
/// of its own. The kind is replaceable, which means that a relay keeps the newest event of a key and forgets the ones before.
/// So a maker has one offer per key, posts it again to change it, and replaces it with an empty one to take it back.
/// </summary>
/// <remarks>
/// The tags are the ones that the kind has: <c>type</c> and <c>network</c>. Its third tag, <c>endpoint</c>, names a place
/// where a coinjoin is reached, and a maker has no such place yet. The content tells the offer as JSON:
/// <c>minFeeRate</c> in sat/vB, <c>bid</c> and <c>ask</c> in sats per anonymity point and <c>liquidity</c> in sats.
/// The coins of the maker are not told.
/// </remarks>
public static class MakerOfferEvent
{
	/// <summary>The kind of the events that announce where coinjoins can be had, see the draft "Nostr Coinjoin Discovery" (kind 15750).</summary>
	public const int Kind = 15750;

	/// <summary>What the <c>type</c> tag of the kind says for the offers of Black Hole.</summary>
	public const string Type = "BlackHoleJoinMarket";

	/// <summary>
	/// For how long an event of the kind counts. Whoever announces with it posts again before this time is over,
	/// to tell that it is still there.
	/// </summary>
	public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

	private const string TypeTag = "type";
	private const string NetworkTag = "network";

	private static readonly Encoder<MakerOffer> Encoder = offer =>
		Encode.Object([
			("minFeeRate", Encode.Decimal(offer.MinFeeRate.SatoshiPerByte)),
			("bid", Encode.Int64(offer.Bid.Satoshi)),
			("ask", Encode.Int64(offer.Ask.Satoshi)),
			("liquidity", Encode.Int64(offer.Liquidity.Satoshi))
		]);

	private static readonly Decoder<MakerOffer> Decoder =
		Decode.Object(get => new MakerOffer(
			new FeeRate(Math.Clamp(get.Required("minFeeRate", Decode.Decimal), 0m, 1_000_000m)),
			Money.Satoshis(get.Required("bid", Decode.Int64)),
			Money.Satoshis(get.Required("ask", Decode.Int64)),
			Money.Satoshis(get.Required("liquidity", Decode.Int64))));

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

	/// <summary>
	/// Reads the offer that an event tells on a network. It is for the caller to check who signed the event and how old it is.
	/// </summary>
	/// <returns>False if the event tells no offer of that network: it is of another kind, type or network, it takes an offer back, or what it tells is no offer.</returns>
	public static bool TryParse(NostrEvent nostrEvent, Network network, [NotNullWhen(true)] out MakerOffer? offer)
	{
		offer = null;

		if (nostrEvent.Kind != Kind
			|| GetTagValue(nostrEvent, TypeTag) != Type
			|| GetTagValue(nostrEvent, NetworkTag) != GetNetworkName(network)
			|| string.IsNullOrWhiteSpace(nostrEvent.Content))
		{
			return false;
		}

		try
		{
			var decoded = JsonDecoder.FromString(Decoder)(nostrEvent.Content);

			if (!decoded.IsOk || decoded.Value.Validate() is not null)
			{
				return false;
			}

			offer = decoded.Value;
			return true;
		}
		catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidOperationException)
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
		nostrEvent.Tags.FirstOrDefault(tag => tag.TagIdentifier == identifier)?.Data.FirstOrDefault();
}
