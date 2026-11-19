using System.Collections.ObjectModel;
using System.Linq;
using WalletWasabi.Blockchain.TransactionBuilding;
using WalletWasabi.Fluent.Models.Transactions;
using WalletWasabi.Fluent.Models.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Send;

/// <summary>
/// What the preview of a transaction says about its privacy: the warnings, and how serious the worst of them is.
/// </summary>
public partial class PrivacyWarningsFlyoutViewModel : ViewModelBase
{
	private readonly PrivacyWarningsModel _privacyWarningsModel;

	[AutoNotify] private bool _noPrivacy;
	[AutoNotify] private bool _badPrivacy;
	[AutoNotify] private bool _goodPrivacy;
	[AutoNotify] private bool _maxPrivacy;

	public PrivacyWarningsFlyoutViewModel(UiContext uiContext, IWalletModel wallet) : base(uiContext)
	{
		_privacyWarningsModel = wallet.GetPrivacyWarningsModel();
	}

	public ObservableCollection<PrivacyWarning> Warnings { get; } = new();

	/// <summary>Shows the warnings about the given transaction in place of the ones that were shown so far.</summary>
	public void Update(TransactionInfo info, BuildTransactionResult transaction)
	{
		var warnings = _privacyWarningsModel.GetWarnings(info, transaction);

		Warnings.Clear();

		foreach (var warning in warnings)
		{
			Warnings.Add(warning);
		}

		NoPrivacy = warnings.Any(x => x.Severity == WarningSeverity.Critical);
		BadPrivacy = !NoPrivacy && warnings.Any(x => x.Severity == WarningSeverity.Warning);
		GoodPrivacy = !NoPrivacy && !BadPrivacy && warnings.Any(x => x.Severity == WarningSeverity.Info);
		MaxPrivacy = !NoPrivacy && !BadPrivacy && !GoodPrivacy;
	}
}
