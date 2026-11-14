using NBitcoin;
using NBitcoin.RPC;

namespace WalletWasabi.Models;

/// <summary>
/// What the mempool of the node knows about an unconfirmed transaction and about the unconfirmed transactions that it depends on.
/// </summary>
/// <param name="Fee">Fee of the transaction.</param>
/// <param name="VSize">Virtual size of the transaction.</param>
/// <param name="AncestorsFee">Fees of the unconfirmed transactions that it spends from, directly or not.</param>
/// <param name="AncestorsVSize">Virtual size of those transactions.</param>
/// <param name="EffectiveFeeRate">The fee rate that miners consider for the transaction: it has to pay for its ancestors, and its descendants can pay for it.</param>
public record CpfpInfo(Money Fee, long VSize, Money AncestorsFee, long AncestorsVSize, FeeRate EffectiveFeeRate)
{
	public static CpfpInfo FromMempoolEntry(MempoolEntry entry)
	{
		// The entry counts the transaction itself among its ancestors.
		var ancestorsFee = Money.Max(entry.AncestorFees - entry.BaseFee, Money.Zero);
		var ancestorsVSize = Math.Max(entry.AncestorVirtualSizeBytes - entry.VirtualSizeBytes, 0);

		return new CpfpInfo(entry.BaseFee, entry.VirtualSizeBytes, ancestorsFee, ancestorsVSize, GetEffectiveFeeRate(entry));
	}

	private static FeeRate GetEffectiveFeeRate(MempoolEntry entry)
	{
		// Recent nodes tell the fee rate of the chunk that the transaction would be mined in.
		if (entry.ChunkFees is { } chunkFees && entry.ChunkWeight > 0)
		{
			return new FeeRate(chunkFees, (entry.ChunkWeight + 3) / 4);
		}

		// Otherwise estimate it. The transaction is not mined before its ancestors: when they pay less than it does, it pays for them.
		// (Ancestors that pay more are mined without it.)
		var fee = entry.BaseFee;
		var vsize = entry.VirtualSizeBytes;

		if (entry.AncestorVirtualSizeBytes > vsize && new FeeRate(entry.AncestorFees, entry.AncestorVirtualSizeBytes) < new FeeRate(fee, vsize))
		{
			fee = entry.AncestorFees;
			vsize = entry.AncestorVirtualSizeBytes;
		}

		// Descendants that pay more than that take the transaction into a block with them.
		// The node counts the transaction itself among its descendants, as it does among its ancestors.
		var descendantsFee = entry.DescendantFees - entry.BaseFee;
		var descendantsVSize = entry.DescendantVirtualSizeBytes - entry.VirtualSizeBytes;

		if (descendantsVSize > 0 && new FeeRate(descendantsFee, descendantsVSize) > new FeeRate(fee, vsize))
		{
			fee += descendantsFee;
			vsize += descendantsVSize;
		}

		return new FeeRate(fee, vsize);
	}
}
