using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using NBitcoin;
using NBitcoin.Secp256k1;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.WabiSabi.Client.Banning;
using WalletWasabi.WabiSabi.Client.CoinJoin.Client;

namespace WalletWasabi.Serialization;

public static partial class Encode
{
	public static JsonNode KeyPath(KeyPath kp) =>
		String(kp.ToString());

	public static JsonNode ByteArray(byte[] bytes) =>
		String(Convert.ToBase64String(bytes));

	public static JsonNode ChainCode(byte[] bytes) =>
		ByteArray(bytes);

	public static JsonNode PubKey(PubKey pk) =>
		String(pk.ToHex());

	public static JsonNode ECPubKey(ECPubKey pk) =>
		ByteArray(pk.ToBytes());

	public static JsonNode HdPubKey(HdPubKey hd) =>
		hd.TweakData is null
			? HdPubKeyEncoder(hd)
			: SpPubKeyEncoder(hd);

	public static JsonNode HdPubKeyEncoder(HdPubKey hd) =>
		Object([
			("PubKey", PubKey(hd.PubKey)),
			("FullKeyPath", KeyPath(hd.FullKeyPath) ),
			("HandedOut", Bool(hd.IsHandedOut) ),
			("KeyState", Int((int)hd.KeyState) ),
		]);

	public static JsonNode SpPubKeyEncoder(HdPubKey hd) =>
		Object([
			("PubKey", PubKey(hd.PubKey)),
			("TweakData", ECPubKey(hd.TweakData!))
		]);

	public static JsonNode HDFingerprint(HDFingerprint? fp)
	{
		if (fp is null)
		{
			throw new ArgumentNullException(nameof(fp));
		}

		return String(fp.Value.ToString());
	}

	public static JsonNode BitcoinEncryptedSecretNoEC(BitcoinEncryptedSecretNoEC s) =>
		String(s.ToWif());

	public static JsonNode WalletHeight(ChainHeight height) =>
		String(height.Height.ToString());

	public static JsonNode BlockchainState(BlockchainState s)
	{
		List<(string, JsonNode?)> properties = [("Network", Network(s.Network))];

		if (s.Height is { } height)
		{
			properties.Add(("Height", WalletHeight(height)));
		}

		if (s.BirthHeight is { } birthHeight)
		{
			properties.Add(("BirthHeight", UInt(birthHeight.Height)));
		}

		if (s.BlockHashes.Count > 0)
		{
			properties.Add(("BlockHashes", Array(s.BlockHashes.Select(UInt256))));
		}

		return Object(properties);
	}

	public static JsonNode SerializableException(SerializableException e) =>
		Object([
			("ExceptionType", Optional(e.ExceptionType, String)),
			("Message", String(e.Message)),
			("StackTrace", Optional(e.StackTrace, String)),
			("InnerException", Optional(e.InnerException, SerializableException))
		]);

	public static JsonNode PrisonedCoinRecord(PrisonedCoinRecord r) =>
		Object([
			("Outpoint", Outpoint(r.Outpoint)),
			("BannedUntil", DatetimeOffset(r.BannedUntil))
		]);

	public static JsonNode CoinjoinCosts(KeyValuePair<uint256, CoinjoinCosts> costs) =>
		Object([
			("TransactionId", UInt256(costs.Key)),
			("MiningFee", MoneySatoshis(costs.Value.MiningFee)),
			("WastedDust", MoneySatoshis(costs.Value.WastedDust)),
			("PaymentsTotal", MoneySatoshis(costs.Value.PaymentsTotal))
		]);

	public static JsonNode ClientPrison(IEnumerable<PrisonedCoinRecord> p) =>
		Array(p.Select(PrisonedCoinRecord));
}


public static partial class Decode
{
	public static Decoder<byte[]> ByteArray =>
		String.Map(Convert.FromBase64String);

	public static Decoder<HDFingerprint> HDFingerprint =>
		String.Map(NBitcoin.HDFingerprint.Parse);

	private static Decoder<ChainHeight> WalletHeight =>
		UInt.Map(h => new ChainHeight(h));

	public static Decoder<KeyPath> KeyPath =>
		String.Map(NBitcoin.KeyPath.Parse);

	private static Decoder<PubKey> PubKey =>
		String.Map(s => new PubKey(s));

	public static Decoder<ECPubKey> ECPubKey =>
		ByteArray.Map(a => NBitcoin.Secp256k1.ECPubKey.Create(a));

	private static Decoder<HdPubKey> HdPubKeyDecoder =>
		Object(get => new HdPubKey(
			get.Required("PubKey", PubKey),
			get.Required("FullKeyPath", KeyPath),
			get.Required("KeyState", Int.Map(x => (KeyState)x)),
			get.Optional("HandedOut", Bool, false)
		));

	private static Decoder<HdPubKey> SpPubKeyDecoder =>
		Object(get => new HdPubKey(
			get.Required("PubKey", PubKey),
			get.Required("FullKeyPath", KeyPath),
			get.Required("KeyState", Int.Map(x => (KeyState)x))
		));

	public static Decoder<HdPubKey> HdPubKey =>
		OneOf([HdPubKeyDecoder, SpPubKeyDecoder]);

	public static Decoder<BlockchainState> BlockchainState =>
		Object(get => new BlockchainState(
			get.Required("Network", Network),
			OptionalButValid(get, "Height", WalletHeight),
			OptionalButValid(get, "BirthHeight", WalletHeight),
			OptionalButValid(get, "BlockHashes", Array(UInt256))
			));

	/// <summary>
	/// Decodes a field that can be missing. A value that is there has to be valid: an unreadable height
	/// must not be taken for a wallet that has no height yet.
	/// </summary>
	private static T? OptionalButValid<T>(Getters get, string fieldName, Decoder<T> decoder) where T : class =>
		get.Value.ValueKind == JsonValueKind.Object
		&& get.Value.TryGetProperty(fieldName, out var property)
		&& property.ValueKind != JsonValueKind.Null
			? get.Required(fieldName, decoder)
			: null;

	public static Decoder<BitcoinEncryptedSecretNoEC> BitcoinEncryptedSecretNoEC =>
		String.Map(s => new BitcoinEncryptedSecretNoEC(s, NBitcoin.Network.Main));

	public static Decoder<SerializableException> SerializableException =>
		Object(get => new SerializableException(
			get.Required("ExceptionType", String),
			get.Required("Message", String),
			get.Required("StackTrace", String),
			get.Optional("InnerException", SerializableException)
		));

	public static Decoder<PrisonedCoinRecord> PrisonedCoinRecord =>
		Object(get => new PrisonedCoinRecord(
			get.Required("Outpoint", OutPoint),
			get.Required("BannedUntil", DateTimeOffset)
		));

	public static Decoder<KeyValuePair<uint256, CoinjoinCosts>> CoinjoinCosts =>
		Object(get => KeyValuePair.Create(
			get.Required("TransactionId", UInt256),
			new CoinjoinCosts(
				get.Required("MiningFee", MoneySatoshis),
				get.Required("WastedDust", MoneySatoshis),
				get.Required("PaymentsTotal", MoneySatoshis))
		));
}
