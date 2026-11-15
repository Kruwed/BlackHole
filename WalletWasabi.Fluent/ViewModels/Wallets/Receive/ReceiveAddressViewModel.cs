using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Windows.Input;
using DynamicData.Aggregation;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Receive;

/// <summary>
/// Shows an address to receive a payment to. This is the receive screen itself, with the address that was generated
/// when the screen was opened, and it is also the page of an address that was generated earlier and still awaits its payment.
/// </summary>
public partial class ReceiveAddressViewModel : RoutableViewModel
{
	private readonly IWalletModel _wallet;

	/// <param name="isNew">
	/// Whether the address was generated for this opening of the receive screen. Otherwise the page was opened from
	/// the list of the addresses that await their payment.
	/// </param>
	public ReceiveAddressViewModel(UiContext uiContext, IWalletModel wallet, IAddress model, bool isNew) : base(uiContext)
	{
		_wallet = wallet;
		Model = model;
		Address = model.Text;
		ShortenedAddress = model.ShortenedText;

		Title = isNew ? "Receive" : "Address Awaiting Payment";
		Caption = isNew
			? "A new address is generated every time this screen is opened. Give each address to one payer only: payments to the same address are linked to each other."
			: null;

		SetupCancel(enableCancel: false, enableCancelOnEscape: true, enableCancelOnPressed: true);

		EnableBack = !isNew;

		CopyAddressCommand = ReactiveCommand.CreateFromTask(() =>
			UiContext.Clipboard.SetTextAsync(Address));

		ShowOtherAddressesCommand = ReactiveCommand.Create(() => Navigate().To().ReceiveAddresses(_wallet));

		NextCommand = CancelCommand;

		QrCode = UiContext.QrCodeGenerator.Generate(model.Text.ToUpperInvariant());

		// The list is one step away from the receive screen. A page that was opened from the list goes back to it instead.
		OtherAddressesAwaitPayment = isNew
			? _wallet.Addresses.Unused
				.ToObservableChangeSet()
				.Filter(address => !Equals(address, Model))
				.Count()
				.Select(count => count > 0)
				.ObserveOn(RxApp.MainThreadScheduler)
			: Observable.Return(false);
	}

	public override string Title { get; protected set; }

	public string? Caption { get; }

	public ICommand CopyAddressCommand { get; }

	public ICommand ShowOtherAddressesCommand { get; }

	public string Address { get; }
	public string ShortenedAddress { get; }
	public bool AddressHasBeenShortened => Address != ShortenedAddress;

	public IObservable<bool[,]> QrCode { get; }

	/// <summary>Whether addresses other than the one of this page await their payment.</summary>
	public IObservable<bool> OtherAddressesAwaitPayment { get; }

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
