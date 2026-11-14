using Microsoft.Data.Sqlite;
using NBitcoin;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.Models;
using WalletWasabi.Storages;
using WalletWasabi.Tests.Helpers;
using Xunit;

namespace WalletWasabi.Tests.UnitTests.Transactions;

/// <summary>
/// Tests for <see cref="TransactionSqliteStorage"/>
/// </summary>
public class TransactionSqliteStorageTests
{
	[Theory]
	[InlineData("main")]
	[InlineData("testnet")]
	public void InsertDelete(string networkString)
	{
		Network? network = Network.GetNetwork(networkString);
		Assert.NotNull(network);

		using TransactionSqliteStorage storage = TransactionSqliteStorage.FromFile(dataSource: SqliteStorageHelper.InMemoryDatabase, network);

		SmartTransaction tx1 = new SmartTransaction(
			Transaction.Parse("01000000000102d5ae6e2612cdf8932d0e4f684d8ad9afdbca0afffba5c3dc0bf85f2b661bfb670000000000ffffffffbfd117908d5ba536624630519aaea7419605efa33bf1cb50c5ff7441f7b27a5b0100000000ffffffff01c6473d0000000000160014f9d25fe27812c3d35ad3819fcab8b95098effe15024730440220165730f8275434a5484b6aba3c71338a109b7cfd7380fdc18c6791a6afba9dee0220633b3b65313e57bdbd491d17232e6702912869d81461b4c939600d1cc99c06ec012102667c9fb272660cd6c06f853314b53c41da851f86024f9475ff15ea0636f564130247304402205e81562139231274cd7f705772c2810e34b7a291bd9882e6c567553babca9c7402206554ebd60d62a605a27bd4bf6482a533dd8dd35b12dc9d6abfa21738fdf7b57a012102b25dec5439a1aad8d901953a90d16252514a55f547fe2b85bc12e3f72cff1c4b00000000", network),
			Height.Mempool,
			blockHash: null,
			blockIndex: 0,
			isReplacement: false,
			isSpeedup: false,
			isCancellation: false,
			firstSeen: DateTimeOffset.FromUnixTimeSeconds(1570464578));

		SmartTransaction tx2 = new SmartTransaction(
			Transaction.Parse("01000000010113145dd6264da43406a0819b6e2d791ec9281322f33944ef034c3307f38c330000000000ffffffff0220a10700000000001600149af662cf9564700b93bd46fac8b51f64f2adad2343a5070000000000160014f1938902267eac1ae527128fe7773657d2b757b900000000", network),
			Height.Mempool,
			blockHash: null,
			blockIndex: 0,
			isReplacement: false,
			isSpeedup: false,
			isCancellation: false,
			firstSeen: DateTimeOffset.FromUnixTimeSeconds(1555590391));

		Assert.True(storage.IsEmpty());
		Assert.False(storage.Contains(tx1.GetHash()));

		// Insert 2 transactions.
		int added = storage.BulkInsert(new SmartTransaction[] { tx1, tx2 });
		Assert.Equal(2, added);

		// Attempt to insert tx1 again. Should fail because the same txid is already present.
		added = storage.BulkInsert(new SmartTransaction[] { tx1 });
		Assert.Equal(0, added);

		Assert.False(storage.Contains(uint256.Zero));
		Assert.False(storage.Contains(uint256.One));
		Assert.True(storage.Contains(tx1.GetHash()));
		Assert.True(storage.Contains(tx2.GetHash()));

		// Retrieve tx1 to test that the tx1 is serialized and deserialized correctly.
		Assert.True(storage.TryGet(tx1.GetHash(), out SmartTransaction? txActual));
		Assert.NotNull(txActual);
		Assert.NotSame(tx1, txActual); // A new instance was created in the process.

		// Make sure that tx1 was deserialized correctly.
		Assert.Equal(tx1.GetHash(), txActual.GetHash());
		Assert.Equal(tx1.Transaction.ToBytes(), txActual.Transaction.ToBytes());
		Assert.Equal(tx1.Height, txActual.Height);
		Assert.Equal(tx1.BlockHash, txActual.BlockHash);
		Assert.Equal(tx1.BlockIndex, txActual.BlockIndex);
		Assert.Equal(tx1.FirstSeen, txActual.FirstSeen);
		Assert.Equal(tx1.IsReplacement, txActual.IsReplacement);
		Assert.Equal(tx1.IsSpeedup, txActual.IsSpeedup);
		Assert.Equal(tx1.IsCancellation, txActual.IsCancellation);

		Assert.False(storage.IsEmpty());

		// Remove tx1 so that only tx2 still remains in the storage.
		Assert.Equal(1, storage.BulkRemove(new uint256[] { tx1.GetHash() }));
		Assert.False(storage.Contains(tx1.GetHash()));
		Assert.True(storage.Contains(tx2.GetHash()));

		// Attempt to delete tx1 again. Should fail because we've just done it.
		Assert.Equal(0, storage.BulkRemove(new uint256[] { tx1.GetHash() }));

		// Test table clearing.
		Assert.True(storage.Clear());
		Assert.True(storage.IsEmpty());
	}

	/// <summary>
	/// A database that was written when transactions had labels keeps working: its transactions are read and new ones can be stored.
	/// </summary>
	[Fact]
	public async Task DatabaseWithLabelsColumnIsUpgradedAsync()
	{
		var network = Network.Main;
		var workDir = await Common.GetEmptyWorkDirAsync();
		var dataSource = Path.Combine(workDir, "Transactions.sqlite");

		var oldTx = new SmartTransaction(
			Transaction.Parse("01000000010113145dd6264da43406a0819b6e2d791ec9281322f33944ef034c3307f38c330000000000ffffffff0220a10700000000001600149af662cf9564700b93bd46fac8b51f64f2adad2343a5070000000000160014f1938902267eac1ae527128fe7773657d2b757b900000000", network),
			Height.Mempool,
			firstSeen: DateTimeOffset.FromUnixTimeSeconds(1555590391));

		// Write a database with the former schema.
		using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = dataSource, Pooling = false }.ConnectionString))
		{
			connection.Open();

			using SqliteCommand createCommand = connection.CreateCommand();
			createCommand.CommandText = """
				CREATE TABLE "transaction" (
					txid BLOB NOT NULL PRIMARY KEY,
					block_height INTEGER NOT NULL,
					block_hash BLOB,
					block_index INTEGER NOT NULL,
					labels TEXT NOT NULL,
					first_seen INTEGER NOT NULL,
					is_replacement BOOLEAN NOT NULL,
					is_speedup BOOLEAN NOT NULL,
					is_cancellation BOOLEAN NOT NULL,
					tx BLOB NOT NULL
				);
				CREATE INDEX transaction_blockchain_idx ON "transaction" (block_height, block_index, first_seen);
				""";
			createCommand.ExecuteNonQuery();

			using SqliteCommand insertCommand = connection.CreateCommand();
			insertCommand.CommandText = """
				INSERT INTO "transaction" (txid, block_height, block_hash, block_index, labels, first_seen, is_replacement, is_speedup, is_cancellation, tx)
				VALUES ($txid, $block_height, NULL, 0, 'Alice, Bob', $first_seen, 0, 0, 0, $tx)
				""";
			insertCommand.Parameters.AddWithValue("$txid", oldTx.GetHash().ToBytes(lendian: false));
			insertCommand.Parameters.AddWithValue("$block_height", int.MaxValue - 1); // A mempool transaction.
			insertCommand.Parameters.AddWithValue("$first_seen", oldTx.FirstSeen.ToUnixTimeSeconds());
			insertCommand.Parameters.AddWithValue("$tx", oldTx.Transaction.ToBytes());
			insertCommand.ExecuteNonQuery();
		}

		var newTx = new SmartTransaction(
			Transaction.Parse("01000000000102d5ae6e2612cdf8932d0e4f684d8ad9afdbca0afffba5c3dc0bf85f2b661bfb670000000000ffffffffbfd117908d5ba536624630519aaea7419605efa33bf1cb50c5ff7441f7b27a5b0100000000ffffffff01c6473d0000000000160014f9d25fe27812c3d35ad3819fcab8b95098effe15024730440220165730f8275434a5484b6aba3c71338a109b7cfd7380fdc18c6791a6afba9dee0220633b3b65313e57bdbd491d17232e6702912869d81461b4c939600d1cc99c06ec012102667c9fb272660cd6c06f853314b53c41da851f86024f9475ff15ea0636f564130247304402205e81562139231274cd7f705772c2810e34b7a291bd9882e6c567553babca9c7402206554ebd60d62a605a27bd4bf6482a533dd8dd35b12dc9d6abfa21738fdf7b57a012102b25dec5439a1aad8d901953a90d16252514a55f547fe2b85bc12e3f72cff1c4b00000000", network),
			Height.Mempool,
			firstSeen: DateTimeOffset.FromUnixTimeSeconds(1570464578));

		using (TransactionSqliteStorage storage = TransactionSqliteStorage.FromFile(dataSource, network))
		{
			// The transaction of the old database is still there...
			Assert.True(storage.TryGet(oldTx.GetHash(), out SmartTransaction? storedOldTx));
			Assert.Equal(oldTx.Transaction.ToBytes(), storedOldTx.Transaction.ToBytes());
			Assert.Equal(oldTx.FirstSeen, storedOldTx.FirstSeen);

			// ... and new ones can be stored, updated and removed.
			Assert.Equal(1, storage.BulkInsert([newTx]));
			Assert.Equal(2, storage.BulkInsert([oldTx, newTx], upsert: true));
			Assert.Equal(2, storage.GetAll(CancellationToken.None).Count());

			Assert.True(storage.TryRemove(newTx.GetHash(), out SmartTransaction? removedTx));
			Assert.Equal(newTx.GetHash(), removedTx?.GetHash());
			Assert.Equal(1, storage.BulkInsert([newTx]));
		}

		// Neither the column nor the labels are left in the file.
		using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = dataSource, Pooling = false }.ConnectionString))
		{
			connection.Open();

			using SqliteCommand columnsCommand = connection.CreateCommand();
			columnsCommand.CommandText = """SELECT group_concat(name) FROM pragma_table_info('transaction')""";
			Assert.Equal("txid,block_height,block_hash,block_index,first_seen,is_replacement,is_speedup,is_cancellation,tx", columnsCommand.ExecuteScalar());
		}

		// Opening the upgraded database again changes nothing.
		using (TransactionSqliteStorage storage = TransactionSqliteStorage.FromFile(dataSource, network))
		{
			Assert.Equal(2, storage.GetAll(CancellationToken.None).Count());
		}
	}
}
