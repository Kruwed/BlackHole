using Nito.AsyncEx;
using System.IO;
using static WalletWasabi.Logging.LoggerTools;

namespace WalletWasabi.Wallets;

/// <summary>
/// Holds the wallet of the application. There is at most one: it is created or recovered once and it lives in a single wallet file.
/// </summary>
public class WalletManager
{
	/// <remarks>All access must be guarded by <see cref="_lock"/> object.</remarks>
	private volatile bool _disposedValue = false;

	public WalletManager(
		Network network,
		WalletDirectories walletDirectories,
		WalletFactory createWallet)
	{
		Network = network;
		WalletDirectories = walletDirectories;
		_createWallet = createWallet;
		_cancelAllTasksToken = _cancelAllTasks.Token;

		LoadWalletFromFileSystem();
	}

	/// <summary>
	/// Triggered when the wallet is created or recovered. The sender of the event is the WalletManager and the argument is the wallet.
	/// </summary>
	public event EventHandler<Wallet>? WalletAdded;

	/// <summary>Cancels initialization of the wallet.</summary>
	private readonly CancellationTokenSource _cancelAllTasks = new();

	/// <summary>Token from <see cref="_cancelAllTasks"/>.</summary>
	/// <remarks>Accessing the token of <see cref="_cancelAllTasks"/> can lead to <see cref="ObjectDisposedException"/>. So we copy the token and no exception can be thrown.</remarks>
	private readonly CancellationToken _cancelAllTasksToken;

	/// <remarks>All access must be guarded by <see cref="_lock"/> object.</remarks>
	private Wallet? _wallet;

	private readonly Lock _lock = new();
	private readonly AsyncLock _startStopWalletLock = new();

	private readonly WalletFactory _createWallet;
	public Network Network { get; }
	public WalletDirectories WalletDirectories { get; }

	/// <summary>The wallet, if it was created or recovered already.</summary>
	public Wallet? Wallet
	{
		get
		{
			lock (_lock)
			{
				return _wallet;
			}
		}
	}

	public bool HasWallet => Wallet is not null;

	private void LoadWalletFromFileSystem()
	{
		string walletFilePath = WalletDirectories.WalletFilePath;

		if (!File.Exists(walletFilePath))
		{
			return;
		}

		try
		{
			var wallet = _createWallet(KeyManager.FromFile(walletFilePath));

			lock (_lock)
			{
				_wallet = wallet;
			}
		}
		catch (Exception ex)
		{
			// The file stays where it is. No other wallet can be created while it is there, see AssertWalletCanBeAdded.
			Logger.LogWarning($"The wallet file could not be loaded.\n" +
				$"Wallet file path: {walletFilePath}\n" +
				$"Exception: {ex}");
		}
	}

	/// <summary>
	/// Makes sure that a wallet can be created or recovered: there is no wallet yet and no wallet file is in the way.
	/// </summary>
	/// <remarks>A wallet file that cannot be loaded is never overwritten: it may be the only copy of a wallet of another software.</remarks>
	/// <exception cref="InvalidOperationException">If a wallet cannot be added.</exception>
	public void AssertWalletCanBeAdded()
	{
		if (HasWallet)
		{
			throw new InvalidOperationException("There is a wallet already. The application works with a single wallet.");
		}

		if (File.Exists(WalletDirectories.WalletFilePath))
		{
			throw new InvalidOperationException(
				$"A wallet file that could not be opened is in the way: '{WalletDirectories.WalletFilePath}'. Move it to another place to create or recover a wallet.");
		}
	}

	public async Task<Wallet> StartWalletAsync(Wallet wallet)
	{
		lock (_lock)
		{
			if (_disposedValue)
			{
				Logger.LogError("Object was already disposed.");
				throw new OperationCanceledException("Object was already disposed.");
			}

			if (_cancelAllTasks.IsCancellationRequested)
			{
				throw new OperationCanceledException($"Stopped loading {wallet}, because cancel was requested.");
			}

			if (_wallet != wallet)
			{
				throw new InvalidOperationException("The wallet was not added to the wallet manager.");
			}
		}

		using (await _startStopWalletLock.LockAsync(_cancelAllTasks.Token).ConfigureAwait(false))
		{
			try
			{
				Logger.LogInfo(FormatLog("Starting wallet...", wallet));
				await wallet.StartAsync(_cancelAllTasksToken).ConfigureAwait(false);
				Logger.LogInfo(FormatLog("Wallet started.", wallet));
				_cancelAllTasksToken.ThrowIfCancellationRequested();
				return wallet;
			}
			catch
			{
				await wallet.StopAsync(CancellationToken.None).ConfigureAwait(false);
				throw;
			}
		}
	}

	/// <summary>
	/// Makes a newly created or recovered wallet the wallet of the application and writes its wallet file.
	/// </summary>
	/// <exception cref="InvalidOperationException">If there is a wallet or a wallet file already.</exception>
	public Wallet AddWallet(KeyManager keyManager)
	{
		Wallet wallet;

		lock (_lock)
		{
			AssertWalletCanBeAdded();

			keyManager.SetFilePath(WalletDirectories.WalletFilePath);
			keyManager.ToFile();

			wallet = _createWallet(keyManager);
			_wallet = wallet;
		}

		WalletAdded?.Invoke(this, wallet);
		return wallet;
	}

	public async Task RemoveAndStopAsync(CancellationToken cancel)
	{
		Wallet? wallet;

		lock (_lock)
		{
			// Already disposed.
			if (_disposedValue)
			{
				return;
			}

			_disposedValue = true;
		}

		_cancelAllTasks.Cancel();

		using (await _startStopWalletLock.LockAsync(cancel).ConfigureAwait(false))
		{
			lock (_lock)
			{
				wallet = _wallet;
				_wallet = null;
			}

			if (wallet is not null)
			{
				cancel.ThrowIfCancellationRequested();

				try
				{
					if (wallet.Loaded)
					{
						await wallet.StopAsync(cancel).ConfigureAwait(false);
						Logger.LogInfo(FormatLog("is stopped.", wallet));
					}

					wallet.Dispose();
				}
				catch (Exception ex)
				{
					Logger.LogError(FormatLog(ex.ToString(), wallet));
				}
			}
		}

		_cancelAllTasks.Dispose();
	}

	/// <summary>The key manager of the wallet, as long as it belongs to the network of the application.</summary>
	private KeyManager? KeyManagerOnNetwork =>
		Wallet?.KeyManager is { } keyManager && keyManager.GetNetwork() == Network
			? keyManager
			: null;

	public void SetMaxBestHeight(uint bestHeight)
	{
		KeyManagerOnNetwork?.SetMaxBestHeight(bestHeight);
	}

	/// <summary>The height that the wallet was created at, if it is known.</summary>
	public ChainHeight? GetBirthHeight() =>
		KeyManagerOnNetwork?.GetBirthHeight() is { } birthHeight && birthHeight > 0
			? birthHeight
			: null;

	/// <summary>The height that the wallet is synchronized to.</summary>
	public ChainHeight? GetBestHeight() =>
		KeyManagerOnNetwork?.GetBestHeight();
}
