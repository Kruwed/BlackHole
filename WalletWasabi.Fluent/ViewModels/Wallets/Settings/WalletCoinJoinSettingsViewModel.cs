using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.CoinJoinProfiles;
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

	[AutoNotify] private string _anonScoreTarget;
	[AutoNotify] private bool _nonPrivateCoinIsolation;
	[AutoNotify] private bool _onlyUsePrivateFundsForPayments;
	[AutoNotify] private bool _maximizePrivacyProfileSelected;
	[AutoNotify] private bool _defaultProfileSelected;
	[AutoNotify] private bool _economicalProfileSelected;

	[AutoNotify] private bool _autoCoinJoin;
	[AutoNotify] private string _plebStopThreshold;
	[AutoNotify] private string _maxCoinjoinLeftover;

	public WalletCoinJoinSettingsViewModel(UiContext uiContext, IWalletModel walletModel) : base(uiContext)
	{
		_wallet = walletModel;
		_autoCoinJoin = _wallet.Settings.AutoCoinjoin;
		_plebStopThreshold = _wallet.Settings.PlebStopThreshold.ToString();
		_maxCoinjoinLeftover = _wallet.Settings.MaxCoinjoinLeftover.ToString();
		_anonScoreTarget = _wallet.Settings.AnonScoreTarget.ToString();
		_nonPrivateCoinIsolation = _wallet.Settings.NonPrivateCoinIsolation;
		_onlyUsePrivateFundsForPayments = _wallet.Settings.OnlyUsePrivateFundsForPayments;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		NextCommand = ReactiveCommand.Create(() =>
		{
			_wallet.Settings.Save();
			Navigate().Back();
		});

		SetAutoCoinJoin = ReactiveCommand.CreateFromTask(
			() =>
			{
				_wallet.Settings.AutoCoinjoin = AutoCoinJoin;
				_wallet.Settings.Save();
				return Task.CompletedTask;
			});

		SetNonPrivateCoinIsolationCommand = ReactiveCommand.CreateFromTask(() =>
		{
			_wallet.Settings.NonPrivateCoinIsolation = NonPrivateCoinIsolation;
			_wallet.Settings.Save();
			return Task.CompletedTask;
		});

		SetOnlyUsePrivateFundsForPaymentsCommand = ReactiveCommand.CreateFromTask(() =>
		{
			_wallet.Settings.OnlyUsePrivateFundsForPayments = OnlyUsePrivateFundsForPayments;
			_wallet.Settings.Save();
			return Task.CompletedTask;
		});

		SelectMaximizePrivacySettings = ReactiveCommand.CreateFromTask(() => SetProfile("MaximizePrivacy"));

		SelectDefaultSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Default"));

		SelectEconomicalSettings = ReactiveCommand.CreateFromTask(() => SetProfile("Economical"));

		this.WhenAnyValue(
				x => x.AnonScoreTarget,
				x => x.NonPrivateCoinIsolation,
				x => x.OnlyUsePrivateFundsForPayments)
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(_ =>
			{
				var selectedProfile = PrivacyProfiles.Profiles
					.FirstOrDefault(p =>
						p.Equals(
							int.TryParse(AnonScoreTarget, out var anonScoreTarget) ? anonScoreTarget : 0,
							NonPrivateCoinIsolation,
							OnlyUsePrivateFundsForPayments));

				MaximizePrivacyProfileSelected = selectedProfile?.Name == "MaximizePrivacy";
				EconomicalProfileSelected = selectedProfile?.Name == "Economical";
				DefaultProfileSelected = selectedProfile?.Name == "Default";
			});

		this.ValidateProperty(x => x.AnonScoreTarget, ValidateAnonScoreTarget);
		this.ValidateProperty(x => x.MaxCoinjoinLeftover, ValidateMaxCoinjoinLeftover);

		this.WhenAnyValue(x => x.PlebStopThreshold)
			.Skip(1)
			.Throttle(TimeSpan.FromMilliseconds(1000))
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(
				x =>
				{
					if (Money.TryParse(x, out var result) && result != _wallet.Settings.PlebStopThreshold)
					{
						_wallet.Settings.PlebStopThreshold = result;
						_wallet.Settings.Save();
					}
				});
	}

	public ICommand SetAutoCoinJoin { get; }
	public ICommand SetNonPrivateCoinIsolationCommand { get; }
	public ICommand SetOnlyUsePrivateFundsForPaymentsCommand { get; }
	public ICommand SelectMaximizePrivacySettings { get; }
	public ICommand SelectDefaultSettings { get; }
	public ICommand SelectEconomicalSettings { get; }

	private void ValidateAnonScoreTarget(IValidationErrors errors)
	{
		if (int.TryParse(AnonScoreTarget, out var anonScoreTarget))
		{
			if (anonScoreTarget is < PrivacyProfiles.AbsoluteMinAnonScoreTarget or > PrivacyProfiles.AbsoluteMaxAnonScoreTarget)
			{
				errors.Add(ErrorSeverity.Error, $"Must be between {PrivacyProfiles.AbsoluteMinAnonScoreTarget} and {PrivacyProfiles.AbsoluteMaxAnonScoreTarget}");
			}
			else
			{
				_wallet.Settings.AnonScoreTarget = anonScoreTarget;
				_wallet.Settings.Save();
			}
		}
		else
		{
			errors.Add(ErrorSeverity.Error, $"Must be a number between {PrivacyProfiles.AbsoluteMinAnonScoreTarget} and {PrivacyProfiles.AbsoluteMaxAnonScoreTarget}");
		}
	}

	/// <summary>
	/// The dust forfeit limit is an amount in BTC, and there always is one. As with the anonymity score target, a valid value is stored right away.
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

	private Task SetProfile(string profileName)
	{
		var profile = PrivacyProfiles.Profiles.FirstOrDefault(p => p.Name == profileName);
		if (profile is null)
		{
			return Task.CompletedTask;
		}

		AnonScoreTarget = profile.AnonScoreTarget.ToString();
		_wallet.Settings.AnonScoreTarget = profile.AnonScoreTarget;

		NonPrivateCoinIsolation = profile.NonPrivateCoinIsolation;
		_wallet.Settings.NonPrivateCoinIsolation = profile.NonPrivateCoinIsolation;

		OnlyUsePrivateFundsForPayments = profile.OnlyUsePrivateFundsForPayments;
		_wallet.Settings.OnlyUsePrivateFundsForPayments = profile.OnlyUsePrivateFundsForPayments;

		_wallet.Settings.Save();
		return Task.CompletedTask;
	}
}
