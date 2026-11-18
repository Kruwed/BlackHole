using WalletWasabi.FidelityBonds;
using WalletWasabi.Helpers;

namespace WalletWasabi.Models;

public class ServiceConfiguration
{
	public ServiceConfiguration(int dropUnconfirmedTransactionsAfterDays = Constants.DefaultMaxDaysInMempool)
	{
		DropUnconfirmedTransactionsAfterDays = dropUnconfirmedTransactionsAfterDays;
	}

	public int DropUnconfirmedTransactionsAfterDays { get; }

	/// <summary>
	/// How far above the chain a wallet that goes through the chain again looks for the unlock heights of its fidelity
	/// bonds. It is as far as a bond can be locked for, and only tests have a reason for less.
	/// </summary>
	public uint FidelityBondSearchBlocks { get; init; } = FidelityBondKeys.MaxLockBlocks;
}
