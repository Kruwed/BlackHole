using NBitcoin;
using ReactiveUI;
using System.Reactive.Linq;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Fluent.Infrastructure;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

[AppLifetime]
public partial class WalletSettingsModel : ReactiveObject
{
	private readonly IServices _services;
	private readonly KeyManager _keyManager;
	private bool _isDirty;

	[AutoNotify] private bool _isNewWallet;
	[AutoNotify] private bool _autoCoinjoin;
	[AutoNotify] private Money _plebStopThreshold;
	[AutoNotify] private int _anonScoreTarget;
	[AutoNotify] private bool _nonPrivateCoinIsolation;
	[AutoNotify] private bool _onlyUsePrivateFundsForPayments;

	public WalletSettingsModel(IServices services, KeyManager keyManager, bool isNewWallet = false, bool isCoinJoinPaused = false)
	{
		_services = services;
		_keyManager = keyManager;

		_isNewWallet = isNewWallet;
		_isDirty = isNewWallet;
		IsCoinJoinPaused = isCoinJoinPaused;

		_autoCoinjoin = _keyManager.AutoCoinJoin;
		_plebStopThreshold = _keyManager.PlebStopThreshold ?? KeyManager.DefaultPlebStopThreshold;
		_anonScoreTarget = _keyManager.AnonScoreTarget;
		_nonPrivateCoinIsolation = _keyManager.NonPrivateCoinIsolation;
		_onlyUsePrivateFundsForPayments = _keyManager.OnlyUsePrivateFundsForPayments;

		this.WhenAnyValue(
				x => x.AutoCoinjoin,
				x => x.PlebStopThreshold,
				x => x.AnonScoreTarget,
				x => x.NonPrivateCoinIsolation,
				x => x.OnlyUsePrivateFundsForPayments)
			.Skip(1)
			.Do(_ => SetValues())
			.Subscribe();
	}

	public int MinGapLimit => _keyManager.MinGapLimit;

	public bool IsCoinJoinPaused { get; set; }

	/// <summary>
	/// Saves the current configuration to the wallet file. A new wallet becomes the wallet of the application by that.
	/// </summary>
	public void Save()
	{
		if (!_isDirty)
		{
			return;
		}

		if (IsNewWallet)
		{
			// Adding the wallet writes its file.
			_services.AddWallet(_keyManager);
			IsNewWallet = false;
		}
		else
		{
			_keyManager.ToFile();
		}

		_isDirty = false;
	}

	private void SetValues()
	{
		_keyManager.AutoCoinJoin = AutoCoinjoin;
		_keyManager.PlebStopThreshold = PlebStopThreshold;
		_keyManager.AnonScoreTarget = AnonScoreTarget;
		_keyManager.NonPrivateCoinIsolation = NonPrivateCoinIsolation;
		_keyManager.OnlyUsePrivateFundsForPayments = OnlyUsePrivateFundsForPayments;
		_isDirty = true;
	}

	public void RescanWallet(uint startingHeight, int minGapLimit)
	{
		_keyManager.SetResyncParameters(startingHeight + Constants.ResyncHeightMargin, minGapLimit);
	}
}
