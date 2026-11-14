using System.IO;
using System.Reactive.Linq;
using WalletWasabi.Bases;
using WalletWasabi.Serialization;
using Unit = System.Reactive.Unit;

namespace WalletWasabi.Fluent.Configuration;

public class UiConfig : ConfigBase
{
	private bool _privacyMode;
	private bool _isCustomChangeAddress;
	private string _windowState;
	private bool _oobe;
	private Version _lastVersionHighlightsDisplayed;
	private bool _autoPaste;
	private int _feeTarget;
	private double? _windowWidth;
	private double? _windowHeight;

	public UiConfig(
		string filePath,
		bool privacyMode,
		bool isCustomChangeAddress,
		string windowState,
		bool oobe,
		Version lastVersionHighlightsDisplayed,
		bool autoPaste,
		int feeTarget,
		double? windowWidth,
		double? windowHeight) : base(filePath)
	{
		_privacyMode = privacyMode;
		_isCustomChangeAddress = isCustomChangeAddress;
		_windowState = windowState;
		_oobe = oobe;
		_lastVersionHighlightsDisplayed = lastVersionHighlightsDisplayed;
		_autoPaste = autoPaste;
		_feeTarget = feeTarget;
		_windowWidth = windowWidth;
		_windowHeight = windowHeight;

		this.WhenAnyValue(
				x => x.AutoPaste,
				x => x.IsCustomChangeAddress,
				x => x.WindowState,
				x => x.Oobe,
				x => x.LastVersionHighlightsDisplayed,
				x => x.PrivacyMode,
				x => x.FeeTarget,
				(_, _, _, _, _, _, _) => Unit.Default)
			.Throttle(TimeSpan.FromMilliseconds(1000))
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ => ToFile());

		this.WhenAnyValue(
				x => x.WindowWidth,
				x => x.WindowHeight)
			.Throttle(TimeSpan.FromMilliseconds(1000))
			.ObserveOn(RxApp.TaskpoolScheduler)
			.Subscribe(_ => ToFile());
	}

	public UiConfig(string filePath)
		: this(
			filePath,
			privacyMode: false,
			isCustomChangeAddress: false,
			windowState: "Normal",
			oobe: true,
			lastVersionHighlightsDisplayed: new (2, 3, 1),
			autoPaste: false,
			feeTarget: 2,
			windowWidth: null,
			windowHeight: null)
	{
	}

	public bool Oobe
	{
		get => _oobe;
		set => RaiseAndSetIfChanged(ref _oobe, value);
	}

	public Version LastVersionHighlightsDisplayed
	{
		get => _lastVersionHighlightsDisplayed;
		set => RaiseAndSetIfChanged(ref _lastVersionHighlightsDisplayed, value);
	}

	public string WindowState
	{
		get => _windowState;
		internal set => RaiseAndSetIfChanged(ref _windowState, value);
	}

	public int FeeTarget
	{
		get => _feeTarget;
		internal set => RaiseAndSetIfChanged(ref _feeTarget, value);
	}

	public bool AutoPaste
	{
		get => _autoPaste;
		set => RaiseAndSetIfChanged(ref _autoPaste, value);
	}

	public bool IsCustomChangeAddress
	{
		get => _isCustomChangeAddress;
		set => RaiseAndSetIfChanged(ref _isCustomChangeAddress, value);
	}

	public bool PrivacyMode
	{
		get => _privacyMode;
		set => RaiseAndSetIfChanged(ref _privacyMode, value);
	}

	public double? WindowWidth
	{
		get => _windowWidth;
		internal set => RaiseAndSetIfChanged(ref _windowWidth, value);
	}

	public double? WindowHeight
	{
		get => _windowHeight;
		internal set => RaiseAndSetIfChanged(ref _windowHeight, value);
	}

	public static UiConfig LoadFile(string filePath)
	{
		try
		{
			using var cfgFile = File.Open(filePath, FileMode.Open, FileAccess.Read);
			var decoder = JsonDecoder.FromStream(UiConfigDecode.UiConfig(filePath));
			var decodingResult = decoder(cfgFile);
			return decodingResult.Match(cfg => cfg, error => throw new InvalidOperationException(error));
		}
		catch (FileNotFoundException)
		{
			var config = new UiConfig(filePath);
			config.ToFile();
			Logging.Logger.LogInfo($"File did not exist. Created at path: '{filePath}'.");
			return config;
		}
		catch (Exception ex)
		{
			var config = new UiConfig(filePath);
			config.ToFile();
			Logging.Logger.LogInfo($"{nameof(UiConfig)} file has been deleted because it was corrupted. Recreated default version at path: `{filePath}`.");
			Logging.Logger.LogWarning(ex);
			return config;
		}
	}

	protected override string EncodeAsJson() =>
		JsonEncoder.ToReadableString(this, UiConfigEncode.UiConfig);
}
