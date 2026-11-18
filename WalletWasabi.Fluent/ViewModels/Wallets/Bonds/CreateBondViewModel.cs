using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>A month that coins can be locked until.</summary>
public record BondMonth(BondLocktime Locktime)
{
	public string Name => Locktime.Date.ToString("MMMM", CultureInfo.InvariantCulture);

	public override string ToString() => Name;
}

/// <summary>
/// The first step to a new fidelity bond: the user chooses the month that the coins are locked until. Every month has its
/// own address. The coins of the wallet that go there are chosen next.
/// </summary>
[NavigationMetaData(Title = "New Fidelity Bond", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class CreateBondViewModel : RoutableViewModel
{
	private readonly IWalletModel _walletModel;
	private readonly IReadOnlyList<BondLocktime> _locktimes;

	[AutoNotify] private int? _selectedYear;
	[AutoNotify] private BondMonth? _selectedMonth;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<BondMonth> _months = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _address;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private string? _unlockText;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasCoins;

	public CreateBondViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet) : base(uiContext)
	{
		_walletModel = walletModel;
		_locktimes = walletModel.Bonds.GetSelectableLocktimes();
		Years = _locktimes.Select(x => x.Date.Year).Distinct().ToList();

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: false);
		EnableBack = true;

		// The months of the year that can still be chosen. A month that was chosen stays chosen if the new year has it too.
		this.WhenAnyValue(x => x.SelectedYear)
			.Subscribe(year =>
			{
				var chosen = SelectedMonth?.Locktime.Date.Month;
				Months = _locktimes.Where(x => x.Date.Year == year).Select(x => new BondMonth(x)).ToList();
				SelectedMonth = Months.FirstOrDefault(x => x.Locktime.Date.Month == chosen);
			});

		this.WhenAnyValue(x => x.SelectedMonth)
			.Subscribe(month =>
			{
				Address = month is null ? null : walletModel.Bonds.GetAddress(month.Locktime).ToString();
				UnlockText = month is null ? null : DescribeLock(month.Locktime, DateTimeOffset.UtcNow);
			});

		var canContinue = this.WhenAnyValue(x => x.SelectedMonth, x => x.HasCoins, (month, hasCoins) => month is not null && hasCoins);

		// The coins that are locked are picked by the user, like the coins of any other payment.
		NextCommand = ReactiveCommand.Create(
			() =>
			{
				if (SelectedMonth is { } month)
				{
					Navigate().To().ManualControlDialog(walletModel, wallet, month.Locktime);
				}
			},
			canContinue);
	}

	/// <summary>The years that have a month to choose.</summary>
	public IReadOnlyList<int> Years { get; }

	/// <summary>
	/// Tells until when the coins are locked and how long that is from now.
	/// </summary>
	public static string DescribeLock(BondLocktime locktime, DateTimeOffset now) =>
		$"Locked until {BondItemViewModel.FormatDate(locktime.Date)}, 00:00 UTC. That is {DurationText.Between(now, locktime.Date)} from now.";

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		_walletModel.HasBalance
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.HasCoins)
			.DisposeWith(disposables);
	}
}
