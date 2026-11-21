using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.ViewModels;

/// <summary>
/// What the preview of a transaction warns about. It does not judge what a transaction gives away: which coins it
/// spends together, how many payments it makes, how much of the balance it takes and whether it has change is what the
/// user chose. It tells what may become of the transaction: unconfirmed coins, and coins that are in a coinjoin round.
/// </summary>
public class SendWarningsTests
{
	/// <summary>
	/// One transaction that does all that the preview used to warn about for the sake of privacy: it makes two payments,
	/// it spends two coins together that nothing linked before, they are all that the wallet has, and it has change.
	/// </summary>
	[Fact]
	public void PaymentsInOneTransactionWithChangeFromSeveralCoinsAreNothingToWarnAbout()
	{
		var result = BuildTransactionWithTwoPayments(firstCoinIsConfirmed: true);

		Assert.Equal(2, result.OuterWalletOutputs.Count());
		Assert.Equal(2, result.SpentCoins.Count());
		Assert.Equal(2, result.SpentCoins.Select(x => x.HdPubKey.Cluster).Distinct().Count());
		Assert.Single(result.InnerWalletOutputs);

		Assert.Empty(TransactionWarning.GetWarnings(result));
	}

	[Fact]
	public void SpendingUnconfirmedCoinsIsWarnedAbout()
	{
		var result = BuildTransactionWithTwoPayments(firstCoinIsConfirmed: false);

		Assert.IsType<UnconfirmedFundsWarning>(Assert.Single(TransactionWarning.GetWarnings(result)));
	}

	/// <summary>A coin that is in a coinjoin round may be spent by the coinjoin instead.</summary>
	[Fact]
	public void SpendingCoinsThatAreInACoinjoinRoundIsWarnedAbout()
	{
		var result = BuildTransactionWithTwoPayments(firstCoinIsConfirmed: true);
		result.SpentCoins.First().CoinJoinInProgress = true;

		Assert.IsType<CoinjoiningFundsWarning>(Assert.Single(TransactionWarning.GetWarnings(result)));
	}

	[Fact]
	public void WarningsAreToldTogether()
	{
		Assert.Empty(TransactionWarning.GetWarnings(spendsUnconfirmed: false, spendsCoinjoining: false));

		Assert.Equal(
			[typeof(UnconfirmedFundsWarning), typeof(CoinjoiningFundsWarning)],
			TransactionWarning.GetWarnings(spendsUnconfirmed: true, spendsCoinjoining: true).Select(x => x.GetType()));
	}

	/// <summary>Builds a transaction that pays 0.1 BTC to each of two strangers out of the two coins of a wallet, 0.08 and 0.16 BTC.</summary>
	private static BuildTransactionResult BuildTransactionWithTwoPayments(bool firstCoinIsConfirmed)
	{
		var transactionFactory = ServiceFactory.CreateTransactionFactory(
			new[]
			{
				(0, 0.08m, firstCoinIsConfirmed),
				(1, 0.16m, true)
			});

		using Key first = new();
		using Key second = new();

		var payment = new PaymentIntent(
			new DestinationRequest(first.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), Money.Coins(0.1m)),
			new DestinationRequest(second.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86), Money.Coins(0.1m)));

		var parameters = TransactionParametersBuilder.CreateDefault()
			.SetFeeRate(2m)
			.SetAllowUnconfirmed(true)
			.SetPayment(payment)
			.Build();

		return transactionFactory.BuildTransaction(parameters);
	}
}
