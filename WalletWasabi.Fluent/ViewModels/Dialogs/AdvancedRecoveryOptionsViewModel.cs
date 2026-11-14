using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Dialogs.Base;
using WalletWasabi.Models;

namespace WalletWasabi.Fluent.ViewModels.Dialogs;

[NavigationMetaData(Title = "Advanced Recovery Options", NavigationTarget = NavigationTarget.CompactDialogScreen)]
public partial class AdvancedRecoveryOptionsViewModel : DialogViewModelBase<(int MinGapLimit, uint BirthHeight)?>
{
	[AutoNotify] private string _minGapLimit;
	[AutoNotify] private string _birthHeight;

	private readonly uint _earliestBirthHeight;

	/// <param name="birthHeight">Height of the first block that is looked through for the transactions of the wallet.</param>
	/// <param name="earliestBirthHeight">Nothing of the wallet can be found before this height.</param>
	public AdvancedRecoveryOptionsViewModel(UiContext uiContext, int minGapLimit, uint birthHeight, uint earliestBirthHeight) : base(uiContext)
	{
		_minGapLimit = minGapLimit.ToString();
		_earliestBirthHeight = earliestBirthHeight;
		_birthHeight = birthHeight.ToString(CultureInfo.InvariantCulture);

		this.ValidateProperty(x => x.MinGapLimit, ValidateMinGapLimit);
		this.ValidateProperty(x => x.BirthHeight, ValidateBirthHeight);

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);
		EnableBack = false;

		NextCommand = ReactiveCommand.Create(
			() => Close(result: (int.Parse(MinGapLimit), uint.Parse(BirthHeight))),
			this.WhenAnyValue(x => x.MinGapLimit, x => x.BirthHeight).Select(_ => !Validations.Any));
	}

	private void ValidateMinGapLimit(IValidationErrors errors)
	{
		if (!int.TryParse(MinGapLimit, out var minGapLimit) ||
			minGapLimit is < KeyManager.AbsoluteMinGapLimit or > KeyManager.MaxGapLimit)
		{
			errors.Add(
				ErrorSeverity.Error,
				$"Must be a number between {KeyManager.AbsoluteMinGapLimit} and {KeyManager.MaxGapLimit}.");
		}
	}

	private void ValidateBirthHeight(IValidationErrors errors)
	{
		if (!uint.TryParse(BirthHeight, out var height) || height < _earliestBirthHeight)
		{
			errors.Add(
				ErrorSeverity.Error,
				$"Must be a number greater than or equal to {_earliestBirthHeight}.");
		}
	}
}
