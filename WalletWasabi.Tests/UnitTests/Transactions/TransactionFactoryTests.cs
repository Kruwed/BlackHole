using NBitcoin;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Exceptions;
using WalletWasabi.Extensions;
using WalletWasabi.Models;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Wallets;
using WalletWasabi.Wallets.SilentPayment;
using Xunit;
using System.Collections.Immutable;

namespace WalletWasabi.Tests.UnitTests.Transactions;

public class TransactionFactoryTests
{
	[Fact]
	public void InsufficientBalance()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: true),
				(1, 0.02m, confirmed: true)
			});

		// We try to spend 100btc but we only have 0.03
		var amount = Money.Coins(100m);
		using Key key = new();
		var payment = new PaymentIntent(key, amount);

		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var ex = Assert.Throws<InsufficientBalanceException>(() => transactionFactory.BuildTransaction(txParameters));

		Assert.Equal(ex.Minimum, amount);
		Assert.Equal(ex.Actual, transactionFactory.Coins.Select(x => x.Amount).Sum());
	}

	[Fact]
	public void TooMuchFeePaid()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.0001m, confirmed: true)
			});

		using Key key = new();
		var payment = new PaymentIntent(key, MoneyRequest.CreateAllRemaining(subtractFee: true));

		// A Taproot input and a P2PKH output make 103 vbytes: 48.544 sat/vb take exactly half of the 10 000 sats.
		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(48.544m).Build();
		var result = transactionFactory.BuildTransaction(txParameters);
		var output = Assert.Single(result.OuterWalletOutputs);
		Assert.Equal(Money.Satoshis(5000), result.Fee);
		Assert.Equal(result.Fee, output.Amount); // edge case! paid amount equal to paid fee

		// The transaction cost is higher than the intended payment.
		txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(50m).Build();
		Assert.Throws<TransactionFeeOverpaymentException>(() => transactionFactory.BuildTransaction(txParameters));

		// self spend case
		var ownKey = transactionFactory.KeyManager.GetNextReceiveKey();
		var selfSpendPayment = new PaymentIntent(ownKey.GetAddress(transactionFactory.Network), MoneyRequest.CreateAllRemaining(subtractFee: true));
		txParameters = CreateBuilder().SetPayment(selfSpendPayment).SetFeeRate(50m).Build();
		Assert.Throws<TransactionFeeOverpaymentException>(() => transactionFactory.BuildTransaction(txParameters));
	}

	/// <summary>
	/// The wallet prefers no coin for what it is. Of the coins that are enough for the payment, it takes the one that
	/// is closest to the amount.
	/// </summary>
	[Fact]
	public void SelectTheCoinClosestToTheAmount()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.08m, confirmed: true),
				(1, 0.16m, confirmed: true)
			});

		using Key key = new();
		var payment = new PaymentIntent(key, Money.Coins(0.07m));
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		var spentCoin = Assert.Single(result.SpentCoins);
		Assert.Equal(Money.Coins(0.08m), spentCoin.Amount);
		Assert.False(result.SpendsUnconfirmed);
		var tx = result.Transaction.Transaction;
		Assert.Equal(2, tx.Outputs.Count);

		var changeCoin = Assert.Single(result.InnerWalletOutputs);
		Assert.True(changeCoin.HdPubKey.IsInternal);
	}

	/// <summary>
	/// Coins that nothing links to each other are spent together only as far as the payment needs it: as few of them as
	/// possible, and of those the ones closest to the amount.
	/// </summary>
	[Fact]
	public void SelectAsFewUnlinkedCoinsAsThePaymentNeeds()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: true),
				(1, 0.02m, confirmed: true),
				(2, 0.04m, confirmed: true),
				(3, 0.08m, confirmed: true),
				(4, 0.16m, confirmed: true)
			});

		using Key key = new();
		var payment = new PaymentIntent(key, Money.Coins(0.17m));
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		// 0.16 and 0.01 would not pay for the fee as well.
		Assert.Equal([Money.Coins(0.02m), Money.Coins(0.16m)], result.SpentCoins.Select(x => x.Amount).Order());
		Assert.Single(result.OuterWalletOutputs);
		Assert.False(result.SpendsUnconfirmed);

		var tx = result.Transaction.Transaction;
		Assert.Equal(2, tx.Outputs.Count);
	}

	[Fact]
	public void SelectSameScriptPubKeyCoins()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: false),
				(1, 0.02m, confirmed: false),
				(1, 0.04m, confirmed: true),
				(2, 0.08m, confirmed: true)
			});

		// Selecting 0.08 + 0.04 should be enough but it has to select 0.02 too because it is the same address
		using Key key = new();
		var payment = new PaymentIntent(key, Money.Coins(0.1m));
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.True(result.SpendsUnconfirmed);
		Assert.Equal(3, result.SpentCoins.Count());
		Assert.Equal(Money.Coins(0.14m), result.SpentCoins.Select(x => x.Amount).Sum());

		var changeCoin = Assert.Single(result.InnerWalletOutputs);

		var tx = result.Transaction.Transaction;

		// It must select the unconfirmed coin of the same address too.
		Assert.True(result.SpendsUnconfirmed);
		Assert.Equal(2, tx.Outputs.Count);
	}

	[Fact]
	public async Task SelectSameClusterCoinsAsync()
	{
		var password = "foo";
		var keyManager = ServiceFactory.CreateKeyManager(password);

		HdPubKey NewKey() => keyManager.GenerateNewKey(KeyState.Used, true);
		var sCoins = Enumerable.Range(0, 10)
			.Select(_ => BitcoinFactory.CreateSmartCoin(NewKey(), 0.9m))
			.ToArray();

		// cluster 1 holds seven coins
		var coinsCluster1 = new[] { sCoins[0], sCoins[1], sCoins[2], sCoins[3], sCoins[4], sCoins[5], sCoins[6] };
		var cluster1 = new Cluster(coinsCluster1.Select(x => x.HdPubKey).ToImmutableHashSet());
		foreach (var coin in coinsCluster1)
		{
			coin.HdPubKey.Cluster = cluster1;
		}

		// cluster 2 holds three coins
		var coinsCluster2 = new[] { sCoins[7], sCoins[8], sCoins[9] };
		var cluster2 = new Cluster(coinsCluster2.Select(x => x.HdPubKey).ToImmutableHashSet());
		foreach (var coin in coinsCluster2)
		{
			coin.HdPubKey.Cluster = cluster2;
		}

		var coinsView = new CoinsView(sCoins.ToArray());
		var transactionFactory = new TransactionFactory(Network.Main, keyManager, coinsView, password);

		// Two 0.9btc coins are enough: they come from the cluster that is the closest to the amount
		using Key key1 = new();
		var payment = new PaymentIntent(key1, Money.Coins(1.75m));
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(2, result.SpentCoins.Count());
		Assert.All(result.SpentCoins, c => Assert.Equal(c.HdPubKey.Cluster, cluster2));
		Assert.Contains(sCoins[7], result.SpentCoins);
		Assert.Contains(sCoins[8], result.SpentCoins);

		// Three 0.9btc coins are enough
		using Key key2 = new();
		payment = new PaymentIntent(key2, Money.Coins(1.85m));
		txParameters = CreateBuilder().SetPayment(payment).Build();
		result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(3, result.SpentCoins.Count());
		Assert.All(result.SpentCoins, c => Assert.Equal(c.HdPubKey.Cluster, cluster2));
		Assert.Contains(sCoins[7], result.SpentCoins);
		Assert.Contains(sCoins[8], result.SpentCoins);
		Assert.Contains(sCoins[9], result.SpentCoins);

		// Four 0.9btc coins are enough but this time the smaller cluster is NOT enough
		// That's why it has to use the coins in the cluster number 1
		using Key key3 = new();
		payment = new PaymentIntent(key3, Money.Coins(3.5m));
		txParameters = CreateBuilder().SetPayment(payment).Build();
		result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(4, result.SpentCoins.Count());
		Assert.All(result.SpentCoins, c => Assert.Equal(c.HdPubKey.Cluster, cluster1));
		Assert.Contains(sCoins[0], result.SpentCoins);
		Assert.Contains(sCoins[1], result.SpentCoins);
		Assert.Contains(sCoins[2], result.SpentCoins);
		Assert.Contains(sCoins[3], result.SpentCoins);

		// Nine 0.9btc coins are enough but there is no cluster big enough
		// That's why it has to use the coins from both the clusters
		using Key key = new();
		payment = new PaymentIntent(key, Money.Coins(7.4m));
		txParameters = CreateBuilder().SetPayment(payment).Build();
		result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(9, result.SpentCoins.Count());
	}

	[Fact]
	public void CustomChangeScript()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 1m, confirmed: true)
			});

		using Key key = new();
		using Key changeKey = new();

		var payment = new PaymentIntent(new[]
		{
			new DestinationRequest(key.PubKey, Money.Coins(0.1m)),
			new DestinationRequest(changeKey.PubKey, MoneyRequest.CreateChange(subtractFee: true))
		});
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Single(result.SpentCoins);
		Assert.Equal(Money.Coins(1m), result.SpentCoins.Select(x => x.Amount).Sum());

		var tx = result.Transaction.Transaction;
		Assert.Equal(2, tx.Outputs.Count);

		var changeOutput = Assert.Single<TxOut>(tx.Outputs, x => x.ScriptPubKey == changeKey.PubKey.ScriptPubKey);
		Assert.False(transactionFactory.KeyManager.TryGetKeyForScriptPubKey(changeOutput.ScriptPubKey, out _));
		Assert.Equal(Money.Coins(0.9m), changeOutput.Value + result.Fee);
	}

	[Fact]
	public void SubtractFeeFromSpecificOutput()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 1m, confirmed: true)
			});

		using Key key1 = new();
		using Key key2 = new();
		using Key key3 = new();

		var payment = new PaymentIntent(new[]
		{
			new DestinationRequest(key1, Money.Coins(0.3m)),
			new DestinationRequest(key2, Money.Coins(0.3m), subtractFee: true),
			new DestinationRequest(key3, Money.Coins(0.3m))
		});
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		var spentCoin = Assert.Single(result.SpentCoins);
		Assert.Equal(Money.Coins(1m), spentCoin.Amount);

		var tx = result.Transaction.Transaction;
		Assert.Equal(4, tx.Outputs.Count);

		var destination2Output = Assert.Single<TxOut>(tx.Outputs, x => x.ScriptPubKey == key2.GetScriptPubKey(ScriptPubKeyType.Legacy));
		Assert.Equal(Money.Coins(0.3m), destination2Output.Value + result.Fee);

		var changeOutput = Assert.Single(tx.Outputs, x => transactionFactory.KeyManager.TryGetKeyForScriptPubKey(x.ScriptPubKey, out _));
		Assert.Equal(Money.Coins(0.1m), changeOutput.Value);
	}

	[Fact]
	public void SubtractFeeFromTooSmallOutput()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 1m, confirmed: true)
			});

		using Key key1 = new();
		using Key key2 = new();
		using Key key3 = new();

		var payment = new PaymentIntent(new[]
		{
			new DestinationRequest(key1, Money.Coins(0.3m)),
			new DestinationRequest(key2, Money.Coins(0.00001m), subtractFee: true),
			new DestinationRequest(key3, Money.Coins(0.3m))
		});
		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(20m).Build();
		var ex = Assert.Throws<OutputTooSmallException>(() => transactionFactory.BuildTransaction(txParameters));

		// A Taproot input, three P2PKH outputs and a Taproot change make 214 vbytes: 4280 sats do not fit into an output of 1000 sats.
		Assert.Equal(Money.Satoshis(3280), ex.Value.Abs());
	}

	[Fact]
	public void MultiplePaymentsToSameAddress()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 1m, confirmed: true)
			});

		using Key key = new();
		var destination = key.PubKey;
		var payment = new PaymentIntent(new[]
		{
			new DestinationRequest(destination, Money.Coins(0.3m)),
			new DestinationRequest(destination, Money.Coins(0.3m), subtractFee: true),
			new DestinationRequest(destination, Money.Coins(0.3m))
		});
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		var spentCoin = Assert.Single(result.SpentCoins);
		Assert.Equal(Money.Coins(1m), spentCoin.Amount);

		var tx = result.Transaction.Transaction;
		Assert.Equal(2, tx.Outputs.Count); // consolidates same address payment

		var destinationOutput = Assert.Single(result.OuterWalletOutputs);
		Assert.Equal(destination.ScriptPubKey, destinationOutput.ScriptPubKey);
		Assert.Equal(Money.Coins(0.9m), destinationOutput.Amount + result.Fee);

		var changeOutput = Assert.Single(result.InnerWalletOutputs);
		Assert.Equal(Money.Coins(0.1m), changeOutput.Amount);
	}

	[Fact]
	public void SendAbsolutelyAllCoins()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.5m, confirmed: false),
				(1, 0.4m, confirmed: true),
				(2, 0.3m, confirmed: false),
				(3, 0.2m, confirmed: true)
			});

		using Key key = new();
		var destination = key;
		var payment = new PaymentIntent(new[]
		{
			new DestinationRequest(destination, MoneyRequest.CreateAllRemaining(subtractFee: true))
		});
		var txParameters = CreateBuilder().SetPayment(payment).Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(Money.Coins(1.4m), result.SpentCoins.Select(x => x.Amount).Sum());

		var tx = result.Transaction.Transaction;
		Assert.Single(tx.Outputs);
	}

	[Fact]
	public void SpendOnlyAllowedCoins()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: false),
				(1, 0.02m, confirmed: false),
				(2, 0.04m, confirmed: true),
				(3, 0.08m, confirmed: true)
			});

		using Key key = new();
		var payment = new PaymentIntent(key, Money.Coins(0.095m));
		var coins = transactionFactory.Coins;
		var allowedCoins = new[]
		{
			coins.Single(x => x.Amount == Money.Coins(0.08m)),
			coins.Single(x => x.Amount == Money.Coins(0.04m))
		}.ToArray();

		var txParameters = CreateBuilder()
			.SetPayment(payment)
			.SetAllowedInputs(allowedCoins.Select(x => x.Outpoint))
			.Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(Money.Coins(0.12m), result.SpentCoins.Select(x => x.Amount).Sum());
		Assert.Equal(2, result.SpentCoins.Count());
		Assert.Contains(allowedCoins[0], result.SpentCoins);
		Assert.Contains(allowedCoins[1], result.SpentCoins);
	}

	[Fact]
	public void SpendWholeAllowedCoins()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: false),
				(1, 0.02m, confirmed: false),
				(2, 0.04m, confirmed: true),
				(3, 0.08m, confirmed: true)
			});

		using Key key = new();
		var destination = key.PubKey;
		var payment = new PaymentIntent(destination, MoneyRequest.CreateAllRemaining(subtractFee: true));
		var coins = transactionFactory.Coins;
		var allowedCoins = new[]
		{
			coins.Single(x => x.Amount == Money.Coins(0.01m)),
			coins.Single(x => x.Amount == Money.Coins(0.08m)),
			coins.Single(x => x.Amount == Money.Coins(0.04m))
		}.ToArray();

		var txParameters = CreateBuilder()
			.SetPayment(payment)
			.SetAllowedInputs(allowedCoins.Select(x => x.Outpoint))
			.Build();
		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(Money.Coins(0.13m), result.SpentCoins.Select(x => x.Amount).Sum());
		Assert.Equal(3, result.SpentCoins.Count());
		Assert.Contains(allowedCoins[0], result.SpentCoins);
		Assert.Contains(allowedCoins[1], result.SpentCoins);

		var tx = result.Transaction.Transaction;
		Assert.Single(tx.Outputs);

		var destinationOutput = Assert.Single(tx.Outputs, x => x.ScriptPubKey == destination.ScriptPubKey);
		Assert.Equal(Money.Coins(0.13m), destinationOutput.Value + result.Fee);
	}

	[Fact]
	public void InsufficientAllowedCoins()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: true),
				(1, 0.08m, confirmed: true)
			});

		var allowedCoins = new[]
		{
			transactionFactory.Coins.Single(x => x.Amount == Money.Coins(0.01m))
		}.ToArray();

		var amount = Money.Coins(0.5m); // it is not enough
		using Key key = new();
		var payment = new PaymentIntent(key, amount);

		var txParameters = CreateBuilder()
			.SetPayment(payment)
			.SetAllowedInputs(allowedCoins.Select(x => x.Outpoint))
			.Build();

		var ex = Assert.Throws<InsufficientBalanceException>(() => transactionFactory.BuildTransaction(txParameters));

		Assert.Equal(ex.Minimum, amount);
		Assert.Equal(ex.Actual, allowedCoins[0].Amount);
	}

	[Fact]
	public void SpendWholeCoinsEvenWhenNotAllowed()
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.01m, confirmed: false),
				(1, 0.02m, confirmed: false),
				(1, 0.04m, confirmed: true),
				(2, 0.08m, confirmed: true)
			});

		// Selecting 0.08 + 0.02 should be enough but it has to select 0.02 too because it is the same address
		using Key key = new();
		var payment = new PaymentIntent(key, Money.Coins(0.095m));
		var coins = transactionFactory.Coins;

		// the allowed coins contain enough money but one of those has the same script that
		// one unselected coins. That unselected coin has to be spent too.
		var allowedInputs = new[]
		{
			coins.Single(x => x.Amount == Money.Coins(0.08m)).Outpoint,
			coins.Single(x => x.Amount == Money.Coins(0.02m)).Outpoint
		}.ToArray();

		var txParameters = CreateBuilder()
			.SetPayment(payment)
			.SetAllowedInputs(allowedInputs)
			.Build();

		var result = transactionFactory.BuildTransaction(txParameters);

		Assert.Equal(Money.Coins(0.14m), result.SpentCoins.Select(x => x.Amount).Sum());
		Assert.Equal(3, result.SpentCoins.Count());
		var sameAddressCoins = coins.GroupBy(x => x.ScriptPubKey).Single(x => x.Count() == 2).ToArray();
		Assert.Contains(sameAddressCoins[0], result.SpentCoins);
		Assert.Contains(sameAddressCoins[1], result.SpentCoins);
	}

	/// <summary>
	/// Tests that we throw <see cref="TransactionSizeException"/> when NBitcoin returns a coin selection whose sum is lower than the desired one.
	/// This can happen because bitcoin transactions can have only a limited number of coin inputs because of the transaction size limit.
	/// </summary>
	/// Translation: More stupid test ever! This is ruining our productivity by taking 1 minute to run.
	[Fact]
	public void TooManyInputCoins()
	{
		Money paymentAmount = Money.Coins(0.29943925m);

		using Key key = new();
		TransactionFactory transactionFactory = ServiceFactory.CreateTransactionFactory(DemoCoinSets.LotOfCoins);

		PaymentIntent payment = new(key, MoneyRequest.Create(paymentAmount));
		Assert.Equal(ChangeStrategy.Auto, payment.ChangeStrategy);

		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(12m).Build();
		TransactionSizeException ex = Assert.Throws<TransactionSizeException>(() => transactionFactory.BuildTransaction(txParameters));
		Assert.Equal(paymentAmount, ex.Target);
		Assert.Equal(Money.Coins(0.26498776m), ex.MaximumPossible);
	}

	[Fact]
	public void TransactionSizeExceptionFailingTest()
	{
		Money paymentAmount = Money.Coins(0.5m);

		using Key key = new();

		var coins = new[]
		{
			(0, 0.00118098m, true),
			(1, 0.02000000m, true),
			(2, 0.00008192m, true),
			(3, 0.00005000m, true),
			(4, 0.02000000m, true),
			(5, 0.02000000m, true),
			(6, 0.00531441m, true),
			(7, 0.02000000m, true),
			(8, 0.02000000m, true),
			(9, 0.02000000m, true),
			(10, 0.00531441m, true),
			(11, 0.02000000m, true),
			(12, 0.00006561m, true),
			(13, 0.00354294m, true),
			(14, 0.00020000m, true),
			(15, 0.00354294m, true),
			(16, 0.02097152m, true),
			(17, 0.00006561m, true),
			(18, 0.02097152m, true),
			(19, 0.00006561m, true),
			(20, 0.00006561m, true),
			(21, 0.04782969m, true),
			(22, 0.00005000m, true),
			(23, 0.00006561m, true),
			(24, 0.02097152m, true),
			(25, 0.02097152m, true),
			(26, 0.04782847m, true),
			(27, 0.00158637m, true),
			(28, 0.00100000m, true),
			(29, 0.00008192m, true),
			(30, 0.00006561m, true),
			(31, 0.02097152m, true),
			(32, 0.00006561m, true),
			(33, 0.00005000m, true),
			(34, 0.00354294m, true),
			(35, 0.00006561m, true),
			(36, 0.00065536m, true),
			(37, 0.00005000m, true),
			(38, 0.02000000m, true),
			(39, 0.00006561m, true),
			(40, 0.00006561m, true),
			(41, 0.00005000m, true),
			(42, 0.02097152m, true),
			(43, 0.02097152m, true),
			(44, 0.00065536m, true),
			(45, 0.02097152m, true),
			(46, 0.00006561m, true),
			(47, 0.00531441m, true),
			(48, 0.00006561m, true),
			(49, 0.02097152m, true),
			(50, 0.00013122m, true),
			(51, 0.00006561m, true),
			(52, 0.02000000m, true),
			(53, 0.02000000m, true),
			(54, 0.00050000m, true),
			(55, 0.00005648m, true),
			(56, 0.00020000m, true),
			(57, 0.00006561m, true),
			(58, 0.02097152m, true),
			(59, 0.02000000m, true),
			(60, 0.00006561m, true),
			(61, 0.02097152m, true),
			(62, 0.02000000m, true),
			(63, 0.00008192m, true),
			(64, 0.02097152m, true),
			(65, 0.00006561m, true),
			(66, 0.00005000m, true),
			(67, 0.04782969m, true),
			(68, 0.02000000m, true),
			(69, 0.00006019m, true),
			(70, 0.00006561m, true),
			(71, 0.02000000m, true),
			(72, 0.02097152m, true),
			(73, 0.02000000m, true),
			(74, 0.00006561m, true),
			(75, 0.00354294m, true),
			(76, 0.15804864m, true),
			(77, 0.02097152m, true),
			(78, 0.00006561m, true),
			(79, 0.00200000m, true),
			(80, 0.00005892m, true),
			(81, 0.00006292m, true)
		};

		TransactionFactory transactionFactory = ServiceFactory.CreateTransactionFactory(coins);

		PaymentIntent payment = new(key, MoneyRequest.Create(paymentAmount));
		Assert.Equal(ChangeStrategy.Auto, payment.ChangeStrategy);

		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(7703m).Build();
		transactionFactory.BuildTransaction(txParameters);
	}

	[Fact]
	public void SelectLockTimeForTransaction()
	{
		var lockTimeZero = uint.MaxValue;
		var samplingSize = 10_000;

		var dictionary = Enumerable.Range(-99, 101).ToDictionary(x => (uint)x, x => 0);
		dictionary[lockTimeZero] = 0;

		var curTip = 100_000u;
		var lockTimeSelector = new LockTimeSelector(new Random(123456));

		foreach (var i in Enumerable.Range(0, samplingSize))
		{
			var lt = (uint)lockTimeSelector.GetLockTimeBasedOnDistribution(curTip).Height;
			var diff = lt == 0 ? lockTimeZero : lt - curTip;
			dictionary[diff]++;
		}

		Assert.InRange(dictionary[lockTimeZero], samplingSize * 0.85, samplingSize * 0.95); // around 90%
		Assert.InRange(dictionary[0], samplingSize * 0.072, samplingSize * 0.091); // around 8.15%: the tip, also for the 0.65% that the network gives the next tip
		Assert.Equal(0, dictionary[1]); // never the next tip: the node refuses a transaction that is not final yet

		var rest = dictionary.Where(x => x.Key < 0).Select(x => x.Value);
		Assert.DoesNotContain(rest, x => x > samplingSize * 0.001);
	}

	[Fact]
	public void HowFeeRateAffectsFeeAndNumberOfOutputs()
	{
		// A Taproot input, a P2PKH output and a Taproot change output make a transaction of 146 vbytes.
		// The change is 1409 sats minus the fee. It is dropped once it is below 330 sats, the dust limit of a Taproot output.

		// Fee rate: 5.0 sat/vb.
		{
			BuildTransactionResult txResult = ComputeTxResult(5.0m);
			Assert.Equal(2, txResult.Transaction.Transaction.Outputs.Count);
			AssertOutputValues(mainValue: Money.Satoshis(10000m), changeValue: Money.Satoshis(679m), txResult.Transaction.Transaction.Outputs);
			Assert.Equal(7.3m, txResult.FeePercentOfSent);
			Assert.Equal(Money.Satoshis(730), txResult.Fee);
		}

		// Fee rate: 6.0 sat/vb.
		{
			BuildTransactionResult txResult = ComputeTxResult(6.0m);
			Assert.Equal(2, txResult.Transaction.Transaction.Outputs.Count);
			AssertOutputValues(mainValue: Money.Satoshis(10000m), changeValue: Money.Satoshis(533m), txResult.Transaction.Transaction.Outputs);
			Assert.Equal(8.76m, txResult.FeePercentOfSent);
			Assert.Equal(Money.Satoshis(876), txResult.Fee);
		}

		// Fee rate: 7.0 sat/vb.
		{
			BuildTransactionResult txResult = ComputeTxResult(7.0m);
			Assert.Equal(2, txResult.Transaction.Transaction.Outputs.Count);
			AssertOutputValues(mainValue: Money.Satoshis(10000m), changeValue: Money.Satoshis(387m), txResult.Transaction.Transaction.Outputs);
			Assert.Equal(10.22m, txResult.FeePercentOfSent);
			Assert.Equal(Money.Satoshis(1022), txResult.Fee);
		}

		// Fee rate: 7.39 sat/vb. The change is 331 sats, just above the dust limit.
		{
			BuildTransactionResult txResult = ComputeTxResult(7.39m);
			Assert.Equal(2, txResult.Transaction.Transaction.Outputs.Count);
			AssertOutputValues(mainValue: Money.Satoshis(10000m), changeValue: Money.Satoshis(331m), txResult.Transaction.Transaction.Outputs);
			Assert.Equal(10.78m, txResult.FeePercentOfSent);
			Assert.Equal(Money.Satoshis(1078), txResult.Fee);
		}

		// Fee rate: 7.4 sat/vb. The change would be 329 sats, so there is only ONE output now!
		{
			BuildTransactionResult txResult = ComputeTxResult(7.4m);
			Assert.Single(txResult.Transaction.Transaction.Outputs);
			Assert.Equal(Money.Satoshis(10000m), txResult.Transaction.Transaction.Outputs[0].Value);
			Assert.Equal(14.09m, txResult.FeePercentOfSent);
			Assert.Equal(Money.Satoshis(1409), txResult.Fee);
		}

		static BuildTransactionResult ComputeTxResult(decimal feeRate)
		{
			TransactionFactory transactionFactory = ServiceFactory.CreateTransactionFactory(
				new[]
				{
					(KeyIndex: 0, Amount: 0.00011409m, Confirmed: true)
				});

			using Key key = new();
			PaymentIntent payment = new(key, MoneyRequest.Create(Money.Coins(0.00010000m)));
			TransactionParameters txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(feeRate).Build();
			return transactionFactory.BuildTransaction(txParameters);
		}

		static void AssertOutputValues(Money mainValue, Money changeValue, TxOutList txOuts)
		{
			if (mainValue == txOuts[0].Value)
			{
				Assert.Equal(changeValue, txOuts[1].Value);
			}
			else if (mainValue == txOuts[1].Value)
			{
				Assert.Equal(changeValue, txOuts[0].Value);
			}
			else
			{
				Assert.Fail("Main value is not correct.");
			}
		}
	}

	[Fact]
	public async Task CanPayToSilentPaymentAddressesAsync()
	{
		// Create a crediting transaction which received 1 BTC. Then it spends that UTXO to send 0.9 BTC to a
		// silent payment address (sp1qqdpppm9jc....qulwdyd) to finally send the new UTXO to bc1q03j8...6rrpr.
		// The latest step is to verify we can compute the private key required to unlock an UTXO received in
		// as a silent payment.

		var paymentAmount = Money.Coins(0.9m);

		var keyManager = ServiceFactory.CreateKeyManager("password", seed: ServiceFactory.FixedSeed);

		// Generates a pubkey to receive funds (1 BTC)
		var pubkey = keyManager.GetNextReceiveKey();
		var creditingTx = CreateCreditingTransaction(pubkey.GetAssumedScriptPubKey(), Money.Coins(1));

		// Configure a TransactionFactory to use the only UTXO available
		var coinsView = new CoinsView([
			new SmartCoin(creditingTx, 0, pubkey)
		]);
		var transactionFactory = new TransactionFactory(Network.Main, keyManager, coinsView, "password");

		// Create a silent payment address to receive a 0.9 payment
		using var scanKey = ECPrivKey.Create(Encoders.Hex.DecodeData("7e3a1d4b5f8e2c9a0f6b4d3e9a1f0c8b7e5d2a3c4f6e8b7a9d0c1f2e3a4b5d61"));
		using var spendKey = ECPrivKey.Create(Encoders.Hex.DecodeData("3f9a7d2c6b8e1a4f5d0c2e7b9f3a6d4c1b0e8f7a9d5c2b4e7f6a3c9b0d1f8e22"));
		var silentPaymentAddress = new SilentPaymentAddress(0, scanKey.CreatePubKey(), spendKey.CreatePubKey());

		// Uses the TransactionFactory to create a TX that pays to a silent payment address
		// and verify both the amount and the scriptPubKey are correct.
		var payment = new PaymentIntent(silentPaymentAddress, MoneyRequest.Create(paymentAmount));
		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(12m).Build();
		var result = transactionFactory.BuildTransaction(txParameters);
		var paymentOutput = Assert.Single(result.OuterWalletOutputs);
		Assert.Equal(paymentAmount, paymentOutput.Amount); // The amount is correct

		// Computes the private key required to unlock the paymentOutput
		var spendingTx = result.Transaction.Transaction;
		var prevOuts = spendingTx.Inputs.Select(x => x.PrevOut).ToArray();
		var pubKeys = spendingTx.Inputs
			.Select(x => SilentPayment.ExtractPubKey(x.ScriptSig, x.WitScript, GetScriptPubKey(transactionFactory.Coins, x.PrevOut)))
			.DropNulls()
			.ToArray();
		var sharedSecret = SilentPayment.ComputeSharedSecretReceiver(prevOuts, pubKeys, scanKey);
		using var pk = new Key(SilentPayment.ComputePrivKey(spendKey, sharedSecret, 0).sec.ToBytes());
		Assert.Equal($"1 {pk.PubKey.TaprootInternalKey}", paymentOutput.ScriptPubKey.ToString());
	}

	[Fact]
	public async Task CanPayToLabeledSilentPaymentAddressesAsync()
	{
		// Create a crediting transaction which received 1 BTC. Then it spends that UTXO to send 0.9 BTC to a
		// silent payment address (sp1qqdpppm9jc....qulwdyd) to finally send the new UTXO to bc1q03j8...6rrpr.
		// The latest step is to verify we can compute the private key required to unlock an UTXO received in
		// as a silent payment.

		var paymentAmount = Money.Coins(0.9m);

		var keyManager = ServiceFactory.CreateKeyManager("password", seed: ServiceFactory.FixedSeed);

		// Generates a pubkey to receive funds (1 BTC)
		var pubkey = keyManager.GetNextReceiveKey();
		var creditingTx = CreateCreditingTransaction(pubkey.GetAssumedScriptPubKey(), Money.Coins(1));

		// Configure a TransactionFactory to use the only UTXO available
		var coinsView = new CoinsView([
			new SmartCoin(creditingTx, 0, pubkey)
		]);
		var transactionFactory = new TransactionFactory(Network.Main, keyManager, coinsView, "password");

		// Create a silent payment address to receive a 0.9 payment
		using var scanKey = ECPrivKey.Create(Encoders.Hex.DecodeData("7e3a1d4b5f8e2c9a0f6b4d3e9a1f0c8b7e5d2a3c4f6e8b7a9d0c1f2e3a4b5d61"));
		using var spendKey = ECPrivKey.Create(Encoders.Hex.DecodeData("3f9a7d2c6b8e1a4f5d0c2e7b9f3a6d4c1b0e8f7a9d5c2b4e7f6a3c9b0d1f8e22"));
		using var label = SilentPayment.CreateLabel(scanKey, 1);
		var silentPaymentAddress = new SilentPaymentAddress(0, scanKey.CreatePubKey(), spendKey.CreatePubKey());
		var labeledSilentPaymentAddress = silentPaymentAddress.DeriveAddressForLabel(label.CreatePubKey());

		// Uses the TransactionFactory to create a TX that pays to a silent payment address
		// and verify both the amount and the scriptPubKey are correct.
		var payment = new PaymentIntent(labeledSilentPaymentAddress, MoneyRequest.Create(paymentAmount));
		var txParameters = CreateBuilder().SetPayment(payment).SetFeeRate(12m).Build();
		var result = transactionFactory.BuildTransaction(txParameters);
		var paymentOutput = Assert.Single(result.OuterWalletOutputs);
		Assert.Equal(paymentAmount, paymentOutput.Amount); // The amount is correct

		// Computes the private key required to unlock the paymentOutput
		var spendingTx = result.Transaction.Transaction;
		var prevOuts = spendingTx.Inputs.Select(x => x.PrevOut).ToArray();
		var pubKeys = spendingTx.Inputs
			.Select(x => SilentPayment.ExtractPubKey(x.ScriptSig, x.WitScript, GetScriptPubKey(transactionFactory.Coins, x.PrevOut)))
			.DropNulls()
			.ToArray();
		var sharedSecret = SilentPayment.ComputeSharedSecretReceiver(prevOuts, pubKeys, scanKey);
		var tweakData = SilentPayment.TweakData(prevOuts, pubKeys);
		var outputs = SilentPayment.ExtractSilentPaymentScriptPubKeys([silentPaymentAddress, labeledSilentPaymentAddress], tweakData, spendingTx, scanKey);

		var detectedAddress = Assert.Single(outputs.Keys);
		Assert.Equal(labeledSilentPaymentAddress, detectedAddress);

		var basePrivateKey = SilentPayment.ComputePrivKey(spendKey, sharedSecret, 0);
		using var pk = new Key((basePrivateKey.sec + label.sec).ToBytes());
		Assert.Equal($"1 {pk.PubKey.TaprootInternalKey}", paymentOutput.ScriptPubKey.ToString());
	}

	[Fact]
	public void RealMainNetTransaction()
	{
		var mnemonic = new Mnemonic("bargain pumpkin blouse crush invest control radar install alien same shield grain");
		var extKey = mnemonic.DeriveExtKey();
		using var scanKey = ECPrivKey.Create(extKey.Derive(KeyPath.Parse("m/352'/1'/0'/1'/0")).PrivateKey.ToBytes());
		using var spendKey = ECPrivKey.Create(extKey.Derive(KeyPath.Parse("m/352'/1'/0'/0'/0")).PrivateKey.ToBytes());
		var tx = Transaction.Parse(
			"020000000001016ea784410522d6c6c7dd43f9f2f05356200a7ab1f531f7bc5e34b4f84a89d7b4010000000000000080024006000000000000225120b8b5f908697c0c5982609bf577d319f7670c570e55532417cf7690e46fe67f6b6daa0100000000001600142897cab30c29ff293d1b8ef57153c3c3df0a72440247304402207d864ccbf293728f115a9a60e107cffa228e2e9b3a6325246804d2b99c52be2f02205ad685c728fa3c47f19a4863f122aee7f76eaa60101e49001157808af482d0580121024c006af7003e9588c5ca755686ffe0df6c2c107f42d06c213355783a5ed6ac9300000000",
			Network.Main);
		var silentPaymentAddress = SilentPaymentAddress.Parse(
			"sp1qqwegltczx7hry7xtptykt6z70m0akdkne463ghhn33qyyudz2p0jkqa6e5672fm4pj4a24cthu6g4829s6nr3lkjcjxah4gaxr6wd4vhh5970lk7",
			Network.Main);
		var gsp = new SilentPaymentAddress(0, scanKey.CreatePubKey(), spendKey.CreatePubKey());
		Assert.Equal(silentPaymentAddress, gsp);

		var prevOuts = tx.Inputs.Select(x => x.PrevOut).ToArray();
		var prevOutScript = Script.FromHex("00148e66f5ffc5cd985076aa0fe325da4b9a239e7ab7");
		var pubKeys = tx.Inputs
			.Select(x => SilentPayment.ExtractPubKey(x.ScriptSig, x.WitScript, prevOutScript))
			.DropNulls()
			.ToArray();

		var tweakData = SilentPayment.TweakData(prevOuts, pubKeys);
		var outputs = SilentPayment.ExtractSilentPaymentScriptPubKeys([silentPaymentAddress], tweakData, tx, scanKey);

		var detectedAddress = Assert.Single(outputs.Keys);
		Assert.Equal(silentPaymentAddress, detectedAddress);

		var sharedSecret = SilentPayment.ComputeSharedSecretReceiver(prevOuts, pubKeys, scanKey);
		using var pk = new Key(SilentPayment.ComputePrivKey(spendKey, sharedSecret, 0).sec.ToBytes());

		var singleScript = Assert.Single(outputs[detectedAddress]);
		Assert.Equal("bc1phz6ljzrf0sx9nqnqn06h05ce7ansc4cw24fjg970w6gwgmlx0a4sdnn5ll", singleScript.GetDestinationAddress(Network.Main)!.ToString());
	}

	private static Script GetScriptPubKey(ICoinsView coins, OutPoint outPoint)
	{
		if (!coins.TryGetByOutPoint(outPoint, out var smartCoin))
		{
			throw new InvalidOperationException("This should never happen");
		}
		return smartCoin.ScriptPubKey;
	}

	private static SmartTransaction CreateCreditingTransaction(Script scriptPubKey, Money amount)
	{
		Transaction tx = Network.Main.CreateTransaction();
		tx.Version = 1;
		tx.LockTime = LockTime.Zero;
		tx.Inputs.Add(OutPoint.Parse("7fcade4a7e9bedf88790a83225c7b1bf1c1dca3190aead94131b873029ab1a20-0"), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
		tx.Inputs.Add(OutPoint.Parse("7fcade4a7e9bedf88790a83225c7b1bf1c1dca3190aead94131b873029ab1a20-1"), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
		tx.Outputs.Add(amount, scriptPubKey);
		return new SmartTransaction(tx, Height.Mempool);
	}

	private static TransactionParametersBuilder CreateBuilder()
		=> TransactionParametersBuilder.CreateDefault().SetFeeRate(2m).SetAllowUnconfirmed(true);
}
