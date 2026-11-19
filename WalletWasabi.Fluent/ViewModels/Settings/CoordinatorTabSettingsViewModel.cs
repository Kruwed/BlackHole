using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;

namespace WalletWasabi.Fluent.ViewModels.Settings;

[AppLifetime]
[NavigationMetaData(Title = "Coordinator")]
public partial class CoordinatorTabSettingsViewModel : RoutableViewModel
{
	[AutoNotify] private string _coordinatorUri;
	[AutoNotify] private string _maxCoinJoinMiningFeeRate;
	[AutoNotify] private string _absoluteMinInputCount;
	[AutoNotify] private string _maxCoinjoinLeftover = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasWallet;

	/// <summary>
	/// The settings of the wallet, once there is one. The dust forfeit limit is kept with the wallet and not with the
	/// settings of the application, so it applies to the next round without a restart.
	/// </summary>
	private WalletSettingsModel? _walletSettings;

	public CoordinatorTabSettingsViewModel(UiContext uiContext, ApplicationSettings settings) : base(uiContext)
	{
		Settings = settings;

		this.ValidateProperty(x => x.CoordinatorUri, ValidateCoordinatorUri);
		this.ValidateProperty(x => x.MaxCoinJoinMiningFeeRate, ValidateMaxCoinJoinMiningFeeRate);
		this.ValidateProperty(x => x.AbsoluteMinInputCount, ValidateAbsoluteMinInputCount);
		this.ValidateProperty(x => x.MaxCoinjoinLeftover, ValidateMaxCoinjoinLeftover);

		_coordinatorUri = settings.CoordinatorUri;
		_maxCoinJoinMiningFeeRate = settings.MaxCoinJoinMiningFeeRate;
		_absoluteMinInputCount = settings.AbsoluteMinInputCount;

		this.WhenAnyValue(
				x => x.Settings.CoordinatorUri,
				x => x.Settings.Network)
			.ToSignal()
			.Subscribe(x => CoordinatorUri = Settings.CoordinatorUri);

		this.WhenAnyValue(x => x.Settings.MaxCoinJoinMiningFeeRate)
			.ToSignal()
			.Subscribe(x => MaxCoinJoinMiningFeeRate = Settings.MaxCoinJoinMiningFeeRate);

		this.WhenAnyValue(x => x.Settings.AbsoluteMinInputCount)
			.ToSignal()
			.Subscribe(x => AbsoluteMinInputCount = Settings.AbsoluteMinInputCount);

		// The wallet appears when it is created or recovered, and its limit with it.
		UiContext.WalletRepository.WhenAnyValue(x => x.Wallet)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(wallet =>
			{
				_walletSettings = wallet?.Settings;

				if (_walletSettings is { } walletSettings)
				{
					MaxCoinjoinLeftover = walletSettings.MaxCoinjoinLeftover.ToString();
				}

				HasWallet = _walletSettings is not null;
			});
	}

	/// <summary>Whether the settings of the application cannot be changed. The dust forfeit limit is not one of them.</summary>
	public bool IsReadOnly => Settings.IsOverridden;

	public ApplicationSettings Settings { get; }

	private void ValidateCoordinatorUri(IValidationErrors errors)
	{
		var coordinatorUri = CoordinatorUri;

		if (string.IsNullOrWhiteSpace(coordinatorUri))
		{
			Settings.CoordinatorUri = "";
			return;
		}

		if (!Uri.TryCreate(coordinatorUri, UriKind.Absolute, out _))
		{
			errors.Add(ErrorSeverity.Error, "Invalid URI.");
			return;
		}

		Settings.CoordinatorUri = coordinatorUri;
	}

	private void ValidateMaxCoinJoinMiningFeeRate(IValidationErrors errors)
	{
		var maxCoinJoinMiningFeeRate = MaxCoinJoinMiningFeeRate;

		if (string.IsNullOrEmpty(maxCoinJoinMiningFeeRate))
		{
			return;
		}

		if (!decimal.TryParse(maxCoinJoinMiningFeeRate, out var maxCoinJoinMiningFeeRateDecimal))
		{
			errors.Add(ErrorSeverity.Error, "Invalid number.");
			return;
		}

		if (maxCoinJoinMiningFeeRateDecimal < Constants.MinRelayFeeRate.SatoshiPerByte)
		{
			errors.Add(ErrorSeverity.Error, $"Mining fee rate must be at least {Constants.MinRelayFeeRate.SatoshiPerByte} sat/vB");
			return;
		}

		Settings.MaxCoinJoinMiningFeeRate = maxCoinJoinMiningFeeRateDecimal.ToString(CultureInfo.InvariantCulture);
	}

	private void ValidateAbsoluteMinInputCount(IValidationErrors errors)
	{
		var absoluteMinInputCount = AbsoluteMinInputCount;

		if (string.IsNullOrEmpty(absoluteMinInputCount))
		{
			return;
		}

		if (!int.TryParse(absoluteMinInputCount, out var absoluteMinInputCountInt))
		{
			errors.Add(ErrorSeverity.Error, "Invalid number.");
			return;
		}

		if (absoluteMinInputCountInt < Constants.AbsoluteMinInputCount)
		{
			errors.Add(ErrorSeverity.Error, $"Absolute min input count should be at least {Constants.AbsoluteMinInputCount}");
			return;
		}

		Settings.AbsoluteMinInputCount = absoluteMinInputCountInt.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Reads a dust forfeit limit: an amount in BTC between nothing and <see cref="KeyManager.HighestMaxCoinjoinLeftover"/>.
	/// </summary>
	public static bool TryParseMaxCoinjoinLeftover(string? text, [NotNullWhen(true)] out Money? limit)
	{
		if (Money.TryParse(text, out var amount) && amount is not null && amount >= Money.Zero && amount <= KeyManager.HighestMaxCoinjoinLeftover)
		{
			limit = amount;
			return true;
		}

		limit = null;
		return false;
	}

	/// <summary>
	/// The dust forfeit limit is an amount in BTC, and there always is one. A valid value is written to the wallet file right away.
	/// </summary>
	private void ValidateMaxCoinjoinLeftover(IValidationErrors errors)
	{
		if (_walletSettings is not { } walletSettings)
		{
			return;
		}

		if (!TryParseMaxCoinjoinLeftover(MaxCoinjoinLeftover, out var limit))
		{
			errors.Add(ErrorSeverity.Error, $"Must be between 0 and {KeyManager.HighestMaxCoinjoinLeftover} BTC.");
			return;
		}

		if (limit != walletSettings.MaxCoinjoinLeftover)
		{
			walletSettings.MaxCoinjoinLeftover = limit;
			walletSettings.Save();
		}
	}
}
