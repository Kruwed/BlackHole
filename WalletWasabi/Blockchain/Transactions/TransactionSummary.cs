using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Models;

namespace WalletWasabi.Blockchain.Transactions;

public class TransactionSummary
{
	public TransactionSummary(SmartTransaction tx, Money amount, FeeRate? effectiveFeeRate)
	{
		Transaction = tx;
		Amount = amount;
		EffectiveFeeRate = effectiveFeeRate;
	}

	private readonly List<FidelityBond> _unlockedBonds = [];

	public SmartTransaction Transaction { get; }

	/// <summary>How the transaction changed what the wallet owns, its coins and the coins in its fidelity bonds together.</summary>
	public Money Amount { get; set; }

	/// <summary>The coins that the transaction puts into fidelity bonds of the wallet.</summary>
	public Money LockedInBonds { get; private set; } = Money.Zero;

	/// <summary>What the fidelity bonds of the wallet held that the transaction redeems.</summary>
	public Money UnlockedFromBonds => _unlockedBonds.Sum(x => x.Amount);
	public FeeRate? EffectiveFeeRate { get; }
	public Func<string> Hex => () => Transaction.Transaction.ToHex();
	public Func<IReadOnlyCollection<OutPoint>> ForeignInputs => () => Transaction.ForeignInputs.Select(x => x.PrevOut).ToArray();
	public IReadOnlyCollection<SmartCoin> WalletInputs => Transaction.WalletInputs;
	public Func<IReadOnlyCollection<IndexedTxOut>> ForeignOutputs => () => Transaction.ForeignOutputs;
	public IReadOnlyCollection<SmartCoin> WalletOutputs => Transaction.WalletOutputs;
	public DateTimeOffset FirstSeen => Transaction.FirstSeen;
	public Height Height => Transaction.Height;
	public uint256? BlockHash => Transaction.BlockHash;
	public int BlockIndex => Transaction.BlockIndex;
	public bool IsCancellation => Transaction.IsCancellation;
	public bool IsSpeedup => Transaction.IsSpeedup;
	public bool IsCPFP => Transaction.IsCPFP;
	public bool IsCPFPd => Transaction.IsCPFPd;

	/// <summary>The transaction creates a bond of the wallet: the coins of the bond are the wallet's.</summary>
	public void AddLockedBond(FidelityBond bond)
	{
		Amount += bond.Amount;
		LockedInBonds += bond.Amount;
	}

	/// <summary>The transaction spends a bond of the wallet: the coins of the bond leave it, to the wallet or to elsewhere.</summary>
	public void AddUnlockedBond(FidelityBond bond)
	{
		Amount -= bond.Amount;
		_unlockedBonds.Add(bond);
	}

	public Money? GetFee()
	{
		if (Transaction.GetFee() is { } fee)
		{
			return fee;
		}

		// A bond of the wallet is no coin of it, but the wallet knows what it held. With that every input may be known.
		return TryGetAllInputs(out var inputs)
			? Transaction.Transaction.GetFee(inputs)
			: null;
	}

	public FeeRate? FeeRate()
	{
		if (Transaction.TryGetFeeRate(out var feeRate))
		{
			return feeRate;
		}

		return TryGetAllInputs(out var inputs) && !Transaction.IsSegwitWithoutWitness
			? Transaction.Transaction.GetFeeRate(inputs)
			: EffectiveFeeRate;
	}

	/// <summary>The coins that the transaction spends, if they are all coins or bonds of the wallet and at least one is a bond.</summary>
	private bool TryGetAllInputs([NotNullWhen(true)] out ICoin[]? inputs)
	{
		inputs = null;

		if (_unlockedBonds.Count == 0)
		{
			return false;
		}

		var known = Transaction.WalletInputs.Select(x => x.Coin).Concat(_unlockedBonds.Select(x => x.Coin)).DistinctBy(x => x.Outpoint).ToArray();

		if (known.Length != Transaction.Transaction.Inputs.Count)
		{
			return false;
		}

		inputs = known;
		return true;
	}

	public uint256 GetHash() => Transaction.GetHash();

	public bool IsOwnCoinjoin() => Transaction.IsOwnCoinjoin();
}
