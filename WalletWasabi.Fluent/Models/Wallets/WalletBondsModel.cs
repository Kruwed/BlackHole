using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FeeRateEstimation;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>Where a fidelity bond stands.</summary>
public enum FidelityBondState
{
	/// <summary>The transaction that pays to the bond is not confirmed yet.</summary>
	Unconfirmed,

	/// <summary>The coins are locked: the chain did not reach the unlock height, or the node does not tell how far the chain is.</summary>
	Locked,

	/// <summary>The chain reached the unlock height. The bond can be redeemed.</summary>
	Unlocked,

	/// <summary>The transaction that redeems the bond is not confirmed yet.</summary>
	Redeeming,

	Redeemed
}

/// <param name="Value">What the bond counts for, see <see cref="BondValue"/>. Null as long as it cannot be told.</param>
public record FidelityBondModel(FidelityBond Bond, BitcoinAddress Address, FidelityBondState State, BigInteger? Value);

/// <summary>The bonds of the wallet at one moment.</summary>
/// <param name="ChainHeight">The height of the chain that the states of the bonds were told by, if the node told it.</param>
public record FidelityBondsModel(IReadOnlyList<FidelityBondModel> Bonds, uint? ChainHeight);

/// <summary>
/// The fidelity bonds of the wallet: coins that are locked until the chain reaches a block height.
/// </summary>
[AppLifetime]
public class WalletBondsModel
{
	/// <summary>The furthest ahead that coins can be locked: the blocks of ten years.</summary>
	public const uint MaxLockBlocks = FidelityBondKeys.MaxLockBlocks;

	private readonly IServices _services;
	private readonly Wallet _wallet;

	public WalletBondsModel(IServices services, Wallet wallet)
	{
		_services = services;
		_wallet = wallet;

		// A transaction that concerns a bond, and every new block: it confirms and it brings the unlock heights nearer.
		// The node is asked about itself far more often than it has anything new to tell.
		Changed = Observable.Merge(
			services.EventBus.AsObservable<FidelityBondTransactionProcessed>().ToSignal(),
			services.EventBus.AsObservable<RpcStatusChanged>()
				.Select(e => e.Status.Match(node => ((ulong?)node.Blocks, (ulong?)node.Headers), _ => (null, null)))
				.DistinctUntilChanged()
				.ToSignal());
	}

	/// <summary>Tells that the bonds may look different now.</summary>
	public IObservable<Unit> Changed { get; }

	/// <summary>
	/// The height of the last block that the node has. A bond can be redeemed when it is not below the unlock height: the
	/// node takes the transaction then. Null as long as the node did not answer.
	/// </summary>
	public uint? NodeHeight => _services.GetNodeStatus() is { } node ? (uint)node.Blocks : null;

	/// <summary>
	/// The height of the chain as far as the node knows of it, which is further than <see cref="NodeHeight"/> while the node
	/// is still downloading blocks. A new bond unlocks above it. Null as long as the node did not answer.
	/// </summary>
	public uint? ChainHeight => _services.GetNodeStatus() is { } node ? (uint)Math.Max(node.Blocks, node.Headers) : null;

	/// <summary>What the node estimated the last time for the fee rates that confirm a transaction within so many blocks, if it did.</summary>
	public FeeRateEstimations? FeeRateEstimations => _wallet.FeeRateEstimations;

	/// <summary>Tells why coins cannot be locked until the height, or null if they can.</summary>
	/// <param name="chainHeight">The height of the last block of the chain.</param>
	public static string? ValidateUnlockHeight(uint unlockHeight, uint chainHeight)
	{
		if (unlockHeight <= chainHeight)
		{
			return $"The chain is at block {BlockHeightText.Format(chainHeight)} already. Name a block that is still to come.";
		}

		if (unlockHeight - chainHeight > MaxLockBlocks)
		{
			return $"The furthest is block {BlockHeightText.Format(chainHeight + MaxLockBlocks)}, about ten years from now.";
		}

		return null;
	}

	/// <summary>Tells if coins can be locked until the height now: it is ahead of the chain, and not further than the wallet allows.</summary>
	public bool CanLockUntil(uint unlockHeight) => ChainHeight is { } chainHeight && ValidateUnlockHeight(unlockHeight, chainHeight) is null;

	/// <summary>
	/// The address that locks what is paid to it until the chain reaches the height. The wallet watches the address from
	/// now on, and the wallet file remembers it: this is to be called before the address is shown to anyone.
	/// </summary>
	public BitcoinAddress GetAddress(uint unlockHeight) => _wallet.KeyManager.GetFidelityBondAddress(unlockHeight);

	/// <summary>Tells where a bond stands.</summary>
	/// <param name="nodeHeight">The height of the last block that the node has, if it told. Without it a bond counts as locked.</param>
	public static FidelityBondState GetState(FidelityBond bond, uint? nodeHeight) =>
		bond switch
		{
			{ SpenderTransaction.Confirmed: true } => FidelityBondState.Redeemed,
			{ IsSpent: true } => FidelityBondState.Redeeming,
			{ IsConfirmed: false } => FidelityBondState.Unconfirmed,
			_ when nodeHeight is { } height && bond.IsUnlocked(height) => FidelityBondState.Unlocked,
			_ => FidelityBondState.Locked
		};

	/// <summary>
	/// What a bond counts for at the given height of the chain, if it is confirmed and not spent. Null otherwise.
	/// </summary>
	public static BigInteger? GetValue(FidelityBond bond, uint? nodeHeight) =>
		!bond.IsSpent && nodeHeight is { } currentHeight && bond.Transaction.Height is Height.ChainHeight(var confirmationHeight)
			? BondValue.Calculate(bond.Amount, confirmationHeight, bond.UnlockHeight, currentHeight)
			: null;

	/// <summary>The bonds of the wallet, the redeemed ones included.</summary>
	public FidelityBondsModel GetBonds()
	{
		var nodeHeight = NodeHeight;
		var network = _wallet.Network;

		var bonds = _wallet.GetFidelityBonds()
			.Select(bond => new FidelityBondModel(
				bond,
				_wallet.KeyManager.FidelityBonds.GetAddress(bond.UnlockHeight, network),
				GetState(bond, nodeHeight),
				GetValue(bond, nodeHeight)))
			.ToList();

		return new FidelityBondsModel(bonds, nodeHeight);
	}

	/// <summary>
	/// Builds the transaction that pays a bond whose timelock is over back to the wallet.
	/// </summary>
	/// <exception cref="InvalidOperationException">The bond cannot be redeemed, at least not now or not at this fee rate.</exception>
	public SmartTransaction BuildRedemption(FidelityBond bond, FeeRate feeRate)
	{
		var nodeHeight = NodeHeight
			?? throw new InvalidOperationException("The Bitcoin node does not answer. Without it there is no telling whether the chain has reached the unlock height of the bond.");

		return _wallet.BuildFidelityBondRedemption(bond, _wallet.Password, feeRate, nodeHeight);
	}
}
