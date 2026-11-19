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
	[AutoNotify] private Money _maxCoinjoinLeftover;
	[AutoNotify] private bool _nonPrivateCoinIsolation;

	public WalletSettingsModel(IServices services, KeyManager keyManager, bool isNewWallet = false)
	{
		_services = services;
		_keyManager = keyManager;

		_isNewWallet = isNewWallet;
		_isDirty = isNewWallet;

		_maxCoinjoinLeftover = _keyManager.MaxCoinjoinLeftover;
		_nonPrivateCoinIsolation = _keyManager.NonPrivateCoinIsolation;

		this.WhenAnyValue(
				x => x.MaxCoinjoinLeftover,
				x => x.NonPrivateCoinIsolation)
			.Skip(1)
			.Do(_ => SetValues())
			.Subscribe();
	}

	public int MinGapLimit => _keyManager.MinGapLimit;

	/// <summary>No block below this height concerns the wallet. Zero if the wallet does not know.</summary>
	public uint BirthHeight => _keyManager.GetBirthHeight()?.Height ?? 0;

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
		_keyManager.MaxCoinjoinLeftover = MaxCoinjoinLeftover;
		_keyManager.NonPrivateCoinIsolation = NonPrivateCoinIsolation;
		_isDirty = true;
	}
}
