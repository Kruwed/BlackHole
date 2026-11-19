using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Settings;

/// <summary>
/// The settings of the wallet, all of which are about its coinjoins. The menu of the wallet and the menu of the coinjoin controls open this page.
/// </summary>
[AppLifetime]
[NavigationMetaData(Title = "Coinjoin Settings", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class WalletCoinJoinSettingsViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	[AutoNotify] private string _maxCoinjoinLeftover;

	public WalletCoinJoinSettingsViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		_wallet = walletModel;
		_maxCoinjoinLeftover = _wallet.Settings.MaxCoinjoinLeftover.ToString();

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		NextCommand = ReactiveCommand.Create(() =>
		{
			_wallet.Settings.Save();
			Navigate().Back();
		});

		this.ValidateProperty(x => x.MaxCoinjoinLeftover, ValidateMaxCoinjoinLeftover);
	}

	/// <summary>
	/// The dust forfeit limit is an amount in BTC, and there always is one. A valid value is stored right away.
	/// </summary>
	private void ValidateMaxCoinjoinLeftover(IValidationErrors errors)
	{
		var highest = KeyManager.HighestMaxCoinjoinLeftover;

		if (!Money.TryParse(MaxCoinjoinLeftover, out var limit) || limit is null || limit < Money.Zero || limit > highest)
		{
			errors.Add(ErrorSeverity.Error, $"Must be between 0 and {highest} BTC.");
			return;
		}

		if (limit != _wallet.Settings.MaxCoinjoinLeftover)
		{
			_wallet.Settings.MaxCoinjoinLeftover = limit;
			_wallet.Settings.Save();
		}
	}
}
