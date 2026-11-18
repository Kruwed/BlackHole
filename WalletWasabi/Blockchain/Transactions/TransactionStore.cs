using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using WalletWasabi.Storages;

namespace WalletWasabi.Blockchain.Transactions;

public class TransactionStore : IDisposable
{
	public TransactionStore(string workFolderPath, Network network)
	{
		_dataSource = workFolderPath;
		bool useInMemoryDatabase = _dataSource == SqliteStorageHelper.InMemoryDatabase;

		if (!useInMemoryDatabase)
		{
			IoHelpers.EnsureDirectoryExists(_dataSource);
			_dataSource = Path.Combine(_dataSource, "Transactions.sqlite");
		}

		_sqliteStorage = TransactionSqliteStorage.FromFile(dataSource: _dataSource, network);
	}

	private readonly string _dataSource;
	private readonly Lock _sqliteStorageLock = new();

	/// <remarks>Guarded by <see cref="_sqliteStorageLock"/>.</remarks>
	private readonly TransactionSqliteStorage _sqliteStorage;

	/// <remarks>Guarded by <see cref="_sqliteStorageLock"/>.</remarks>
	private Dictionary<uint256, SmartTransaction> Transactions { get; } = new();

	private long _version;

	/// <summary>
	/// Counts the changes of the store: a transaction that was added, updated or removed. What was read from the store is
	/// still what the store holds as long as this number is the same.
	/// </summary>
	public long Version => Interlocked.Read(ref _version);

	public Task InitializeAsync(CancellationToken cancellationToken)
	{
		lock (_sqliteStorageLock)
		{
			InitializeTransactionsNoLock(cancellationToken);
		}

		return Task.CompletedTask;
	}

	private void InitializeTransactionsNoLock(CancellationToken cancellationToken)
	{
		try
		{
			lock (_sqliteStorageLock)
			{
				int i = 0;
				foreach (SmartTransaction tx in _sqliteStorage.GetAll(cancellationToken).OrderByBlockchain())
				{
					i++;

					if (i % 100 == 0)
					{
						cancellationToken.ThrowIfCancellationRequested();
					}

					TryAddOrUpdateNoLockNoSerialization(tx);
				}
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// We found a corrupted entry. Stop here.
			// Do not try to automatically correct the data, because the internal data structures are throwing events that may confuse the consumers of those events.
			Logger.LogError($"'{_dataSource}' database got corrupted. Clearing it...");
			_sqliteStorage.Clear();
			throw;
		}
	}

	private bool TryAddOrUpdateNoLockNoSerialization(SmartTransaction tx)
	{
		uint256 hash = tx.GetHash();

		// Counted here, so that the transactions that are read from the database at the start count as a change too.
		if (Transactions.TryAdd(hash, tx))
		{
			Interlocked.Increment(ref _version);
			return true;
		}
		else
		{
			if (Transactions[hash].TryUpdate(tx))
			{
				Interlocked.Increment(ref _version);
				return true;
			}
		}

		return false;
	}

	public bool TryAdd(SmartTransaction tx)
	{
		lock (_sqliteStorageLock)
		{
			if (Transactions.TryAdd(tx.GetHash(), tx))
			{
				Interlocked.Increment(ref _version);

				if (_sqliteStorage.BulkInsert(tx) == 0)
				{
					throw new UnreachableException($"Transaction '{tx.GetHash()}' was added in memory but not in database.");
				}

				return true;
			}

			return false;
		}
	}

	public bool TryAddOrUpdate(SmartTransaction tx)
	{
		lock (_sqliteStorageLock)
		{
			bool result = TryAddOrUpdateNoLockNoSerialization(tx);

			if (result)
			{
				if (_sqliteStorage.BulkInsert(new SmartTransaction[] { tx }, upsert: true) == 0)
				{
					throw new UnreachableException($"Transaction '{tx.GetHash()}' was update in memory but not in database.");
				}
			}

			return result;
		}
	}

	public bool TryUpdate(SmartTransaction tx)
	{
		bool updated = false;

		lock (_sqliteStorageLock)
		{
			if (Transactions.TryGetValue(tx.GetHash(), out var foundTx))
			{
				if (foundTx.TryUpdate(tx))
				{
					updated = true;
					Interlocked.Increment(ref _version);

					if (_sqliteStorage.BulkUpdate(tx) == 0)
					{
						throw new UnreachableException($"Transaction '{tx.GetHash()}' was update in memory but not in database.");
					}
				}
			}
		}

		return updated;
	}

	public bool TryRemove(uint256 hash, [NotNullWhen(true)] out SmartTransaction? tx)
	{
		bool isRemoved = false;

		lock (_sqliteStorageLock)
		{
			if (Transactions.Remove(hash, out tx))
			{
				isRemoved = true;
				Interlocked.Increment(ref _version);

				if (!_sqliteStorage.TryRemove(hash, out _))
				{
					throw new UnreachableException($"Transaction '{tx.GetHash()}' was removed from memory but not from database.");
				}
			}
		}

		return isRemoved;
	}

	public bool TryGetTransaction(uint256 hash, [NotNullWhen(true)] out SmartTransaction? tx)
	{
		lock (_sqliteStorageLock)
		{
			return Transactions.TryGetValue(hash, out tx);
		}
	}

	public List<SmartTransaction> GetTransactions()
	{
		lock (_sqliteStorageLock)
		{
			return Transactions.Values.OrderByBlockchain().ToList();
		}
	}

	public bool TryGetFirstSeenTransaction([NotNullWhen(true)] out SmartTransaction? stx)
	{
		lock (_sqliteStorageLock)
		{
			stx = Transactions.Values.OrderByBlockchain().FirstOrDefault();
			return stx != null;
		}
	}

	public List<uint256> GetTransactionHashes()
	{
		lock (_sqliteStorageLock)
		{
			return Transactions.Values.OrderByBlockchain().Select(x => x.GetHash()).ToList();
		}
	}

	public bool IsEmpty()
	{
		lock (_sqliteStorageLock)
		{
			return Transactions.Count == 0;
		}
	}

	public bool Contains(uint256 hash)
	{
		lock (_sqliteStorageLock)
		{
			return Transactions.ContainsKey(hash);
		}
	}

	public void Dispose()
	{
		_sqliteStorage.Dispose();
	}
}
