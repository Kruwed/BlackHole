using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Models.UI;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Helpers;
using WalletWasabi.WabiSabi.Client;

namespace WalletWasabi.Fluent.ViewModels.Settings;

/// <summary>
/// Where the user tells which coordinator the application coinjoins with, and what it accepts of a round. It comes
/// fourth on the welcome.
/// </summary>
/// <remarks>
/// The coordinator is the one thing that is connected to. The highest mining fee rate and the lowest number of
/// inputs are settings that take a restart, and the dust forfeit limit is kept with the wallet.
/// </remarks>
[NavigationMetaData(Title = "Connect to Coordinator")]
public partial class ConnectToCoordinatorViewModel : ConnectionPageViewModel
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

	public ConnectToCoordinatorViewModel(UiContext uiContext) : base(uiContext, Connection.Coordinator)
	{
		ExampleUri = GetExampleUri(uiContext.Services.GetNetwork());
		IsReadOnly = Settings.IsCoordinatorUriOverridden;

		// A coordinator that was given at the start is not in the saved settings. It is shown, because it is the one in use.
		_coordinatorUri = IsReadOnly ? uiContext.Services.Config.CoordinatorUri : Settings.CoordinatorUri;
		_maxCoinJoinMiningFeeRate = Settings.MaxCoinJoinMiningFeeRate;
		_absoluteMinInputCount = Settings.AbsoluteMinInputCount;

		this.ValidateProperty(x => x.CoordinatorUri, ValidateCoordinatorUri);
		this.ValidateProperty(x => x.MaxCoinJoinMiningFeeRate, ValidateMaxCoinJoinMiningFeeRate);
		this.ValidateProperty(x => x.AbsoluteMinInputCount, ValidateAbsoluteMinInputCount);
		this.ValidateProperty(x => x.MaxCoinjoinLeftover, ValidateMaxCoinjoinLeftover);

		// What a coordinator answered says nothing about another address.
		this.WhenAnyValue(x => x.CoordinatorUri)
			.Skip(1)
			.Subscribe(_ => ForgetAnswer());

		SetUpCommands(canAsk: this.WhenAnyValue(x => x.CoordinatorUri, uri => CoordinatorConnectionCheck.TryParseUri(uri, out _)));
	}

	/// <summary>
	/// A coordinator that was given on the command line or in the environment wins over the saved one, so saving another one would have no effect.
	/// </summary>
	public bool IsReadOnly { get; }

	/// <summary>What the address of a coordinator looks like.</summary>
	public string ExampleUri { get; }

	/// <summary>
	/// Gives an address that shows what the address of a coordinator looks like. On regtest it is where the coordinator
	/// of this repository listens when it runs on this computer; elsewhere there is no coordinator to point at.
	/// </summary>
	public static string GetExampleUri(Network network) =>
		network == Network.RegTest ? Constants.RegTestCoordinatorUri : "https://coordinator.example/";

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

	private void ValidateCoordinatorUri(IValidationErrors errors)
	{
		if (!string.IsNullOrWhiteSpace(CoordinatorUri) && !CoordinatorConnectionCheck.TryParseUri(CoordinatorUri, out _))
		{
			errors.Add(ErrorSeverity.Error, $"The address must look like {ExampleUri}.");
		}
	}

	/// <summary>A valid rate is saved right away. It applies from the next start on.</summary>
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

	/// <summary>A valid count is saved right away. It applies from the next start on.</summary>
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

	protected override string GetWaitingMessage(bool isConnecting) =>
		isConnecting ? "Connecting to the coordinator…" : "Asking the coordinator…";

	protected override async Task<ConnectionAnswer> VerifyAsync(CancellationToken cancellationToken)
	{
		if (!CoordinatorConnectionCheck.TryParseUri(CoordinatorUri, out var coordinatorUri))
		{
			return new ConnectionAnswer(false, $"The address must look like {ExampleUri}.");
		}

		var result = await UiContext.Services.VerifyCoordinatorAsync(coordinatorUri.ToString(), cancellationToken);

		return result.IsOk
			? new ConnectionAnswer(true, "The coordinator answers.")
			: new ConnectionAnswer(false, result.Error);
	}

	protected override async Task<ConnectionAnswer> ConnectAsync(CancellationToken cancellationToken)
	{
		if (!CoordinatorConnectionCheck.TryParseUri(CoordinatorUri, out var coordinatorUri))
		{
			return new ConnectionAnswer(false, $"The address must look like {ExampleUri}.");
		}

		var result = await UiContext.Services.ConnectToCoordinatorAsync(coordinatorUri.ToString(), cancellationToken);

		if (!result.IsOk)
		{
			return new ConnectionAnswer(false, result.Error);
		}

		if (!IsReadOnly)
		{
			Settings.SetConnectedCoordinator(coordinatorUri);
		}

		return new ConnectionAnswer(true, $"Connected. {Constants.AppName} coinjoins with this coordinator from now on.");
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

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
			})
			.DisposeWith(disposables);
	}
}
