using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Models.Transactions;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

public partial class TransactionInfo
{
	[AutoNotify] private FeeRate _feeRate = FeeRate.Zero;
	[AutoNotify] private IEnumerable<SmartCoin> _coins = Enumerable.Empty<SmartCoin>();

	public TransactionInfo(Destination destination, int anonScoreTarget)
	{
		Destination = destination;
		PrivateCoinThreshold = anonScoreTarget;

		this.WhenAnyValue(x => x.FeeRate)
			.Subscribe(_ => OnFeeChanged());

		this.WhenAnyValue(x => x.Coins)
			.Subscribe(_ => OnCoinsChanged());
	}

	public int PrivateCoinThreshold { get; }

	public Money Amount { get; init; } = Money.Zero;

	public Destination Destination { get; init; }

	public FeeRate? MaximumPossibleFeeRate { get; set; }

	public IEnumerable<SmartCoin> ChangelessCoins { get; set; } = [];

	public bool IsOptimized => ChangelessCoins.Any();

	public bool IsCustomFeeUsed { get; set; }

	public bool SubtractFee { get; init; }

	public bool IsFixedAmount { get; init; }

	public IReadOnlyList<RecipientInfo> AdditionalRecipients { get; init; } = [];

	public bool IsPayToMany => AdditionalRecipients.Count > 0;

	public IEnumerable<RecipientInfo> AllRecipients
	{
		get
		{
			yield return new RecipientInfo(Destination, Amount, IsSubtractFee: SubtractFee);
			foreach (var r in AdditionalRecipients)
			{
				yield return r;
			}
		}
	}

	public Money TotalAmount => AllRecipients.Aggregate(Money.Zero, (sum, r) => sum + r.Amount);

	private void OnFeeChanged()
	{
		ChangelessCoins = [];
	}

	private void OnCoinsChanged()
	{
		MaximumPossibleFeeRate = null;
		ChangelessCoins = []; // Clear ChangelessCoins when the coins change, so we calculate the suggestions with the new coins.
	}

	public TransactionInfo Clone()
	{
		return new TransactionInfo(Destination, PrivateCoinThreshold)
		{
			FeeRate = FeeRate,
			Coins = Coins,
			Amount = Amount,
			Destination = Destination,
			MaximumPossibleFeeRate = MaximumPossibleFeeRate,
			ChangelessCoins = ChangelessCoins,
			IsCustomFeeUsed = IsCustomFeeUsed,
			SubtractFee = SubtractFee,
			IsFixedAmount = IsFixedAmount,
			AdditionalRecipients = AdditionalRecipients
		};
	}
}
