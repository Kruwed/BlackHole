using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.Mempool;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.Helpers;
using WalletWasabi.Tests.UnitTests.Mocks;
using WalletWasabi.Wallets;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Wallet;

/// <summary>
/// Tests for <see cref="WalletManager"/>: the application works with a single wallet.
/// </summary>
public class WalletManagerTests
{
	[Fact]
	public async Task AddsOnlyOneWalletAsync()
	{
		using var environment = await TestEnvironment.CreateAsync();
		var manager = environment.CreateWalletManager();
		var walletFilePath = manager.WalletDirectories.WalletFilePath;

		Assert.False(manager.HasWallet);
		Assert.Null(manager.Wallet);
		Assert.False(File.Exists(walletFilePath));
		manager.AssertWalletCanBeAdded();

		var addedWallets = new List<WalletWasabi.Wallets.Wallet>();
		manager.WalletAdded += (_, wallet) => addedWallets.Add(wallet);

		// Adding the wallet writes its file.
		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		var wallet = manager.AddWallet(keyManager);

		Assert.True(manager.HasWallet);
		Assert.Same(wallet, manager.Wallet);
		Assert.Same(keyManager, wallet.KeyManager);
		Assert.Equal([wallet], addedWallets);
		Assert.Equal(walletFilePath, keyManager.FilePath);
		Assert.Equal(keyManager.MasterFingerprint, KeyManager.FromFile(walletFilePath).MasterFingerprint);

		// There is no second wallet.
		var otherKeyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		Assert.Throws<InvalidOperationException>(manager.AssertWalletCanBeAdded);
		Assert.Throws<InvalidOperationException>(() => manager.AddWallet(otherKeyManager));

		Assert.Same(wallet, manager.Wallet);
		Assert.Single(addedWallets);
		Assert.Null(otherKeyManager.FilePath);
		Assert.Equal(keyManager.MasterFingerprint, KeyManager.FromFile(walletFilePath).MasterFingerprint);

		await manager.RemoveAndStopAsync(CancellationToken.None);
	}

	[Fact]
	public async Task LoadsTheWalletFromItsFileAsync()
	{
		using var environment = await TestEnvironment.CreateAsync();

		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		var firstManager = environment.CreateWalletManager();
		firstManager.AddWallet(keyManager);
		await firstManager.RemoveAndStopAsync(CancellationToken.None);

		// The next start finds the wallet.
		var manager = environment.CreateWalletManager();

		Assert.True(manager.HasWallet);
		Assert.Equal(keyManager.MasterFingerprint, manager.Wallet!.KeyManager.MasterFingerprint);
		Assert.Throws<InvalidOperationException>(manager.AssertWalletCanBeAdded);
		Assert.Throws<InvalidOperationException>(() => manager.AddWallet(KeyManager.CreateNew(out _, "password", Network.Main)));

		await manager.RemoveAndStopAsync(CancellationToken.None);
	}

	/// <summary>
	/// A wallet file that cannot be opened may be the only copy of a wallet of another software. It is never replaced.
	/// </summary>
	[Fact]
	public async Task DoesNotOverwriteAWalletFileItCannotOpenAsync()
	{
		using var environment = await TestEnvironment.CreateAsync();
		var walletFilePath = environment.WalletDirectories.WalletFilePath;

		const string ForeignWalletFile = """{ "EncryptedSecret": "not a wallet of this application" }""";
		await File.WriteAllTextAsync(walletFilePath, ForeignWalletFile);

		var manager = environment.CreateWalletManager();

		Assert.False(manager.HasWallet);

		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		var exception = Assert.Throws<InvalidOperationException>(manager.AssertWalletCanBeAdded);
		Assert.Contains(walletFilePath, exception.Message);
		Assert.Throws<InvalidOperationException>(() => manager.AddWallet(keyManager));

		Assert.False(manager.HasWallet);
		Assert.Null(keyManager.FilePath);
		Assert.Equal(ForeignWalletFile, await File.ReadAllTextAsync(walletFilePath));

		await manager.RemoveAndStopAsync(CancellationToken.None);
	}

	/// <summary>
	/// Only the wallet file counts. Other files in the wallets folder, e.g. wallets that were given a name once, are left alone.
	/// </summary>
	[Fact]
	public async Task IgnoresOtherFilesInTheWalletsFolderAsync()
	{
		using var environment = await TestEnvironment.CreateAsync();
		var otherWalletFilePath = Path.Combine(environment.WalletDirectories.WalletsDir, "Another Wallet.json");

		var otherKeyManager = KeyManager.CreateNew(out _, "password", Network.Main, otherWalletFilePath);
		var otherWalletFile = await File.ReadAllTextAsync(otherWalletFilePath);

		var manager = environment.CreateWalletManager();
		Assert.False(manager.HasWallet);

		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		manager.AddWallet(keyManager);

		Assert.NotEqual(otherKeyManager.MasterFingerprint, manager.Wallet!.KeyManager.MasterFingerprint);
		Assert.Equal(keyManager.MasterFingerprint, KeyManager.FromFile(manager.WalletDirectories.WalletFilePath).MasterFingerprint);
		Assert.Equal(otherWalletFile, await File.ReadAllTextAsync(otherWalletFilePath));

		await manager.RemoveAndStopAsync(CancellationToken.None);
	}

	[Fact]
	public async Task TellsTheHeightsOfTheWalletAsync()
	{
		using var environment = await TestEnvironment.CreateAsync();
		var manager = environment.CreateWalletManager();

		// Without a wallet there is nothing to synchronize.
		Assert.Null(manager.GetBirthHeight());
		Assert.Null(manager.GetBestHeight());

		var keyManager = KeyManager.CreateNew(out _, "password", Network.Main);
		keyManager.SetBestHeight(800_000);
		manager.AddWallet(keyManager);

		Assert.Equal(keyManager.GetBestHeight(), manager.GetBestHeight());

		manager.SetMaxBestHeight(700_000);
		Assert.Equal(new Height.ChainHeight(700_000), manager.GetBestHeight());

		// The height of the wallet is never raised by this.
		manager.SetMaxBestHeight(900_000);
		Assert.Equal(new Height.ChainHeight(700_000), manager.GetBestHeight());

		await manager.RemoveAndStopAsync(CancellationToken.None);
	}

	/// <summary>
	/// The services that wallets need, on top of an empty working directory.
	/// </summary>
	private sealed class TestEnvironment : IDisposable
	{
		private readonly FilterManager _filterManager;
		private readonly AllTransactionStore _transactionStore;
		private readonly MailboxProcessor<CpfpInfoMessage> _cpfpInfoUpdater;
		private readonly WalletFactory _walletFactory;

		private TestEnvironment(string workDir)
		{
			var network = Network.Main;
			var eventBus = new EventBus();
			var filterHeaderChain = new FilterHeaderChain();

			WalletDirectories = new WalletDirectories(network, workDir);
			_filterManager = new FilterManager(Path.Combine(workDir, "Filters"), network, filterHeaderChain, eventBus);
			_transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, network);
			_cpfpInfoUpdater = Workers.Spawn($"CpfpInfoProvider_{Guid.NewGuid():N}", Workers.EventDriven(Unit.Instance, CpfpInfoUpdater.Create(new MockRpcClient(), eventBus)));

			_walletFactory = WalletWasabi.Wallets.Wallet.CreateFactory(
				network,
				_filterManager,
				_transactionStore,
				filterHeaderChain,
				new MempoolService(eventBus),
				new ServiceConfiguration(),
				(_, _) => Task.FromResult<Block?>(null),
				eventBus,
				new CpfpInfoProvider(_cpfpInfoUpdater));
		}

		public WalletDirectories WalletDirectories { get; }

		public static async Task<TestEnvironment> CreateAsync([System.Runtime.CompilerServices.CallerMemberName] string callerMemberName = "")
		{
			var workDir = await Common.GetEmptyWorkDirAsync(callerMemberName: callerMemberName);
			return new TestEnvironment(workDir);
		}

		/// <summary>Creates a wallet manager as the application does at its start: it looks for the wallet file.</summary>
		public WalletManager CreateWalletManager() =>
			new(Network.Main, WalletDirectories, _walletFactory);

		public void Dispose()
		{
			_cpfpInfoUpdater.Dispose();
			_transactionStore.Dispose();
			_filterManager.Dispose();
		}
	}
}
