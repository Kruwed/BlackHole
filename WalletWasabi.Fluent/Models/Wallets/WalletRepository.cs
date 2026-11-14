using Avalonia.Threading;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.Models.Wallets;

/// <summary>
/// Gives access to the wallet of the application. There is at most one wallet: it appears when the user creates or recovers it.
/// </summary>
public partial class WalletRepository : ReactiveObject
{
	private readonly IServices _services;

	public WalletRepository(IServices services)
	{
		_services = services;

		if (services.WalletManager.Wallet is { } wallet)
		{
			Wallet = new WalletModel(services, wallet);
		}

		services.WalletManager.WalletAdded += (_, addedWallet) =>
		{
			// The wallet can also be added from outside of the user interface (the RPC server). Its model belongs to the UI thread.
			if (Dispatcher.UIThread.CheckAccess())
			{
				Wallet = new WalletModel(_services, addedWallet);
			}
			else
			{
				Dispatcher.UIThread.Post(() => Wallet = new WalletModel(_services, addedWallet));
			}
		};
	}

	/// <summary>The wallet, once it was created or recovered.</summary>
	// AutoInterfaces (such as IWalletModel) cannot be seen by AutoNotifyGenerator.
	public IWalletModel? Wallet
	{
		get;
		private set => this.RaiseAndSetIfChanged(ref field, value);
	}

	public bool HasWallet => _services.HasWallet();

	public async Task<WalletSettingsModel> NewWalletAsync(WalletCreationOptions options, CancellationToken? cancelToken = null)
	{
		return options switch
		{
			WalletCreationOptions.AddNewWallet add => await CreateNewWalletAsync(add),
			WalletCreationOptions.RecoverWallet recover => await RecoverWalletAsync(recover),
			_ => throw new InvalidOperationException($"{nameof(WalletCreationOptions)} not supported: {options?.GetType().Name}")
		};
	}

	/// <summary>
	/// Makes the newly created or recovered wallet the wallet of the application.
	/// </summary>
	/// <remarks>Must be called on the UI thread.</remarks>
	public IWalletModel SaveWallet(WalletSettingsModel walletSettings)
	{
		walletSettings.Save();

		var result = Wallet ?? throw new InvalidOperationException("The wallet was not added.");
		result.Settings.IsCoinJoinPaused = walletSettings.IsCoinJoinPaused;
		return result;
	}

	private async Task<WalletSettingsModel> CreateNewWalletAsync(WalletCreationOptions.AddNewWallet options)
	{
		var backup = options.Backup;

		ArgumentNullException.ThrowIfNull(backup);
		ArgumentNullException.ThrowIfNull(backup.Password);

		var keyManager = await Task.Run(
				() =>
				{
					// Nothing concerns a new wallet up to the tip of the chain. If the node did not tell it yet, the wallet learns it when it reaches the node.
					var walletGenerator = new WalletGenerator(_services.GetNetwork())
					{
						TipHeight = _services.GetNodeTipHeight() is var tipHeight and > 0 ? new Height.ChainHeight(tipHeight) : null
					};

					return walletGenerator.GenerateWallet(
						backup.Password,
						backup.Shares.Take(backup.Settings.Threshold).ToArray());
				});

		return new WalletSettingsModel(_services, keyManager, true);
	}

	private async Task<WalletSettingsModel> RecoverWalletAsync(WalletCreationOptions.RecoverWallet options)
	{
		var (backup, minGapLimit, birthHeight) = options;

		ArgumentNullException.ThrowIfNull(minGapLimit);
		ArgumentNullException.ThrowIfNull(backup);

		// The wallet file is only written later, when the wallet is saved at the end of the UI workflow.
		var keyManager = await Task.Run(() =>
			KeyManager.Recover(
				backup.Shares,
				backup.Password,
				_services.GetNetwork(),
				"",
				minGapLimit.Value,
				birthHeight: birthHeight));

		return new WalletSettingsModel(_services, keyManager, true, true);
	}
}
