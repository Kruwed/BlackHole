using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using DynamicData.Aggregation;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Wallets.Receive;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Tabs;

/// <summary>
/// The tab that receives: a button that generates a new address, and below it the addresses that were generated and
/// have not been paid to yet.
/// </summary>
[AppLifetime]
public partial class ReceiveTabViewModel : WalletTabViewModel
{
	public const string TabTitle = "Receive";

	private readonly IWalletModel _wallet;

	[AutoNotify] private FlatTreeDataGridSource<AddressViewModel> _source = new(Enumerable.Empty<AddressViewModel>());
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasAddresses;

	public ReceiveTabViewModel(UiContext uiContext, IWalletModel wallet, Action<WalletTabViewModel> select) : base(uiContext, TabTitle, select)
	{
		_wallet = wallet;

		// Every address is one that was not given out before. It is shown right away, and it joins the list below.
		GenerateCommand = ReactiveCommand.Create(() => ShowAddress(_wallet.Addresses.NextReceiveAddress()));
	}

	/// <summary>Generates a new address and shows it.</summary>
	public ICommand GenerateCommand { get; }

	protected override void OnActivated(CompositeDisposable disposables)
	{
		base.OnActivated(disposables);

		_wallet.Addresses.Unused
			.ToObservableChangeSet()
			.Transform(address => new AddressViewModel(UiContext, ShowAddress, address))
			.DisposeMany()
			.Bind(out var unusedAddresses)
			.Subscribe()
			.DisposeWith(disposables);

		_wallet.Addresses.Unused
			.ToObservableChangeSet()
			.Count()
			.Select(count => count > 0)
			.ObserveOn(RxApp.MainThreadScheduler)
			.BindTo(this, x => x.HasAddresses)
			.DisposeWith(disposables);

		var source = ReceiveAddressesDataGridSource.Create(unusedAddresses);
		source.RowSelection!.SingleSelect = true;
		source.DisposeWith(disposables);

		Source = source;
	}

	private void ShowAddress(IAddress address)
	{
		UiContext.Navigate().To().ReceiveAddress(_wallet, address);
	}
}
