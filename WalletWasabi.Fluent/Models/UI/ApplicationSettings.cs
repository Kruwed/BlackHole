using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using NBitcoin;
using ReactiveUI;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using WalletWasabi.Bases;
using WalletWasabi.Client.Configuration;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;
using Unit = System.Reactive.Unit;
using WalletWasabi.Fluent.Configuration;

namespace WalletWasabi.Fluent.Models.UI;

[AppLifetime]
public partial class ApplicationSettings : ReactiveObject
{
	private const int ThrottleTime = 500;

	private readonly IServices _services;
	private readonly Subject<bool> _isRestartNeeded = new();
	private readonly string _persistentConfigFilePath;
	/// <summary>
	/// What the running application works with: the configuration that it was started with, except for what was applied
	/// without a restart since then. Settings that differ from it need a restart.
	/// </summary>
	private PersistentConfig _runningConfig;
	private readonly Config _config;
	private readonly UiConfig _uiConfig;

	// Bitcoin
	[AutoNotify] private Network _network;

	[AutoNotify] private string _bitcoinRpcUri;
	[AutoNotify] private string _bitcoinRpcCredentialString;

	// Coordinator
	[AutoNotify] private string _coordinatorUri;
	[AutoNotify] private string _maxCoinJoinMiningFeeRate;
	[AutoNotify] private string _absoluteMinInputCount;

	// General
	[AutoNotify] private bool _customChangeAddress;
	[AutoNotify] private TorMode _useTor;
	[AutoNotify] private bool _terminateTorOnExit;

	[AutoNotify] private bool _oobe;
	[AutoNotify] private Version _lastVersionHighlightsDisplayed;
	[AutoNotify] private WindowState _windowState;

	// Experimental
	[AutoNotify] private string[] _experimentalFeatures;

	// Nostr
	[AutoNotify] private string[] _nostrRelays;

	public ApplicationSettings(IServices services, PersistentConfig persistentConfig, Config config, UiConfig uiConfig)
	{
		_services = services;
		_persistentConfigFilePath = services.PersistentConfigFilePath;
		_runningConfig = persistentConfig;

		_config = config;
		_uiConfig = uiConfig;

		// Connections
		_useTor = Config.ObjectToTorMode(persistentConfig.UseTor);

		// Bitcoin
		_network = persistentConfig.Network;
		_bitcoinRpcUri = persistentConfig.BitcoinRpcUri;
		_bitcoinRpcCredentialString = persistentConfig.BitcoinRpcCredentialString;

		// Coordinator
		_coordinatorUri = persistentConfig.CoordinatorUri;

		_maxCoinJoinMiningFeeRate = persistentConfig.MaxCoinJoinMiningFeeRate.ToString(CultureInfo.InvariantCulture);
		_absoluteMinInputCount = persistentConfig.AbsoluteMinInputCount.ToString(CultureInfo.InvariantCulture);

		// General
		_customChangeAddress = uiConfig.IsCustomChangeAddress;
		_terminateTorOnExit = persistentConfig.TerminateTorOnExit;

		// Experimental
		_experimentalFeatures = persistentConfig.ExperimentalFeatures.ToArray();

		// Nostr
		_nostrRelays = persistentConfig.NostrRelays.ToArray();

		_oobe = uiConfig.Oobe;
		_lastVersionHighlightsDisplayed = uiConfig.LastVersionHighlightsDisplayed;

		_windowState = Enum.Parse<WindowState>(uiConfig.WindowState);
		SetupObservables();
	}

	private void SetupObservables()
	{
		// Save on change
		var configSaveTrigger1 =
			this.WhenAnyValue(
					x => x.Network,
					x => x.BitcoinRpcCredentialString,
					x => x.BitcoinRpcUri,
					x => x.UseTor,
					x => x.TerminateTorOnExit,
					(_, _, _, _, _) => Unit.Default)
				.Skip(1);
		var configSaveTrigger2 =
			this.WhenAnyValue(
					x => x.MaxCoinJoinMiningFeeRate,
					x => x.AbsoluteMinInputCount,
					x => x.CoordinatorUri,
					x => x.NostrRelays,
					(_, _, _, _) => Unit.Default)
				.Skip(1);

		Observable
			.Merge(configSaveTrigger1, configSaveTrigger2)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Throttle(TimeSpan.FromMilliseconds(ThrottleTime))
			.Do(_ => Save())
			.Subscribe();


		// Save UiConfig on change
		this.WhenAnyValue(
				x => x.CustomChangeAddress,
				x => x.Oobe,
				x => x.LastVersionHighlightsDisplayed,
				x => x.WindowState,
				(_, _, _, _) => Unit.Default)
			.Skip(1)
			.Throttle(TimeSpan.FromMilliseconds(ThrottleTime))
			.Do(_ => ApplyUiConfigChanges())
			.Subscribe();
	}

	public bool IsOverridden => _config.IsOverridden;

	/// <summary>Whether the node was given on the command line or in the environment, so that saving another one would have no effect.</summary>
	public bool IsBitcoinRpcOverridden => _config.IsBitcoinRpcOverridden;

	/// <summary>The same for the coordinator.</summary>
	public bool IsCoordinatorUriOverridden => _config.IsCoordinatorUriOverridden;

	/// <summary>The same for the Nostr relays.</summary>
	public bool IsNostrRelaysOverridden => _config.IsNostrRelaysOverridden;

	public IObservable<bool> IsRestartNeeded => _isRestartNeeded;

	public bool CheckIfRestartIsNeeded(PersistentConfig config)
	{
		return _runningConfig != config;
	}

	/// <summary>
	/// Stores the settings of the node that the application has just been connected to. Unlike a change of these settings
	/// on the settings page, this asks for no restart: the application works with that node already.
	/// </summary>
	public void SetConnectedBitcoinNode(Uri rpcUri, string rpcCredentialString)
	{
		// The address in the form in which it gets saved.
		var uri = rpcUri.ToString();

		_runningConfig = _runningConfig with { BitcoinRpcUri = uri, BitcoinRpcCredentialString = rpcCredentialString };

		BitcoinRpcUri = uri;
		BitcoinRpcCredentialString = rpcCredentialString;
	}

	/// <summary>
	/// Stores the Nostr relays that the application has just been connected to. Like <see cref="SetConnectedBitcoinNode"/>,
	/// this asks for no restart: the application posts to these relays already.
	/// </summary>
	public void SetConnectedNostrRelays(string[] relays)
	{
		_runningConfig = _runningConfig with { NostrRelays = new ValueList<string>(relays) };

		NostrRelays = relays;
	}

	/// <summary>
	/// Stores the coordinator that the application has just been connected to. Like <see cref="SetConnectedBitcoinNode"/>,
	/// this asks for no restart: the application coinjoins with that coordinator already.
	/// </summary>
	public void SetConnectedCoordinator(Uri coordinatorUri)
	{
		// The address in the form in which it gets saved.
		var uri = coordinatorUri.ToString();

		_runningConfig = _runningConfig with { CoordinatorUri = uri };

		CoordinatorUri = uri;
	}

	private void Save()
	{
		RxApp.MainThreadScheduler.Schedule(
			() =>
			{
				try
				{
					var loadedConfig = PersistentConfigManager.LoadFile(_persistentConfigFilePath);
					if (loadedConfig is not PersistentConfig currentConfig)
					{
						throw new NotSupportedException("Only configuration files after v2.5.1 are supported.");
					}
					var newConfig = ApplyChanges(currentConfig);
					PersistentConfigManager.ToFile(_persistentConfigFilePath, newConfig);
					PersistentConfigManager.UpdateNetwork(_persistentConfigFilePath, newConfig.Network);

					_isRestartNeeded.OnNext(CheckIfRestartIsNeeded(newConfig));
				}
				catch (Exception ex)
				{
					Logger.LogDebug(ex);
				}
			});
	}

	private PersistentConfig ApplyChanges(PersistentConfig config)
	{
		PersistentConfig result = config;

		// Bitcoin
		if (string.IsNullOrWhiteSpace(BitcoinRpcUri))
		{
			result = result with { BitcoinRpcUri = "", BitcoinRpcCredentialString = BitcoinRpcCredentialString };
		}
		else if (Uri.TryCreate(BitcoinRpcUri, UriKind.Absolute, out var uri))
		{
			result = result with { BitcoinRpcUri = uri.ToString(), BitcoinRpcCredentialString = BitcoinRpcCredentialString };
		}

		result = result with { CoordinatorUri = CoordinatorUri };

		result = result with
		{
			Network = Network,
			MaxCoinJoinMiningFeeRate = decimal.TryParse(MaxCoinJoinMiningFeeRate, out var maxCoinjoinMiningFeeRate) ?
				maxCoinjoinMiningFeeRate :
				Constants.DefaultMaxCoinJoinMiningFeeRate,
			AbsoluteMinInputCount = int.TryParse(AbsoluteMinInputCount, out var absoluteMinInputCount) ?
				absoluteMinInputCount :
				Constants.DefaultAbsoluteMinInputCount
		};

		// General
		result = result with
		{
			UseTor = UseTor.ToString(),
			TerminateTorOnExit = TerminateTorOnExit,
		};

		// Nostr
		result = result with { NostrRelays = new ValueList<string>(NostrRelays) };

		return result;
	}

	private void ApplyUiConfigChanges()
	{
		_uiConfig.IsCustomChangeAddress = CustomChangeAddress;
		_uiConfig.Oobe = Oobe;
		_uiConfig.LastVersionHighlightsDisplayed = LastVersionHighlightsDisplayed;
		_uiConfig.WindowState = WindowState.ToString();
	}
}
