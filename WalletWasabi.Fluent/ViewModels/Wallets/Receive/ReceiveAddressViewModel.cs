using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Receive;

/// <summary>
/// Shows an address to receive a payment to, with its QR code. The Receive page opens it for the address that it just
/// generated and for an address of its list, one that was generated earlier and still awaits its payment.
/// </summary>
[NavigationMetaData(Title = "Receive Address", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class ReceiveAddressViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	public ReceiveAddressViewModel(UiContext uiContext, IWalletModel wallet, IAddress model) : base(uiContext)
	{
		_wallet = wallet;
		Model = model;
		Address = model.Text;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		EnableBack = false;

		CopyAddressCommand = ReactiveCommand.CreateFromTask(() =>
			UiContext.Clipboard.SetTextAsync(Address));

		// The page lies on top of the list of the addresses, and "OK" leads back there.
		NextCommand = BackCommand;

		QrCode = UiContext.QrCodeGenerator.Generate(model.Text.ToUpperInvariant());
	}

	public ICommand CopyAddressCommand { get; }

	public string Address { get; }

	public IObservable<bool[,]> QrCode { get; }

	private IAddress Model { get; }

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		// An address that was paid to, or that was hidden, is not to be given to anybody any more, so its page closes.
		// The address may also have gone while another page was shown on top of this one.
		_wallet.Addresses.Unused
			.ToObservableChangeSet()
			.Select(_ => Unit.Default)
			.StartWith(Unit.Default)
			.ObserveOn(RxApp.MainThreadScheduler)
			.Where(_ => !_wallet.Addresses.Unused.Contains(Model))
			.Take(1)
			.Subscribe(_ => Navigate().Back())
			.DisposeWith(disposables);

		base.OnNavigatedTo(isInHistory, disposables);
	}
}
