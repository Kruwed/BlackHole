using WalletWasabi.Helpers;

namespace WalletWasabi.Models;

public class ServiceConfiguration
{
	public ServiceConfiguration(int dropUnconfirmedTransactionsAfterDays = Constants.DefaultMaxDaysInMempool)
	{
		DropUnconfirmedTransactionsAfterDays = dropUnconfirmedTransactionsAfterDays;
	}

	public int DropUnconfirmedTransactionsAfterDays { get; }
}
