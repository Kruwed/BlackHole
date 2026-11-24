using System.IO;
using System.Text.Json;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;

namespace WalletWasabi.JoinMarket.Sample;

/// <summary>A coin of the sample wallet.</summary>
/// <param name="Id">The number of the coin in the sample wallet.</param>
/// <param name="TransactionId">The transaction that created the coin, as the sample names it. It is no transaction of any chain.</param>
/// <param name="Index">The place of the coin among the outputs of that transaction.</param>
/// <param name="Address">The address of the coin, as the sample names it. Nobody has its key.</param>
/// <param name="Amount">What the coin holds, in sats.</param>
/// <param name="Points">The anonymity points of the coin.</param>
public sealed record SampleCoin(int Id, string TransactionId, int Index, string Address, long Amount, decimal Points)
{
	public MarketCoin ToMarketCoin() => new(Id.ToString(System.Globalization.CultureInfo.InvariantCulture), TransactionId, Amount, Points);
}

/// <summary>
/// The sample data of the design of the market for anonymity points: an order book of 250 makers and a wallet of 16
/// coins. None of it is real. It is what the design was made and checked with, and it lets the market be looked at
/// and tried out while there are no offers, or no coins, to look at.
/// </summary>
public static class SampleMarket
{
	private const decimal SatsPerBtc = 100_000_000m;

	private static readonly Lazy<IReadOnlyList<BookMaker>> LazyMakers = new(LoadMakers);
	private static readonly Lazy<IReadOnlyList<SampleCoin>> LazyCoins = new(LoadCoins);

	/// <summary>The order book of the sample: 250 makers, 216 of them with a bond.</summary>
	public static IReadOnlyList<BookMaker> Makers => LazyMakers.Value;

	/// <summary>The wallet of the sample: 16 coins.</summary>
	public static IReadOnlyList<SampleCoin> Coins => LazyCoins.Value;

	/// <summary>The taker of the sample: two inputs that hold 0.1 BTC together.</summary>
	public static PlannedTaker Taker { get; } = new(InputCount: 2, Amount: 10_000_000);

	/// <summary>The height of the chain that the sample counts the lock of a bond from.</summary>
	public const uint ChainHeight = 971_284;

	/// <summary>Amounts of the sample are written in BTC with eight decimals.</summary>
	public static long ToSats(decimal btc) => (long)Math.Round(btc * SatsPerBtc, MidpointRounding.AwayFromZero);

	private static IReadOnlyList<BookMaker> LoadMakers()
	{
		using var document = Open("makers.json");

		return [.. document.RootElement.EnumerateArray().Select(maker =>
		{
			var inputCount = maker.GetProperty("k").GetInt32();

			return new BookMaker(
				maker.GetProperty("id").GetInt64(),
				maker.GetProperty("alias").GetString()!,
				maker.GetProperty("minFee").GetDecimal(),
				maker.GetProperty("buy").GetDecimal(),
				maker.GetProperty("sell").GetDecimal(),
				maker.GetProperty("spread").GetDecimal(),
				ToSats(maker.GetProperty("bond").GetDecimal()),
				ToSats(maker.GetProperty("liq").GetDecimal()),
				Math.Min(inputCount, MarketRules.MaxMatchedInputsPerMaker),
				Math.Max(0, inputCount - MarketRules.MaxMatchedInputsPerMaker),
				maker.GetProperty("pts").GetDecimal(),
				[.. maker.GetProperty("pay").EnumerateArray().Select(payment => ToSats(payment.GetDecimal()))]);
		})];
	}

	private static IReadOnlyList<SampleCoin> LoadCoins()
	{
		using var document = Open("utxos.json");

		return [.. document.RootElement.EnumerateArray().Select(coin => new SampleCoin(
			coin.GetProperty("id").GetInt32(),
			coin.GetProperty("txid").GetString()!,
			coin.GetProperty("vout").GetInt32(),
			coin.GetProperty("address").GetString()!,
			ToSats(coin.GetProperty("value").GetDecimal()),
			coin.GetProperty("ap").GetDecimal()))];
	}

	private static JsonDocument Open(string name)
	{
		using var stream = OpenResource(name);

		return JsonDocument.Parse(stream);
	}

	/// <summary>The sample file of the given name, as it is.</summary>
	public static Stream OpenResource(string name) =>
		typeof(SampleMarket).Assembly.GetManifestResourceStream($"WalletWasabi.JoinMarket.Sample.{name}")
			?? throw new InvalidOperationException($"The sample file {name} is missing.");
}
