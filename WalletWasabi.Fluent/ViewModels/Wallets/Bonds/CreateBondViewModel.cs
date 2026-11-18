using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.Validation;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>
/// The first step to a new fidelity bond: the user names the block height that the coins are locked until. Every height
/// has its own address. The coins of the wallet that go there are chosen next.
/// </summary>
[NavigationMetaData(Title = "New Fidelity Bond", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class CreateBondViewModel : RoutableViewModel
{
	/// <summary>The address of a height shows once the typing has stopped for this long: the wallet file remembers every height that it shows an address for.</summary>
	private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(600);

	private readonly IWalletModel _walletModel;

	[AutoNotify] private string _unlockHeightText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private uint? _unlockHeight;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _lockText;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _address;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string _chainText = "";
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isChainHeightKnown;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasCoins;

	public CreateBondViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet) : base(uiContext)
	{
		_walletModel = walletModel;

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: false);
		EnableBack = true;

		this.ValidateProperty(x => x.UnlockHeightText, ValidateUnlockHeight);

		this.WhenAnyValue(x => x.UnlockHeightText)
			.Subscribe(_ => Update());

		var canContinue = this.WhenAnyValue(
			x => x.UnlockHeight,
			x => x.Address,
			x => x.HasCoins,
			(unlockHeight, address, hasCoins) => unlockHeight is not null && address is not null && hasCoins);

		// The coins that are locked are picked by the user, like the coins of any other payment.
		NextCommand = ReactiveCommand.Create(
			() =>
			{
				if (UnlockHeight is { } unlockHeight)
				{
					Navigate().To().ManualControlDialog(walletModel, wallet, unlockHeight);
				}
			},
			canContinue);
	}

	/// <summary>Reads a block height as it is typed: digits, with or without separators between the thousands.</summary>
	public static uint? ParseHeight(string text)
	{
		var digits = text.Replace(",", "").Replace(" ", "").Replace(" ", "");

		return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var height) ? height : null;
	}

	/// <summary>Tells until when the coins are locked and how long that is from now, as far as the node tells how far the chain is.</summary>
	public static string DescribeLock(IWalletModel walletModel, uint unlockHeight) =>
		walletModel.Bonds.ChainHeight is { } chainHeight
			? BlockHeightText.DescribeLock(unlockHeight, chainHeight, DateTimeOffset.Now)
			: $"Locked until block {BlockHeightText.Format(unlockHeight)}.";

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		_walletModel.HasBalance
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.HasCoins)
			.DisposeWith(disposables);

		// Every block brings the unlock height nearer, and may reach it.
		_walletModel.Bonds.Changed
			.StartWith(Unit.Default)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(_ =>
			{
				Update();
				this.RaisePropertyChanged(nameof(UnlockHeightText));
			})
			.DisposeWith(disposables);

		// The wallet watches the address of a height before it shows it, and the wallet file remembers the height. So
		// the address waits until the typing has stopped, and the numbers on the way to the height are not remembered.
		this.WhenAnyValue(x => x.UnlockHeight)
			.Do(_ => Address = null)
			.Throttle(TypingPause, RxApp.TaskpoolScheduler)
			.Select(unlockHeight => Observable.FromAsync(() => Task.Run(() => GetAddress(unlockHeight))))
			.Switch()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(address => Address = address)
			.DisposeWith(disposables);
	}

	private string? GetAddress(uint? unlockHeight)
	{
		try
		{
			return unlockHeight is { } height ? _walletModel.Bonds.GetAddress(height).ToString() : null;
		}
		catch (Exception ex)
		{
			// An address that the wallet file could not remember is not shown.
			Logger.LogError(ex);
			return null;
		}
	}

	/// <summary>Takes the height out of the text if coins can be locked until it, and tells what that means.</summary>
	private void Update()
	{
		var chainHeight = _walletModel.Bonds.ChainHeight;

		IsChainHeightKnown = chainHeight is not null;
		ChainText = chainHeight is { } known
			? $"The chain is at block {BlockHeightText.Format(known)} now."
			: "The Bitcoin node does not answer. Without it there is no telling how far the chain is.";

		var unlockHeight = chainHeight is { } height && ParseHeight(UnlockHeightText) is { } parsed && WalletBondsModel.ValidateUnlockHeight(parsed, height) is null
			? parsed
			: (uint?)null;

		UnlockHeight = unlockHeight;
		LockText = unlockHeight is { } valid && chainHeight is { } current
			? BlockHeightText.DescribeLock(valid, current, DateTimeOffset.Now)
			: null;
	}

	private void ValidateUnlockHeight(IValidationErrors errors)
	{
		if (string.IsNullOrWhiteSpace(UnlockHeightText))
		{
			return;
		}

		if (ParseHeight(UnlockHeightText) is not { } unlockHeight)
		{
			errors.Add(ErrorSeverity.Error, "A block height is a whole number.");
		}
		else if (_walletModel.Bonds.ChainHeight is { } chainHeight && WalletBondsModel.ValidateUnlockHeight(unlockHeight, chainHeight) is { } reason)
		{
			errors.Add(ErrorSeverity.Error, reason);
		}
	}
}
