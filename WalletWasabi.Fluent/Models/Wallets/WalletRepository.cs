using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Fluent.Extensions;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

public partial class WalletRepository : ReactiveObject
{
	private readonly IServices _services;
	private readonly Dictionary<WalletId, WalletModel> _walletDictionary = new();
	private readonly CompositeDisposable _disposable = new();

	public WalletRepository(IServices services)
	{
		_services = services;

		var signals =
			Observable.FromEventPattern<Wallet>(services.WalletManager, nameof(WalletManager.WalletAdded))
					  .Select(_ => System.Reactive.Unit.Default)
					  .StartWith(System.Reactive.Unit.Default);

		Wallets =
			signals.Fetch(() => services.GetWallets(), x => x.WalletId)
				   .DisposeWith(_disposable)
				   .Connect()
				   .TransformWithInlineUpdate(CreateWalletModel, (_, _) => { })
				   .Cast(x => (IWalletModel)x)
				   .AsObservableCache()
				   .DisposeWith(_disposable);

		DefaultWalletName = services.GetLastSelectedWallet();
	}

	public IObservableCache<IWalletModel, WalletId> Wallets { get; }

	public string? DefaultWalletName { get; }
	public bool HasWallet => _services.HasWallet();

	private KeyPath AccountKeyPath => KeyManager.GetAccountKeyPath(_services.GetNetwork(), ScriptPubKeyType.Segwit);

	public void StoreLastSelectedWallet(IWalletModel wallet)
	{
		_services.SetLastSelectedWallet(wallet.Name);
	}

	public string GetNextWalletName()
	{
		return _services.GetNextWalletName("Wallet");
	}

	public async Task<WalletSettingsModel> NewWalletAsync(WalletCreationOptions options, CancellationToken? cancelToken = null)
	{
		return options switch
		{
			WalletCreationOptions.AddNewWallet add => await CreateNewWalletAsync(add),
			WalletCreationOptions.RecoverWallet recover => await RecoverWalletAsync(recover),
			_ => throw new InvalidOperationException($"{nameof(WalletCreationOptions)} not supported: {options?.GetType().Name}")
		};
	}

	public IWalletModel SaveWallet(WalletSettingsModel walletSettings)
	{
		var id = walletSettings.Save();
		var result = GetById(id);
		result.Settings.IsCoinJoinPaused = walletSettings.IsCoinJoinPaused;
		return result;
	}

	public (ErrorSeverity Severity, string Message)? ValidateWalletName(string walletName)
	{
		return _services.ValidateWalletName(walletName);
	}

	private async Task<WalletSettingsModel> CreateNewWalletAsync(WalletCreationOptions.AddNewWallet options)
	{
		var (walletName, backup) = options;

		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentNullException.ThrowIfNull(backup);
		ArgumentNullException.ThrowIfNull(backup.Password);

		var keyManager = await Task.Run(
				() =>
				{
					var walletGenerator = new WalletGenerator(
						_services.GetWalletsDir(),
						_services.GetNetwork())
					{
						TipHeight = _services.GetTipHeight()
					};

					return walletGenerator.GenerateWallet(
						walletName,
						backup.Password,
						backup.Shares.Take(backup.Settings.Threshold).ToArray());
				});

		return new WalletSettingsModel(_services, keyManager, true);
	}

	private async Task<WalletSettingsModel> RecoverWalletAsync(WalletCreationOptions.RecoverWallet options)
	{
		var (walletName, backup, minGapLimit, birthHeight) = options;

		ArgumentException.ThrowIfNullOrEmpty(walletName);
		ArgumentNullException.ThrowIfNull(minGapLimit);
		ArgumentNullException.ThrowIfNull(backup);

		var keyManager = await Task.Run(() =>
		{
			var walletFilePath = _services.GetWalletFilePath(walletName);

			var result = KeyManager.Recover(
				backup.Shares,
				backup.Password,
				_services.GetNetwork(),
				AccountKeyPath,
				null,
				"", // Make sure it is not saved into a file yet.
				minGapLimit.Value,
				birthHeight: birthHeight);

			// Set the filepath but we will only write the file later when the Ui workflow is done.
			result.SetFilePath(walletFilePath);

			return result;
		});

		return new WalletSettingsModel(_services, keyManager, true, true);
	}

	private IWalletModel GetById(WalletId id)
	{
		return
			_walletDictionary.TryGetValue(id, out var wallet)
			? wallet
			: throw new InvalidOperationException($"Wallet not found: {id}");
	}

	private WalletModel CreateWalletModel(Wallet wallet)
	{
		if (_walletDictionary.TryGetValue(wallet.WalletId, out var existing))
		{
			if (!ReferenceEquals(existing.Wallet, wallet))
			{
				throw new InvalidOperationException($"Different instance of: {wallet.WalletName}");
			}
			return existing;
		}

		var result = new WalletModel(_services, wallet);

		_walletDictionary[wallet.WalletId] = result;

		return result;
	}
}
