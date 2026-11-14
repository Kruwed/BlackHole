using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

public class RecipientSummaryViewModel : ViewModelBase
{
	public RecipientSummaryViewModel(UiContext uiContext, string addressText, Amount amount) : base(uiContext)
	{
		AddressText = addressText;
		Amount = amount;
	}

	public string AddressText { get; }

	public Amount Amount { get; }
}
