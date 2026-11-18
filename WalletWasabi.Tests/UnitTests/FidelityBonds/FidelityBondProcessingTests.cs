using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.FidelityBonds;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Storages;
using WalletWasabi.Tests.UnitTests.Mocks;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.FidelityBonds;

/// <summary>
/// The wallet finds its fidelity bonds in the transactions that it processes, and keeps them apart from its coins.
/// </summary>
public class FidelityBondProcessingTests
{
	private const string Password = "password";
	private static readonly Network Network = Network.RegTest;
	private static readonly BondLocktime Locktime = BondLocktime.FromMonth(2027, 1);

	[Fact]
	public async Task BondThatTheWalletFundsIsFoundAndIsNoCoinAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		// The wallet receives a coin and locks half of it in a bond. The rest comes back as change.
		var received = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(1));
		fixture.Processor.ProcessBlock(node.MineBlock([received]), new Height.ChainHeight(1));

		var funding = CreateSpending(
			fixture.KeyManager,
			received,
			0,
			(Money.Coins(0.5m), fixture.BondScriptPubKey(Locktime)),
			(Money.Coins(0.4999m), fixture.KeyManager.GetNextChangeKey().GetAssumedScriptPubKey()));
		fixture.Processor.Process(new SmartTransaction(funding, Height.Mempool));

		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.Equal(new OutPoint(funding, 0), bond.OutPoint);
		Assert.Equal(Money.Coins(0.5m), bond.Amount);
		Assert.Equal(Locktime, bond.Locktime);
		Assert.False(bond.IsConfirmed);
		Assert.False(bond.IsSpent);

		// The bond is no coin: only the change is left to spend.
		var coin = Assert.Single(fixture.Processor.Coins);
		Assert.Equal(Money.Coins(0.4999m), coin.Amount);
		Assert.Equal(funding.GetHash(), Assert.Single(fixture.BondTransactions));

		// The block that confirms the transaction confirms the bond.
		fixture.Processor.ProcessBlock(node.MineBlock([funding]), new Height.ChainHeight(2));

		bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.True(bond.IsConfirmed);
		Assert.Equal(new Height.ChainHeight(2), bond.Transaction.Height);
		Assert.False(bond.IsRedeemable(Locktime.Date));
		Assert.True(bond.IsRedeemable(Locktime.Date.AddSeconds(1)));
	}

	[Fact]
	public async Task BondThatSomeoneElseFundsIsFoundAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var funding = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		var foreign = CreatePayment(CreateForeignScript(), Money.Coins(1));

		Assert.True(fixture.Processor.ConcernsWallet(funding));
		Assert.False(fixture.Processor.ConcernsWallet(foreign));

		fixture.Processor.ProcessBlock(node.MineBlock([foreign, funding]), new Height.ChainHeight(1));

		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.Equal(Money.Coins(0.2m), bond.Amount);
		Assert.True(bond.IsConfirmed);

		// It is no news about the coins of the wallet, but the wallet keeps the transaction and tells about the bond.
		Assert.Empty(fixture.Processor.Coins);
		Assert.Empty(fixture.RelevantTransactions);
		Assert.Equal(funding.GetHash(), Assert.Single(fixture.BondTransactions));
		Assert.Equal(funding.GetHash(), Assert.Single(fixture.TransactionStore.GetTransactions()).GetHash());
	}

	[Fact]
	public async Task UnconfirmedPaymentFromElsewhereIsNoBondUntilItConfirmsAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		// Someone pays to the bond address and bumps the fee: two transactions that spend the same coin, one of which
		// will never confirm. Neither leaves a coin of the wallet behind that would tell which one that is.
		var funding = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		var replacement = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.199m));
		replacement.Inputs[0].PrevOut = funding.Inputs[0].PrevOut;

		Assert.True(fixture.Processor.ConcernsWallet(funding));
		fixture.Processor.Process(new SmartTransaction(funding, Height.Mempool));
		fixture.Processor.Process(new SmartTransaction(replacement, Height.Mempool));

		Assert.Empty(fixture.Processor.GetFidelityBonds());
		Assert.Empty(fixture.BondTransactions);
		Assert.Empty(fixture.TransactionStore.GetTransactions());

		// The one that confirms is the bond.
		fixture.Processor.ProcessBlock(node.MineBlock([replacement]), new Height.ChainHeight(1));

		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.Equal(replacement.GetHash(), bond.OutPoint.Hash);
		Assert.Equal(Money.Coins(0.199m), bond.Amount);
		Assert.True(bond.IsConfirmed);
		Assert.Equal(replacement.GetHash(), Assert.Single(fixture.BondTransactions));
	}

	[Fact]
	public async Task EveryPaymentToABondAddressIsABondOfItsOwnAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);
		var february = BondLocktime.FromMonth(2027, 2);

		var first = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		var second = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.3m));
		var third = CreatePayment(fixture.BondScriptPubKey(february), Money.Coins(0.1m));

		fixture.Processor.ProcessBlock(node.MineBlock([first, second, third]), new Height.ChainHeight(1));

		var bonds = fixture.Processor.GetFidelityBonds();
		Assert.Equal(3, bonds.Count);
		Assert.Equal([Money.Coins(0.2m), Money.Coins(0.3m)], bonds.Where(x => x.Locktime == Locktime).Select(x => x.Amount).Order());
		Assert.Equal(Money.Coins(0.1m), Assert.Single(bonds, x => x.Locktime == february).Amount);
	}

	[Fact]
	public async Task RedeemedBondIsSpentAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var funding = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		fixture.Processor.ProcessBlock(node.MineBlock([funding]), new Height.ChainHeight(1));
		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());

		// The wallet pays the bond back to itself.
		var destination = fixture.KeyManager.GetNextChangeKey().GetAssumedScriptPubKey();
		using var key = fixture.KeyManager.GetFidelityBondSecret(Password, Locktime);
		var redemption = BondRedemption.Create(bond, key, destination, new FeeRate(5m), Locktime.Date.AddDays(1), Network);

		fixture.Processor.Process(new SmartTransaction(redemption, Height.Mempool));

		bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.True(bond.IsSpent);
		Assert.Equal(redemption.GetHash(), bond.SpenderTransaction!.GetHash());
		Assert.False(bond.IsRedeemable(Locktime.Date.AddDays(1)));

		// What was in the bond is a coin of the wallet now.
		var coin = Assert.Single(fixture.Processor.Coins);
		Assert.Equal(redemption.Outputs[0].Value, coin.Amount);
		Assert.Equal(destination, coin.ScriptPubKey);
	}

	[Fact]
	public async Task SpendOfABondConcernsTheWalletWhereverItPaysToAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var funding = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		fixture.Processor.ProcessBlock(node.MineBlock([funding]), new Height.ChainHeight(1));
		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());

		// The same seed in another installation redeems the bond to an address that this wallet does not watch.
		using var key = fixture.KeyManager.GetFidelityBondSecret(Password, Locktime);
		var redemption = BondRedemption.Create(bond, key, CreateForeignScript(), new FeeRate(5m), Locktime.Date.AddDays(1), Network);

		Assert.True(fixture.Processor.ConcernsWallet(redemption));
		fixture.Processor.ProcessBlock(node.MineBlock([redemption]), new Height.ChainHeight(2));

		bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.True(bond.IsSpent);
		Assert.True(bond.SpenderTransaction!.Confirmed);
		Assert.Empty(fixture.Processor.Coins);
	}

	[Fact]
	public async Task BondOfAnotherWalletDoesNotConcernTheWalletAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var otherWallet = KeyManager.CreateNew(out _, Password, Network);

		var funding = CreatePayment(BondScript.GetScriptPubKey(otherWallet.FidelityBonds.GetScript(Locktime)), Money.Coins(0.2m));
		Assert.False(fixture.Processor.ConcernsWallet(funding));

		var otherBond = new FidelityBond(new OutPoint(funding, 0), Money.Coins(0.2m), Locktime, new SmartTransaction(funding, new Height.ChainHeight(1)), null);
		using var key = otherWallet.GetFidelityBondSecret(Password, Locktime);
		var redemption = BondRedemption.Create(otherBond, key, CreateForeignScript(), new FeeRate(5m), Locktime.Date.AddDays(1), Network);
		Assert.False(fixture.Processor.ConcernsWallet(redemption));

		Assert.Empty(fixture.Processor.GetFidelityBonds());
	}

	[Fact]
	public async Task ReplacedFundingLeavesNoBondBehindAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var received = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(1));
		fixture.Processor.ProcessBlock(node.MineBlock([received]), new Height.ChainHeight(1));

		var funding = CreateSpending(fixture.KeyManager, received, 0, (Money.Coins(0.9m), fixture.BondScriptPubKey(Locktime)));
		fixture.Processor.Process(new SmartTransaction(funding, Height.Mempool));
		Assert.Single(fixture.Processor.GetFidelityBonds());

		// The user cancels the transaction: another one spends the coin back to the wallet.
		var cancellation = CreateSpending(fixture.KeyManager, received, 0, (Money.Coins(0.99m), fixture.KeyManager.GetNextChangeKey().GetAssumedScriptPubKey()));
		fixture.Processor.Process(new SmartTransaction(cancellation, Height.Mempool));

		Assert.Empty(fixture.Processor.GetFidelityBonds());
		Assert.Equal(Money.Coins(0.99m), Assert.Single(fixture.Processor.Coins).Amount);
	}

	[Fact]
	public async Task BondsAreFoundAgainFromTheKeptTransactionsAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var external = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		fixture.Processor.ProcessBlock(node.MineBlock([external]), new Height.ChainHeight(1));

		// The wallet locks a coin of its own, and the transaction is not confirmed yet.
		var received = CreatePayment(fixture.KeyManager.GetNextReceiveKey().GetAssumedScriptPubKey(), Money.Coins(0.06m));
		fixture.Processor.ProcessBlock(node.MineBlock([received]), new Height.ChainHeight(2));
		var pending = CreateSpending(fixture.KeyManager, received, 0, (Money.Coins(0.05m), fixture.BondScriptPubKey(BondLocktime.FromMonth(2030, 6))));
		fixture.Processor.Process(new SmartTransaction(pending, Height.Mempool));

		// The application starts again: the wallet has nothing but its keys and the transactions that it kept.
		var restarted = new TransactionProcessor(fixture.TransactionStore, fixture.KeyManager, new EventBus());
		restarted.Process(fixture.TransactionStore.ConfirmedStore.GetTransactions());
		restarted.Process(fixture.TransactionStore.MempoolStore.GetTransactions());

		var bonds = restarted.GetFidelityBonds();
		Assert.Equal(2, bonds.Count);
		Assert.True(Assert.Single(bonds, x => x.Locktime == Locktime).IsConfirmed);
		Assert.False(Assert.Single(bonds, x => x.Locktime == BondLocktime.FromMonth(2030, 6)).IsConfirmed);
	}

	[Fact]
	public async Task ReorganizedBondIsUnconfirmedAgainAsync()
	{
		using var fixture = await Fixture.CreateAsync();
		var node = new FakeBitcoinNode(Network);

		var funding = CreatePayment(fixture.BondScriptPubKey(Locktime), Money.Coins(0.2m));
		var block = node.MineBlock([funding]);
		fixture.Processor.ProcessBlock(block, new Height.ChainHeight(1));
		Assert.True(Assert.Single(fixture.Processor.GetFidelityBonds()).IsConfirmed);

		// The block leaves the chain, as the synchronizer of the wallet handles it.
		fixture.Processor.UndoBlock(new Height.ChainHeight(1));
		fixture.TransactionStore.ReleaseToMempoolFromBlock(block.GetHash());

		var bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.False(bond.IsConfirmed);
		Assert.False(bond.IsRedeemable(Locktime.Date.AddDays(1)));

		// The other chain confirms the transaction too.
		fixture.Processor.ProcessBlock(node.MineBlock([funding]), new Height.ChainHeight(1));

		bond = Assert.Single(fixture.Processor.GetFidelityBonds());
		Assert.True(bond.IsConfirmed);
		Assert.True(bond.IsRedeemable(Locktime.Date.AddDays(1)));
	}

	private static Transaction CreatePayment(Script destination, Money amount)
	{
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0), Script.Empty, new WitScript(Op.GetPushOp(new byte[64])));
		tx.Outputs.Add(amount, destination);
		return tx;
	}

	/// <summary>A transaction of the wallet that spends one of its coins, with its signature: the wallet ignores an unconfirmed one without.</summary>
	private static Transaction CreateSpending(KeyManager keyManager, Transaction funding, uint outputIndex, params (Money Amount, Script Destination)[] outputs)
	{
		var coin = new Coin(funding, outputIndex);
		var tx = Network.CreateTransaction();
		tx.Inputs.Add(coin.Outpoint, Script.Empty, WitScript.Empty, Sequence.MAX_BIP125_RBF_SEQUENCE);

		foreach (var (amount, destination) in outputs)
		{
			tx.Outputs.Add(amount, destination);
		}

		var builder = Network.CreateTransactionBuilder();
		builder.AddKeys(keyManager.GetSecrets(Password, coin.ScriptPubKey).ToArray());
		builder.AddCoins(coin);
		builder.SignTransactionInPlace(tx);

		return tx;
	}

	/// <summary>A script that the wallet has nothing to do with.</summary>
	private static Script CreateForeignScript()
	{
		using var key = new Key();
		return key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
	}

	private sealed class Fixture : IDisposable
	{
		private readonly List<IDisposable> _subscriptions = [];

		private Fixture(AllTransactionStore transactionStore)
		{
			TransactionStore = transactionStore;
			KeyManager = KeyManager.CreateNew(out _, Password, Network);

			var eventBus = new EventBus();
			_subscriptions.Add(eventBus.Subscribe<WalletRelevantTransactionProcessed>(e => RelevantTransactions.Add(e.Result)));
			_subscriptions.Add(eventBus.Subscribe<FidelityBondTransactionProcessed>(e => BondTransactions.Add(e.Transaction.GetHash())));
			Processor = new TransactionProcessor(transactionStore, KeyManager, eventBus);
		}

		public KeyManager KeyManager { get; }
		public AllTransactionStore TransactionStore { get; }
		public TransactionProcessor Processor { get; }

		/// <summary>What the wallet was told about its coins.</summary>
		public List<ProcessedResult> RelevantTransactions { get; } = [];

		/// <summary>The transactions that the wallet was told to concern its bonds.</summary>
		public List<uint256> BondTransactions { get; } = [];

		public Script BondScriptPubKey(BondLocktime locktime) => BondScript.GetScriptPubKey(KeyManager.FidelityBonds.GetScript(locktime));

		public static async Task<Fixture> CreateAsync()
		{
			var transactionStore = new AllTransactionStore(SqliteStorageHelper.InMemoryDatabase, Network);
			await transactionStore.InitializeAsync();
			return new Fixture(transactionStore);
		}

		public void Dispose()
		{
			_subscriptions.ForEach(x => x.Dispose());
			TransactionStore.Dispose();
		}
	}
}
