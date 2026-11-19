using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using NBitcoin;
using ReactiveUI;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Fluent.Helpers;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class CoinModel : ReactiveObject
{
	private readonly IServices _services;
	private bool _subscribedToCoinChanges;

	[AutoNotify] private bool _isExcludedFromCoinJoin;
	[AutoNotify] private bool _isCoinJoinInProgress;
	[AutoNotify] private bool _isBanned;
	[AutoNotify] private string? _bannedUntilUtcToolTip;
	[AutoNotify] private string? _confirmedToolTip;
	[AutoNotify] private uint _confirmations;
	[AutoNotify] private bool _isConfirmed;

	public CoinModel(SmartCoin coin, Network network, IServices services)
	{
		_services = services;
		Coin = coin;
		Amount = coin.Amount;
		BtcAddress = coin.ScriptPubKey.GetDestinationAddress(network)?.ToString();

		Key = coin.Outpoint.GetHashCode();
		BannedUntilUtc = coin.BannedUntilUtc;

		IsExcludedFromCoinJoin = coin.IsExcludedFromCoinJoin;
		IsConfirmed = coin.Confirmed;
		AnonymityPoints = coin.AnonymityPoints;
		IsCoinJoinInProgress = coin.CoinJoinInProgress;
		IsBanned = coin.IsBanned;
		BannedUntilUtcToolTip = $"Can't participate in coinjoin until: {coin.BannedUntilUtc}";

		var confirmations = coin.GetConfirmations(services.GetTipHeight());
		Confirmations = confirmations;
		ConfirmedToolTip = TextHelpers.GetConfirmationText(confirmations);
	}

	private SmartCoin Coin { get; }

	public Money Amount { get; }

	public string? BtcAddress { get; }

	public int Key { get; }

	public DateTimeOffset? BannedUntilUtc { get; }

	/// <summary>The anonymity points of the coin when this model of it was made, or null if they were not known then.</summary>
	public int? AnonymityPoints { get; }

	/// <summary>Subscribes to property changes of underlying SmartCoin.</summary>
	/// <remarks>This method is not thread safe. Make sure it's not called concurrently.</remarks>
	public void SubscribeToCoinChanges(CompositeDisposable disposable)
	{
		if (_subscribedToCoinChanges)
		{
			return;
		}

		disposable.Add(Disposable.Create(() => _subscribedToCoinChanges = false));

		this.WhenAnyValue(c => c.Coin.IsExcludedFromCoinJoin).BindTo(this, x => x.IsExcludedFromCoinJoin).DisposeWith(disposable);
		this.WhenAnyValue(c => c.Coin.Confirmed).BindTo(this, x => x.IsConfirmed).DisposeWith(disposable);
		this.WhenAnyValue(c => c.Coin.CoinJoinInProgress).BindTo(this, x => x.IsCoinJoinInProgress).DisposeWith(disposable);
		this.WhenAnyValue(c => c.Coin.BannedUntilUtc).Select(_ => Coin.IsBanned).BindTo(this, x => x.IsBanned).DisposeWith(disposable);
		this.WhenAnyValue(c => c.Coin.BannedUntilUtc).WhereNotNull().Subscribe(x => BannedUntilUtcToolTip = $"Can't participate in coinjoin until: {x:g}").DisposeWith(disposable);

		this.WhenAnyValue(c => c.Coin.Height).Select(_ => Coin.GetConfirmations(_services.GetTipHeight())).Subscribe(
			confirmations =>
			{
				Confirmations = confirmations;
				ConfirmedToolTip = TextHelpers.GetConfirmationText(confirmations);
			}).DisposeWith(disposable);

		_subscribedToCoinChanges = true;
	}

	public bool IsSameAddress(CoinModel anotherCoin) => anotherCoin.Coin.HdPubKey == Coin.HdPubKey;

	public bool IsSame(CoinModel anotherCoin) => anotherCoin.Coin.Outpoint == Coin.Outpoint;

	// TODO: Leaky abstraction. This shouldn't exist.
	public SmartCoin GetSmartCoin() => Coin;
}
