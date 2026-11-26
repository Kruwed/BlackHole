using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Services;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

[AppLifetime]
public class AddressesModel
{
	private readonly ISubject<HdPubKey> _newAddressGenerated = new Subject<HdPubKey>();
	private readonly Wallet _wallet;
	private readonly SourceList<HdPubKey> _source;

	public AddressesModel(IServices services, Wallet wallet)
	{
		_wallet = wallet;
		_source = new SourceList<HdPubKey>();
		_source.AddRange(GetUnusedKeys());

		// The wallet tells about its transactions on the thread that processes them. The list of the addresses is shown
		// on the screen, so it must only change on the thread of the user interface: a list that lost an address in the
		// moment in which the user generated another one brought the application down.
		services.EventBus.AsObservable<WalletRelevantTransactionProcessed>()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Do(_ => UpdateUnusedKeys())
			.Subscribe();

		_newAddressGenerated
			.Do(address => _source.Add(address))
			.Subscribe();

		_source.Connect()
			.Transform(key => (IAddress) new Address(_wallet.KeyManager, key, Hide))
			.Bind(out var unusedAddresses)
			.Subscribe();

		Unused = unusedAddresses;
	}

	/// <summary>The addresses that were given out and that nobody has paid to yet.</summary>
	private IEnumerable<HdPubKey> GetUnusedKeys() => _wallet.KeyManager.GetKeys(x => x is { IsInternal: false, KeyState: KeyState.Clean, IsHandedOut: true });

	/// <summary>
	/// Whether the wallet gives out a new address at the moment. A wallet that came back from its backup does not until
	/// it has gone through the chain: before that it does not know which of its addresses were used.
	/// </summary>
	public bool CanGiveOutAddress => !_wallet.KeyManager.IsSearchingForAddresses;

	/// <summary>Gives out an address that was not given out before.</summary>
	/// <exception cref="InvalidOperationException">The wallet gives out no address at the moment, see <see cref="CanGiveOutAddress"/>.</exception>
	public IAddress NextReceiveAddress()
	{
		var pubKey = _wallet.GetNextReceiveAddress();
		var nextReceiveAddress = new Address(_wallet.KeyManager, pubKey, Hide);
		_newAddressGenerated.OnNext(pubKey);

		return nextReceiveAddress;
	}

	public ReadOnlyObservableCollection<IAddress> Unused { get; }

	public void Hide(Address address)
	{
		_wallet.KeyManager.SetKeyState(KeyState.Locked, address.HdPubKey);
		_wallet.KeyManager.ToFile();
		_source.Remove(address.HdPubKey);
	}

	private void UpdateUnusedKeys()
	{
		var itemsToRemove = _source.Items
			.Where(item => item.KeyState != KeyState.Clean)
			.ToList();

		foreach (var item in itemsToRemove)
		{
			_source.Remove(item);
		}
	}
}
