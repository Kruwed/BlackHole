using System.Collections.ObjectModel;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Fluent.Models.Transactions;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

/// <summary>
/// What the preview of a transaction warns about: that the transaction spends coins which are not confirmed yet, or
/// coins which are in a coinjoin round. Most transactions do neither, and then the preview shows nothing of this.
/// </summary>
public partial class TransactionWarningsViewModel : ViewModelBase
{
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasWarnings;

	public TransactionWarningsViewModel(UiContext uiContext) : base(uiContext)
	{
	}

	public ObservableCollection<TransactionWarning> Warnings { get; } = new();

	/// <summary>Shows the warnings about the given transaction in place of the ones that were shown so far.</summary>
	public void Update(BuildTransactionResult transaction)
	{
		Warnings.Clear();

		foreach (var warning in TransactionWarning.GetWarnings(transaction))
		{
			Warnings.Add(warning);
		}

		HasWarnings = Warnings.Count > 0;
	}
}
