using NBitcoin;
using System.Linq;
using WabiSabi.Crypto;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Crypto.Randomness;
using WalletWasabi.Extensions;
using WalletWasabi.Helpers;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.WabiSabi.Client;
using WalletWasabi.WabiSabi.Client.Batching;
using WalletWasabi.WabiSabi.Client.CredentialDependencies;
using WalletWasabi.WabiSabi.Coordinator;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.WabiSabi.Client;

public class PaymentAwareOutputProviderTests
{
	[Fact]
	public void CreateOutputsForImpossiblePaymentTest()
	{
		var rpc = new MockRpcClient();
		var wallet = new TestWallet("random-wallet", rpc);
		var paymentBatch = new PaymentBatch(new CoinsRegistry());

		using Key key = new();
		paymentBatch.AddPayment(
			key.PubKey.GetAddress(ScriptPubKeyType.Segwit, rpc.Network),
			Money.Coins(101.0001m)); // Too big, non-standard payment which cannot be done.

		var roundParameters = WabiSabiFactory.CreateRoundParameters(new WabiSabiConfig());
		var registeredCoinsEffectiveValues = new[] { Money.Coins(100m) };
		var theirCoinEffectiveValues = new[] { Money.Coins(0.2m), Money.Coins(0.1m), Money.Coins(0.05m), Money.Coins(0.0025m), Money.Coins(0.0001m) };
		var availableVsize = roundParameters.MaxVsizeAllocationPerAlice - Constants.P2wpkhInputVirtualSize;

		var outputProvider = new PaymentAwareOutputProvider(wallet, paymentBatch, RandomnessProviders.Insecure);
		var outputs = outputProvider.GetOutputs(
			roundId: uint256.Zero,
			roundParameters,
			registeredInputs: [],
			registeredCoinsEffectiveValues,
			theirCoinEffectiveValues,
			availableVsize).ToArray();

		var nonAwaredOutputProvider = new OutputProvider(wallet, RandomnessProviders.Insecure);
		var decomposedOutputs = nonAwaredOutputProvider.GetOutputs(
			uint256.Zero,
			roundParameters,
			registeredInputs: [],
			registeredCoinsEffectiveValues,
			theirCoinEffectiveValues,
			availableVsize).ToArray();

		static decimal ToDecimal(TxOut o) => o.Value.ToDecimal(MoneyUnit.BTC);
		Assert.Equal(outputs.Sum(ToDecimal), decomposedOutputs.Sum(ToDecimal));

		// Make sure this doesn't throw
		var vsizes = Enumerable.Repeat(0L, int.MaxValue).Prepend(availableVsize);
		DependencyGraph.ResolveCredentialDependencies(registeredCoinsEffectiveValues, outputs, roundParameters.MiningFeeRate, vsizes, ProtocolConstants.MaxAmountPerAlice, ProtocolConstants.MaxVsizeCredentialValue);
	}

	/// <summary>
	/// The limit of the wallet reaches the outputs that are registered in a round: with a limit of zero the outputs and
	/// their fees take all that the inputs are worth, and the round can still issue the credentials for them. That holds
	/// for the provider that knows about payments as for the one that does not.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OutputsLeaveNoMoreThanTheWalletAllows(bool isPaymentAware)
	{
		var rpc = new MockRpcClient();
		var wallet = new TestWallet("random-wallet", rpc);
		var roundParameters = WabiSabiFactory.CreateRoundParameters(new WabiSabiConfig());
		var theirCoinEffectiveValues = new[] { Money.Coins(0.2m), Money.Coins(0.1m), Money.Coins(0.05m), Money.Coins(0.0025m), Money.Coins(0.0001m) };
		var availableVsize = roundParameters.MaxVsizeAllocationPerAlice - Constants.P2wpkhInputVirtualSize;
		var feeRate = roundParameters.MiningFeeRate;
		Money? maxLeftover = null;
		var outputProvider = isPaymentAware
			? new PaymentAwareOutputProvider(wallet, new PaymentBatch(new CoinsRegistry()), RandomnessProviders.Insecure, () => maxLeftover)
			: new OutputProvider(wallet, RandomnessProviders.Insecure, () => maxLeftover);
		var roundsWithLeftover = 0;

		foreach (var satoshis in new long[] { 12_345_678, 1_003_333, 777_777, 250_001, 99_999, 61_234, 5_000_123, 33_333_333 })
		{
			var registeredCoinsEffectiveValues = new[] { Money.Satoshis(satoshis) };

			TxOut[] GetOutputs() => outputProvider.GetOutputs(uint256.Zero, roundParameters, registeredInputs: [], registeredCoinsEffectiveValues, theirCoinEffectiveValues, availableVsize).ToArray();
			Money Leftover(TxOut[] outputs) => Money.Satoshis(satoshis) - outputs.Sum(x => x.Value + feeRate.GetFee(x.ScriptPubKey.EstimateOutputVsize()));

			maxLeftover = null;
			if (Leftover(GetOutputs()) > Money.Zero)
			{
				roundsWithLeftover++;
			}

			// The limit is read for every round: it applies without making the provider anew.
			maxLeftover = Money.Zero;
			var outputs = GetOutputs();
			Assert.Equal(Money.Zero, Leftover(outputs));

			var vsizes = Enumerable.Repeat(0L, int.MaxValue).Prepend(availableVsize);
			DependencyGraph.ResolveCredentialDependencies(registeredCoinsEffectiveValues, outputs, feeRate, vsizes, ProtocolConstants.MaxAmountPerAlice, ProtocolConstants.MaxVsizeCredentialValue);
		}

		Assert.True(roundsWithLeftover > 0, "No round left anything over without the limit, so the limit was not put to the test.");
	}

	[Theory]
	[InlineData("0.00484323")]
	[InlineData("0.007")]
	[InlineData("0.007123")]
	[InlineData("0.00555")]
	[InlineData("0.001, 0.001, 0.001, 0.001, 0.001, 0.001")]
	public void CreateOutputsForPaymentsTest(string testData)
	{
		var payments = testData.Split(",").Select(decimal.Parse).ToArray();
		var rpc = new MockRpcClient();
		var wallet = new TestWallet("random-wallet", rpc);
		var paymentBatch = new PaymentBatch(new CoinsRegistry());
		var outputProvider = new PaymentAwareOutputProvider(wallet, paymentBatch, RandomnessProviders.Insecure);

		var roundParameters = WabiSabiFactory.CreateRoundParameters(new WabiSabiConfig());

		var scriptPubKeys = payments.Select(payment =>
		{
			using Key key = new();
			var scriptPubKey = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, rpc.Network);
			paymentBatch.AddPayment(scriptPubKey, Money.Coins(payment));
			return scriptPubKey;
		}).ToArray();

		var registeredCoinsEffectiveValues = new[] { Money.Coins(0.00484323m), Money.Coins(0.003m), Money.Coins(0.00004323m) };
		var totalRegisteredEffectiveValue = registeredCoinsEffectiveValues.Sum().ToDecimal(MoneyUnit.BTC);

		var outputs = outputProvider.GetOutputs(
			roundId: uint256.Zero,
			roundParameters,
			registeredInputs: [],
			registeredCoinsEffectiveValues,
			new[] { Money.Coins(0.2m), Money.Coins(0.1m), Money.Coins(0.05m), Money.Coins(0.0025m), Money.Coins(0.0001m) },
			int.MaxValue).ToArray();

		Assert.All(payments.Take(4).Zip(scriptPubKeys, outputs, (x, y, z) => (Payment: x, Destination: y, Output: z)), x =>
		{
			Assert.Equal(x.Output.ScriptPubKey, x.Destination.ScriptPubKey);
			Assert.Equal(x.Output.Value, Money.Coins(x.Payment));
		});

		Assert.True(outputs.Length >= 2, $"There were {outputs.Length} outputs."); // The rest was decomposed
		Assert.InRange(outputs.Sum(x => x.Value.ToDecimal(MoneyUnit.BTC)),
			totalRegisteredEffectiveValue - 0.00025m,
			totalRegisteredEffectiveValue); // no money was lost
	}

	[Theory]
	[InlineData(new[] { "0.2", "0.30" }, "0.176", 1_000, 0)] // Not enough money to make any of the payments.
	[InlineData(new[] { "0.1", "0.30" }, "0.176", 1_000, 1)] // It is only possible to make one payment.
	[InlineData(new[] { "0.1", "0.05" }, "0.176", 1_000, 2)] // It is possible to make the two payments.
	[InlineData(new[] { "0.1", "0.05" }, "0.150", 1_000, 1)] // It is only possible to make one payment. Not enough for fees.
	[InlineData(new[] { "0.1", "0.05", "0.025", "0.001", "0.001" }, "0.176", 1_000, 4)] // Four is the maximum number of payments.
	[InlineData(new[] { "0.1", "0.30" }, "0.176", 43 + 31, 1)] // It is only possible to make one payment.
	[InlineData(new[] { "0.1", "0.05" }, "0.176", 43 + 31, 1)] // It is possible to make the two payments.
	[InlineData(new[] { "0.1", "0.05" }, "0.176", 20, 0)] // It is possible to make the two payments.
	[InlineData(new[] { "0.1", "0.05" }, "0.14", 40 + 31, 0)] // Not enough vsize to register the payment and the change.
	public void BestPaymentSetTest(string[] amountsToPay, string availableAmountStr, int availableVsize, int expectedOutputs)
	{
		var roundParameters = WabiSabiFactory.CreateRoundParameters(new WabiSabiConfig());
		var paymentBatch = new PaymentBatch(new CoinsRegistry());

		var payments = amountsToPay.Select(a => (Destination: BitcoinFactory.CreateSegwitAddress(), Amount: Money.Coins(decimal.Parse(a))));
		payments.ToList().ForEach(p => paymentBatch.AddPayment(p.Destination, p.Amount));

		var availableMoney = Money.Coins(decimal.Parse(availableAmountStr));
		var paymentSet = paymentBatch.GetBestPaymentSet(availableMoney, availableVsize, roundParameters, registeredInputs: []);

		Assert.True(paymentSet.TotalAmount < availableMoney);
		Assert.True(paymentSet.TotalVSize < availableVsize);
		Assert.Equal(expectedOutputs, paymentSet.Payments.Count());
	}
}
