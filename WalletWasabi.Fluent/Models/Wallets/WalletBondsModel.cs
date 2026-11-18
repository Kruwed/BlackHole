using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Logging;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>Where a fidelity bond stands.</summary>
public enum FidelityBondState
{
	/// <summary>The transaction that pays to the bond is not confirmed yet.</summary>
	Unconfirmed,

	/// <summary>The coins are locked: the locktime is not over, or the node does not tell the time of the chain.</summary>
	Locked,

	/// <summary>The locktime is over. The bond can be redeemed.</summary>
	Unlocked,

	/// <summary>The transaction that redeems the bond is not confirmed yet.</summary>
	Redeeming,

	Redeemed
}

/// <param name="Value">What the bond counts for, see <see cref="BondValue"/>. Null as long as it cannot be told.</param>
public record FidelityBondModel(FidelityBond Bond, BitcoinAddress Address, FidelityBondState State, BigInteger? Value);

/// <summary>The bonds of the wallet at one moment.</summary>
/// <param name="ChainTime">The time of the chain that the states of the bonds were told by, if the node told it.</param>
public record FidelityBondsModel(IReadOnlyList<FidelityBondModel> Bonds, DateTimeOffset? ChainTime);

/// <summary>
/// The fidelity bonds of the wallet: coins that are locked until a date.
/// </summary>
[AppLifetime]
public class WalletBondsModel
{
	/// <summary>
	/// The longest that the wallet offers to lock coins for, ten years. A bond cannot be undone, so the wallet does not
	/// offer every date that the scheme of the bonds knows: that goes to the end of 2099.
	/// </summary>
	public const int MaxLockMonths = 120;

	private readonly IServices _services;
	private readonly Wallet _wallet;

	public WalletBondsModel(IServices services, Wallet wallet)
	{
		_services = services;
		_wallet = wallet;

		// A transaction that concerns a bond, and every new block: it confirms and it moves the time of the chain. The node
		// is asked about itself far more often than it has anything new to tell.
		Changed = Observable.Merge(
			services.EventBus.AsObservable<FidelityBondTransactionProcessed>().ToSignal(),
			services.EventBus.AsObservable<RpcStatusChanged>()
				.Select(e => e.Status.Match(node => (BestBlock: (uint256?)node.BestBlockHash, Time: (DateTimeOffset?)node.MedianTimePast), _ => (null, null)))
				.DistinctUntilChanged()
				.ToSignal());
	}

	/// <summary>Tells that the bonds may look different now.</summary>
	public IObservable<Unit> Changed { get; }

	/// <summary>
	/// The time of the chain: the median time of its last blocks, as the node tells it. A timelock is over when this time
	/// is past it, which is about an hour later than by the clock. Null as long as the node did not answer.
	/// </summary>
	public DateTimeOffset? ChainTime => _services.GetNodeStatus()?.MedianTimePast;

	/// <summary>What the node estimated the last time for the fee rates that confirm a transaction within so many blocks, if it did.</summary>
	public FeeRateEstimations? FeeRateEstimations => _wallet.FeeRateEstimations;

	/// <summary>The months that coins can be locked until, the nearest one first.</summary>
	public IReadOnlyList<BondLocktime> GetSelectableLocktimes() => GetSelectableLocktimes(DateTimeOffset.UtcNow, ChainTime);

	/// <summary>Tells if coins can be locked until the month now: it is ahead, and not further than the wallet offers.</summary>
	public bool IsSelectable(BondLocktime locktime) => GetSelectableLocktimes().Contains(locktime);

	/// <summary>The months that coins can be locked until, the nearest one first.</summary>
	/// <param name="clock">The time by the clock of the computer.</param>
	/// <param name="chainTime">The time of the chain, if the node told it.</param>
	public static IReadOnlyList<BondLocktime> GetSelectableLocktimes(DateTimeOffset clock, DateTimeOffset? chainTime)
	{
		// A clock that is behind the chain does not make a date selectable that the chain is past already.
		var now = chainTime is { } time && time > clock ? time : clock;

		return BondLocktime.FirstAfter(now) is { } first
			? Enumerable.Range(first.Index, MaxLockMonths).TakeWhile(index => index < BondLocktime.Count).Select(BondLocktime.FromIndex).ToList()
			: [];
	}

	/// <summary>Tells where a bond stands.</summary>
	/// <param name="chainTime">The time of the chain, if the node told it. Without it a bond counts as locked.</param>
	public static FidelityBondState GetState(FidelityBond bond, DateTimeOffset? chainTime) =>
		bond switch
		{
			{ SpenderTransaction.Confirmed: true } => FidelityBondState.Redeemed,
			{ IsSpent: true } => FidelityBondState.Redeeming,
			{ IsConfirmed: false } => FidelityBondState.Unconfirmed,
			_ when chainTime is { } time && bond.IsUnlocked(time) => FidelityBondState.Unlocked,
			_ => FidelityBondState.Locked
		};

	/// <summary>The address that locks what is paid to it until the given month.</summary>
	public BitcoinAddress GetAddress(BondLocktime locktime) => _wallet.KeyManager.FidelityBonds.GetAddress(locktime, _wallet.Network);

	/// <summary>The bonds of the wallet, the redeemed ones included.</summary>
	public async Task<FidelityBondsModel> GetBondsAsync(CancellationToken cancellationToken)
	{
		var chainTime = ChainTime;
		var bonds = new List<FidelityBondModel>();

		foreach (var bond in _wallet.GetFidelityBonds())
		{
			bonds.Add(new FidelityBondModel(bond, GetAddress(bond.Locktime), GetState(bond, chainTime), await GetValueAsync(bond, cancellationToken)));
		}

		return new FidelityBondsModel(bonds, chainTime);
	}

	/// <summary>
	/// Builds the transaction that pays a bond whose locktime is over back to the wallet.
	/// </summary>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	public SmartTransaction BuildRedemption(FidelityBond bond, FeeRate feeRate)
	{
		var chainTime = ChainTime
			?? throw new InvalidOperationException("The Bitcoin node does not answer. Without it there is no telling whether the timelock of the bond is over.");

		return _wallet.BuildFidelityBondRedemption(bond, _wallet.Password, feeRate, chainTime);
	}

	/// <summary>
	/// The value of a bond that is confirmed and not spent. It needs the time of the block that confirmed the bond, which
	/// the node tells. The other end is the clock, as it is for everyone who compares bonds.
	/// </summary>
	private async Task<BigInteger?> GetValueAsync(FidelityBond bond, CancellationToken cancellationToken)
	{
		if (bond.IsSpent || bond.Transaction.BlockHash is not { } blockHash)
		{
			return null;
		}

		try
		{
			var confirmationTime = await _wallet.GetBlockTimeAsync(blockHash, cancellationToken);
			return BondValue.Calculate(bond.Amount, confirmationTime, bond.Locktime, DateTimeOffset.UtcNow);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			Logger.LogDebug(ex);
			return null;
		}
	}
}
