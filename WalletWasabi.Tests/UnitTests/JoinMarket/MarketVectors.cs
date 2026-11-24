using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WalletWasabi.JoinMarket.OrderBook;
using WalletWasabi.JoinMarket.Planning;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.JoinMarket;

/// <summary>
/// The expected results of the design of the market for anonymity points: whole plans, cases for the searches of the
/// planner, and what the order book and the maker's side show on the sample data. They were taken from the prototype
/// of the design and are not to be edited: a case that fails means that the code is wrong.
/// </summary>
internal static class MarketVectors
{
	/// <summary>How far two numbers that are not whole may be apart, relative to the larger of them and to one.</summary>
	public const decimal Tolerance = 1e-9m;

	public static string Root => Path.Combine(AppContext.BaseDirectory, "UnitTests", "JoinMarket", "Vectors");

	/// <summary>The names of the files of the whole plans.</summary>
	public static IEnumerable<string> PlanFiles =>
		Directory.GetFiles(Path.Combine(Root, "planner"), "plan-*.json")
			.Select(path => Path.GetFileName(path)!)
			.Order(StringComparer.Ordinal);

	public static JsonDocument Open(params string[] path) =>
		JsonDocument.Parse(File.ReadAllText(Path.Combine([Root, .. path])));

	public static TheoryData<string> PlanFileData()
	{
		var data = new TheoryData<string>();

		foreach (var file in PlanFiles)
		{
			data.Add(file);
		}

		return data;
	}

	/// <summary>The places of the cases of a file, so that every case is a test of its own.</summary>
	public static TheoryData<int> CaseIndexes(string folder, string file, string property = "cases")
	{
		using var document = Open(folder, file);
		var data = new TheoryData<int>();

		for (var i = 0; i < document.RootElement.GetProperty(property).GetArrayLength(); i++)
		{
			data.Add(i);
		}

		return data;
	}

	public static void AssertClose(decimal expected, decimal actual, string what)
	{
		var limit = Tolerance * Math.Max(1, Math.Max(Math.Abs(expected), Math.Abs(actual)));

		Assert.True(Math.Abs(expected - actual) <= limit, $"{what}: expected {expected}, but it is {actual}.");
	}

	public static void AssertClose(double expected, double actual, string what)
	{
		var limit = 1e-9 * Math.Max(1, Math.Max(Math.Abs(expected), Math.Abs(actual)));

		Assert.True(Math.Abs(expected - actual) <= limit, $"{what}: expected {expected:R}, but it is {actual:R}.");
	}

	public static long[] Longs(JsonElement array) => [.. array.EnumerateArray().Select(element => element.GetInt64())];

	public static int[] Ints(JsonElement array) => [.. array.EnumerateArray().Select(element => element.GetInt32())];

	/// <summary>The taker and the makers of a whole plan, the makers in the order in which the planner goes through them.</summary>
	public static (PlannedTaker Taker, PlannedMaker[] Makers) ReadPlanInput(JsonElement input)
	{
		var taker = input.GetProperty("taker");

		return (
			new PlannedTaker(taker.GetProperty("inputs").GetInt32(), taker.GetProperty("amountSats").GetInt64()),
			[.. input.GetProperty("makers").EnumerateArray().Select(maker => PlannedMaker.FromInputCount(
				maker.GetProperty("alias").GetString()!,
				maker.GetProperty("minFee").GetDecimal(),
				maker.GetProperty("buy").GetDecimal(),
				maker.GetProperty("sell").GetDecimal(),
				maker.GetProperty("pts").GetDecimal(),
				maker.GetProperty("k").GetInt32(),
				maker.GetProperty("liqSats").GetInt64(),
				Longs(maker.GetProperty("paySats"))))]);
	}

	/// <summary>The criterion that the files of the design name by a short key.</summary>
	public static BookCriterion ReadCriterion(string key) =>
		key switch
		{
			"minFee" => BookCriterion.FeeRate,
			"spread" => BookCriterion.Spread,
			"buy" => BookCriterion.BuyRate,
			"sell" => BookCriterion.SellRate,
			"bond" => BookCriterion.Bond,
			"liq" => BookCriterion.Liquidity,
			_ => throw new ArgumentOutOfRangeException(nameof(key), key, "The design has no such criterion.")
		};

	/// <summary>The options of a search, whose functions the files tell as tables.</summary>
	public static DecomposeOptions ReadOptions(JsonElement options)
	{
		var credit = options.GetProperty("credit").EnumerateArray().Select(element => element.GetDouble()).ToArray();
		var bonus = ReadTable(options.GetProperty("bonus"));
		var popularity = ReadTable(options.GetProperty("pop"));
		var hasExisting = options.TryGetProperty("existing", out var existing) && existing.ValueKind == JsonValueKind.Array;

		return new DecomposeOptions
		{
			FeeRate = options.GetProperty("f").GetDouble(),
			MaxOutputs = options.GetProperty("maxOuts").GetInt32(),
			Branch = Ints(options.GetProperty("branch")),
			ForfeitMax = options.GetProperty("forfeitMax").GetDouble(),
			Credit = place => place < credit.Length ? credit[place] : 0,
			Bonus = value => bonus.GetValueOrDefault(value),
			Popularity = value => popularity.GetValueOrDefault(value),
			ChangeVb = ReadStepped(options.GetProperty("changeVb"))!,
			CreditChange = options.TryGetProperty("creditChange", out var creditChange) ? ReadStepped(creditChange) : null,
			Exclude = options.TryGetProperty("exclude", out var exclude) && exclude.ValueKind == JsonValueKind.Array ? Longs(exclude).ToHashSet() : null,
			Existing = hasExisting ? Longs(existing) : null,
			FitWindow = hasExisting ? options.GetProperty("fitWindow").GetDouble() : null,
			FitLimit = hasExisting && options.TryGetProperty("fitLimit", out var fitLimit) && fitLimit.ValueKind == JsonValueKind.Number ? fitLimit.GetInt32() : null,
			FitMax = hasExisting && options.TryGetProperty("fitMax", out var fitMax) && fitMax.ValueKind == JsonValueKind.Number ? fitMax.GetInt32() : null
		};
	}

	private static Dictionary<long, double> ReadTable(JsonElement table) =>
		table.EnumerateObject().ToDictionary(entry => long.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture), entry => entry.Value.GetDouble());

	/// <summary>A number, or one number up to a value and another one beyond it. Null if the file has neither.</summary>
	private static Func<long, double>? ReadStepped(JsonElement stepped)
	{
		switch (stepped.ValueKind)
		{
			case JsonValueKind.Number:
				var constant = stepped.GetDouble();
				return _ => constant;

			case JsonValueKind.Object:
				var upTo = stepped.GetProperty("upTo").GetDouble();
				var then = stepped.GetProperty("then").GetDouble();
				var otherwise = stepped.GetProperty("else").GetDouble();
				return left => left <= upTo ? then : otherwise;

			default:
				return null;
		}
	}
}
