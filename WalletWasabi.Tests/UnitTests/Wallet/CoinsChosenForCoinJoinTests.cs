using System.Collections.Generic;
using System.Linq;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Fluent.ViewModels.Wallets;
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
/// A coin takes part in coinjoins only if the user chose it. The wallet keeps the choices in its file, and what it is
/// told about one coin leaves the choices of the others alone.
/// </summary>
public class CoinsChosenForCoinJoinTests
{
	[Fact]
	public void CoinThatArrivesIsNotChosen()
	{
		using var environment = new WalletWithCoins();

		var coin = environment.GiveTheWalletCoin(Money.Coins(1m));

		Assert.False(coin.IsSelectedForCoinJoin);
		Assert.Empty(environment.Wallet.KeyManager.CoinsSelectedForCoinJoin);
	}

	[Fact]
	public void ChoiceOfOneCoinLeavesTheOthersAsTheyAre()
	{
		using var environment = new WalletWithCoins();
		var (wallet, keyManager) = (environment.Wallet, environment.Wallet.KeyManager);

		var first = environment.GiveTheWalletCoin(Money.Coins(1m));
		var second = environment.GiveTheWalletCoin(Money.Coins(2m));
		var third = environment.GiveTheWalletCoin(Money.Coins(3m));

		wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [first.Outpoint] = true, [second.Outpoint] = true });
		wallet.SelectCoinForCoinJoin(second.Outpoint, select: false);
		wallet.SelectCoinForCoinJoin(third.Outpoint);

		Assert.True(first.IsSelectedForCoinJoin);
		Assert.False(second.IsSelectedForCoinJoin);
		Assert.True(third.IsSelectedForCoinJoin);
		Assert.Equal(
			new[] { first.Outpoint, third.Outpoint }.OrderBy(x => x.ToString()),
			keyManager.CoinsSelectedForCoinJoin.OrderBy(x => x.ToString()));
	}

	/// <summary>
	/// While the wallet still reads the blocks of the node, its file knows of choices whose coins the wallet does not
	/// have yet. A tick that the user changes in that time must not make the wallet forget them.
	/// </summary>
	[Fact]
	public void ChoiceMadeWhileTheWalletIsStillLoadingKeepsTheChoicesOfCoinsToCome()
	{
		using var environment = new WalletWithCoins();
		var (wallet, keyManager) = (environment.Wallet, environment.Wallet.KeyManager);

		// The file names a coin that the wallet has not come to.
		var toCome = new OutPoint(uint256.One, 5);
		keyManager.SetCoinsSelectedForCoinJoin([toCome]);

		var known = environment.GiveTheWalletCoin(Money.Coins(1m));
		Assert.False(wallet.Loaded);

		wallet.SelectCoinForCoinJoin(known.Outpoint);
		Assert.Equal(
			new[] { toCome, known.Outpoint }.OrderBy(x => x.ToString()),
			keyManager.CoinsSelectedForCoinJoin.OrderBy(x => x.ToString()));

		wallet.SelectCoinForCoinJoin(known.Outpoint, select: false);
		Assert.Equal([toCome], keyManager.CoinsSelectedForCoinJoin);
	}

	/// <summary>
	/// A chosen coin that was spent, in a round for example, is not kept in the file: the next change of a tick forgets it.
	/// </summary>
	[Fact]
	public void ChoiceOfACoinThatWasSpentIsForgotten()
	{
		using var environment = new WalletWithCoins();
		var (wallet, keyManager) = (environment.Wallet, environment.Wallet.KeyManager);

		var spent = environment.GiveTheWalletCoin(Money.Coins(1m));
		var kept = environment.GiveTheWalletCoin(Money.Coins(2m));
		var other = environment.GiveTheWalletCoin(Money.Coins(3m));
		wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [spent.Outpoint] = true, [kept.Outpoint] = true });

		environment.Spend(spent);
		Assert.True(spent.IsSpent());

		// Nobody told the wallet about the spent coin, so the file still names it.
		Assert.Contains(spent.Outpoint, keyManager.CoinsSelectedForCoinJoin);

		wallet.SelectCoinForCoinJoin(other.Outpoint);

		Assert.Equal(
			new[] { kept.Outpoint, other.Outpoint }.OrderBy(x => x.ToString()),
			keyManager.CoinsSelectedForCoinJoin.OrderBy(x => x.ToString()));
	}

	/// <summary>
	/// Play in the Coinjoin dialog names every coin of the list of the Coins tab with its tick. So the round gets the
	/// coins that are ticked then, and a coin that an earlier round was started with is taken out again unless it is
	/// ticked.
	/// </summary>
	[Fact]
	public void RoundStartedWithPlayGetsTheTickedCoinsAndNoOthers()
	{
		using var environment = new WalletWithCoins();
		var (wallet, keyManager) = (environment.Wallet, environment.Wallet.KeyManager);

		var earlier = environment.GiveTheWalletCoin(Money.Coins(1m));
		var ticked = environment.GiveTheWalletCoin(Money.Coins(2m));
		var untouched = environment.GiveTheWalletCoin(Money.Coins(3m));
		wallet.SelectCoinForCoinJoin(earlier.Outpoint);

		wallet.SelectCoinsForCoinJoin(CoinJoinBoxViewModel.GetRoundSelection(
		[
			(earlier.Outpoint, false),
			(ticked.Outpoint, true),
			(untouched.Outpoint, false)
		]));

		Assert.False(earlier.IsSelectedForCoinJoin);
		Assert.True(ticked.IsSelectedForCoinJoin);
		Assert.False(untouched.IsSelectedForCoinJoin);
		Assert.Equal([ticked.Outpoint], keyManager.CoinsSelectedForCoinJoin);

		// With nothing ticked, nothing is chosen: the round then tells that no coin is ticked.
		wallet.SelectCoinsForCoinJoin(CoinJoinBoxViewModel.GetRoundSelection(
		[
			(earlier.Outpoint, false),
			(ticked.Outpoint, false),
			(untouched.Outpoint, false)
		]));

		Assert.DoesNotContain(wallet.Coins, coin => coin.IsSelectedForCoinJoin);
		Assert.Empty(keyManager.CoinsSelectedForCoinJoin);
	}

	[Fact]
	public void CoinThatTheWalletDoesNotHaveCannotBeChosen()
	{
		using var environment = new WalletWithCoins();
		var wallet = environment.Wallet;
		var unknown = new OutPoint(uint256.One, 5);

		Assert.Throws<InvalidOperationException>(() => wallet.SelectCoinForCoinJoin(unknown));

		// Among several coins it is passed over: a coin can be spent between the tick and the telling.
		wallet.SelectCoinsForCoinJoin(new Dictionary<OutPoint, bool> { [unknown] = true });
		Assert.Empty(wallet.KeyManager.CoinsSelectedForCoinJoin);
	}

	private sealed class WalletWithCoins : IDisposable
	{
		private readonly AllTransactionStore _transactionStore;
		private readonly MailboxProcessor<CpfpInfoMessage> _cpfpInfoUpdater;

		public WalletWithCoins()
		{
			var network = Network.Main;
			var eventBus = new EventBus();

			_transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, network);
			_cpfpInfoUpdater = Workers.Spawn($"CpfpInfoProvider_{Guid.NewGuid():N}", Workers.EventDriven(Unit.Instance, CpfpInfoUpdater.Create(new MockRpcClient(), eventBus)));
			var walletFactory = WalletWasabi.Wallets.Wallet.CreateFactory(network, new MockRpcClient(), _transactionStore, new ServiceConfiguration(), eventBus, new CpfpInfoProvider(_cpfpInfoUpdater));

			Wallet = walletFactory(KeyManager.CreateNew(out _, "password", network));
		}

		/// <summary>The wallet is not started: it has the coins that it is given here and has not read any block.</summary>
		public WalletWasabi.Wallets.Wallet Wallet { get; }

		public SmartCoin GiveTheWalletCoin(Money amount)
		{
			var tx = Network.Main.CreateTransaction();
			tx.Version = 1;
			tx.LockTime = LockTime.Zero;
			tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), new Script(OpcodeType.OP_0, OpcodeType.OP_0), sequence: Sequence.Final);
			tx.Outputs.Add(amount, Wallet.KeyManager.GetNextReceiveKey().P2Taproot);

			Wallet.TransactionProcessor.Process(new SmartTransaction(tx, new Height.ChainHeight(100)));

			return Wallet.Coins.Single(coin => coin.TransactionId == tx.GetHash());
		}

		/// <summary>A confirmed transaction of somebody that spends the coin and pays to nobody the wallet knows.</summary>
		public void Spend(SmartCoin coin)
		{
			var tx = Network.Main.CreateTransaction();
			tx.Version = 1;
			tx.LockTime = LockTime.Zero;
			tx.Inputs.Add(coin.Outpoint, Script.Empty, WitScript.Empty);
			tx.Outputs.Add(coin.Amount - Money.Satoshis(1_000), BitcoinFactory.CreateScript());

			// Confirmed, so that the signature is not asked for.
			Wallet.TransactionProcessor.Process(new SmartTransaction(tx, new Height.ChainHeight(101)));
		}

		public void Dispose()
		{
			_cpfpInfoUpdater.Dispose();
			_transactionStore.Dispose();
		}
	}
}
