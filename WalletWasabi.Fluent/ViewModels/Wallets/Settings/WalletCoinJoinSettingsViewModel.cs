using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
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

	[AutoNotify] private string _anonScoreTarget;
	[AutoNotify] private double _anonScoreTargetSliderPosition;
	[AutoNotify] private bool _nonPrivateCoinIsolation;
	[AutoNotify] private bool _onlyUsePrivateFundsForPayments;

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
		_anonScoreTargetSliderPosition = AnonScoreTargetToSliderPosition(_wallet.Settings.AnonScoreTarget);
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

		// The slider and the text box show the same number. A text that is not a valid target leaves the slider where it is.
		this.WhenAnyValue(x => x.AnonScoreTarget)
			.Skip(1)
			.Subscribe(text =>
			{
				if (TryParseAnonScoreTarget(text, out var target) && SliderPositionToAnonScoreTarget(AnonScoreTargetSliderPosition) != target)
				{
					AnonScoreTargetSliderPosition = AnonScoreTargetToSliderPosition(target);
				}
			});

		this.WhenAnyValue(x => x.AnonScoreTargetSliderPosition)
			.Skip(1)
			.Subscribe(position =>
			{
				var target = SliderPositionToAnonScoreTarget(position);
				if (!TryParseAnonScoreTarget(AnonScoreTarget, out var current) || current != target)
				{
					AnonScoreTarget = target.ToString();
				}
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

	/// <summary>
	/// Where the slider stands for an anonymity score target, between 0 and 1. The scale is logarithmic, because the targets
	/// that people choose are near the low end of the allowed range: on an even scale they would share a few pixels.
	/// </summary>
	public static double AnonScoreTargetToSliderPosition(int anonScoreTarget)
	{
		var clamped = Math.Clamp(anonScoreTarget, KeyManager.MinAnonScoreTarget, KeyManager.MaxAnonScoreTarget);
		return Math.Log((double)clamped / KeyManager.MinAnonScoreTarget) / SliderScale;
	}

	/// <summary>The anonymity score target that a position of the slider between 0 and 1 stands for.</summary>
	public static int SliderPositionToAnonScoreTarget(double position)
	{
		var target = (int)Math.Round(KeyManager.MinAnonScoreTarget * Math.Exp(Math.Clamp(position, 0, 1) * SliderScale));
		return Math.Clamp(target, KeyManager.MinAnonScoreTarget, KeyManager.MaxAnonScoreTarget);
	}

	private static double SliderScale => Math.Log((double)KeyManager.MaxAnonScoreTarget / KeyManager.MinAnonScoreTarget);

	private static bool TryParseAnonScoreTarget(string text, out int anonScoreTarget) =>
		int.TryParse(text, out anonScoreTarget)
		&& anonScoreTarget is >= KeyManager.MinAnonScoreTarget and <= KeyManager.MaxAnonScoreTarget;

	/// <summary>The error under the text box of the target. The box is only as wide as three digits, so the message has to be short.</summary>
	private static string AnonScoreTargetRange => $"{KeyManager.MinAnonScoreTarget} to {KeyManager.MaxAnonScoreTarget}";

	private void ValidateAnonScoreTarget(IValidationErrors errors)
	{
		if (int.TryParse(AnonScoreTarget, out var anonScoreTarget))
		{
			if (anonScoreTarget is < KeyManager.MinAnonScoreTarget or > KeyManager.MaxAnonScoreTarget)
			{
				errors.Add(ErrorSeverity.Error, AnonScoreTargetRange);
			}
			else
			{
				_wallet.Settings.AnonScoreTarget = anonScoreTarget;
				_wallet.Settings.Save();
			}
		}
		else
		{
			errors.Add(ErrorSeverity.Error, AnonScoreTargetRange);
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
}
