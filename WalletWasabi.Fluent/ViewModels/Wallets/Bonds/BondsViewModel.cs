using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Fluent.Models.Wallets;
using WalletWasabi.Fluent.ViewModels.Navigation;
using WalletWasabi.Fluent.ViewModels.Wallets.Coins;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets.Bonds;

/// <summary>
/// The fidelity bonds of the wallet: coins that are locked until the chain reaches a block height. New ones are created
/// from here, out of the coins that are ticked on the Coins tab, and the ones whose unlock height the chain has reached
/// are redeemed from here.
/// </summary>
[NavigationMetaData(Title = "Fidelity Bonds", NavigationTarget = NavigationTarget.DialogScreen)]
public partial class BondsViewModel : RoutableViewModel
{
	private readonly IWalletModel _walletModel;

	[AutoNotify(SetterModifier = AccessModifier.Private)] private IReadOnlyList<BondItemViewModel> _bonds = [];
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _hasBonds;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private Amount? _lockedAmount;
	[AutoNotify(SetterModifier = AccessModifier.Private)] private bool _isChainHeightKnown = true;

	/// <param name="coins">The list of the Coins tab. A new bond locks the coins that are ticked in it.</param>
	public BondsViewModel(UiContext uiContext, IWalletModel walletModel, Wallet wallet, CoinListViewModel coins) : base(uiContext)
	{
		_walletModel = walletModel;

		SetupCancel(enableCancel: true, enableCancelOnEscape: true, enableCancelOnPressed: true);

		NextCommand = ReactiveCommand.Create(() => Navigate().To().CreateBond(walletModel, wallet, coins));
	}

	protected override void OnNavigatedTo(bool isInHistory, CompositeDisposable disposables)
	{
		base.OnNavigatedTo(isInHistory, disposables);

		// The bonds are read from all the transactions of the wallet, which is no work for the thread of the interface.
		_walletModel.Bonds.Changed
			.StartWith(Unit.Default)
			.Select(_ => Observable
				.FromAsync(cancellationToken => Task.Run(_walletModel.Bonds.GetBonds, cancellationToken))
				.Catch((Exception ex) =>
				{
					// The list stays as it is and is read again with the next change.
					Logger.LogError(ex);
					return Observable.Empty<FidelityBondsModel>();
				}))
			.Switch()
			.ObserveOn(RxApp.MainThreadScheduler)
			.Subscribe(Update)
			.DisposeWith(disposables);
	}

	private void Update(FidelityBondsModel model)
	{
		var now = DateTimeOffset.Now;

		// The bonds that still hold coins come first, the one that unlocks next on top. The redeemed ones follow.
		Bonds = model.Bonds
			.OrderBy(x => x.State is FidelityBondState.Redeeming or FidelityBondState.Redeemed)
			.ThenBy(x => x.Bond.UnlockHeight)
			.ThenByDescending(x => x.Bond.Amount)
			.Select(x => new BondItemViewModel(x, model.ChainHeight, now, Redeem))
			.ToList();

		HasBonds = Bonds.Count > 0;
		IsChainHeightKnown = model.ChainHeight is not null;

		var locked = model.Bonds.Where(x => !x.Bond.IsSpent).Sum(x => x.Bond.Amount);
		LockedAmount = locked > Money.Zero ? new Amount(locked) : null;
	}

	private void Redeem(FidelityBondModel bond) => Navigate().To().RedeemBond(_walletModel, bond);
}
