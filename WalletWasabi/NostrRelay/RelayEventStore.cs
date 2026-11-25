using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WalletWasabi.NostrRelay;

/// <summary>What became of an event that the relay was to keep.</summary>
public enum StoreOutcome
{
	/// <summary>The event is kept from now on.</summary>
	Stored,

	/// <summary>The relay has the event already.</summary>
	Duplicate,

	/// <summary>The relay has a newer event that takes the place of this one, so this one is not kept.</summary>
	Superseded,

	/// <summary>The room for events is used up, and nothing makes way: the event is not kept.</summary>
	Full
}

/// <summary>What a relay knows of a key that has shown a fidelity bond.</summary>
/// <param name="Bond">The name of the bond, see <see cref="RelayBondClaim.GetBondId"/>.</param>
/// <param name="Amount">What the bond holds, in sats.</param>
/// <param name="UnlockHeight">The height that the bond is locked until.</param>
/// <param name="Share">What stands behind the key, in sats: what the bond holds, by the number of keys that have shown it.</param>
public sealed record SenderBond(string Bond, long Amount, uint UnlockHeight, long Share);

/// <summary>An event as the store hands it out.</summary>
/// <param name="CreatedAt">When the event was made, as its maker tells it: seconds since 1970.</param>
/// <param name="Json">The event as the relay hands it on.</param>
public sealed record StoredEvent(long CreatedAt, string Id, string Json);

/// <summary>
/// Keeps the events of a relay and finds the ones that a filter asks for. The events are in a database, which is a
/// file, or which is in memory only and gone when the relay stops.
/// </summary>
/// <remarks>
/// <para>
/// The store follows the rules of the protocol for what takes the place of what. Of a replaceable kind it keeps the
/// newest event of a key, and of an addressable kind the newest event of a key and of a name. Of two such events of
/// the same second the one with the lower id stays.
/// </para>
/// <para>
/// The room for events has a limit, in events and in bytes. When it is used up, an event makes way for a new one: the
/// one with the least behind it, see <see cref="RelaySenderRules"/>, and of several such the one that the store has
/// had the longest. A new event that has less behind it than everything in a full store is not kept.
/// </para>
/// <para>
/// The store also keeps the book of the fidelity bonds: which key has shown which bond. What stands behind a key is
/// its share of its bond, and the events of the key carry that number, so that the store knows which make way first.
/// </para>
/// </remarks>
public sealed class RelayEventStore : IDisposable
{
	/// <summary>A tag is found by its value up to this many characters. A tag with a longer value is kept with its event, and cannot be asked for.</summary>
	public const int MaxIndexedTagValueLength = 1_024;

	private const string InMemory = ":memory:";

	/// <summary>The version of the tables. A file of another version is emptied: what it has is a day old at most.</summary>
	private const int SchemaVersion = 2;

	private readonly SqliteConnection _connection;
	private readonly RelayLimits _limits;
	private readonly KindList? _servedKinds;
	private readonly Lock _lock = new();

	private long _count;
	private long _bytes;
	private bool _isDisposed;

	private RelayEventStore(SqliteConnection connection, RelayLimits limits, KindList? servedKinds)
	{
		_connection = connection;
		_limits = limits;
		_servedKinds = servedKinds;
	}

	/// <summary>How many events the store keeps, the ones that have expired and are not removed yet included.</summary>
	public long Count
	{
		get
		{
			lock (_lock)
			{
				return _count;
			}
		}
	}

	/// <summary>How many bytes the events have that the store keeps.</summary>
	public long Bytes
	{
		get
		{
			lock (_lock)
			{
				return _bytes;
			}
		}
	}

	/// <summary>Opens a store.</summary>
	/// <param name="filePath">The file that keeps the events. Null for a store in memory, which is gone when it is closed.</param>
	/// <param name="servedKinds">The kinds of events that are handed out. What the file keeps of other kinds stays there, and is not handed out. Null hands out everything.</param>
	/// <exception cref="SqliteException">The file is there and cannot be used.</exception>
	public static RelayEventStore Open(string? filePath, RelayLimits limits, KindList? servedKinds = null)
	{
		if (filePath is not null && Path.GetDirectoryName(filePath) is { Length: > 0 } folder)
		{
			Directory.CreateDirectory(folder);
		}

		var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = filePath ?? InMemory }.ConnectionString);

		try
		{
			connection.Open();

			Execute(connection, "PRAGMA foreign_keys = ON");

			if (filePath is not null)
			{
				Execute(connection, "PRAGMA journal_mode = 'wal'; PRAGMA synchronous = 'NORMAL'");
			}

			// A file with the tables of before has no place for what stands behind an event. What it has is dropped, which
			// loses a day of events at most.
			if (GetSchemaVersion(connection) != SchemaVersion)
			{
				Execute(connection, "DROP TABLE IF EXISTS tag; DROP TABLE IF EXISTS event; DROP TABLE IF EXISTS sender");
			}

			Execute(
				connection,
				"""
				CREATE TABLE IF NOT EXISTS event (
					id TEXT NOT NULL PRIMARY KEY, /* 64 characters of lowercase hex */
					pubkey TEXT NOT NULL,
					created_at INTEGER NOT NULL, /* seconds since 1970, as the maker of the event tells it */
					kind INTEGER NOT NULL,
					address TEXT, /* the name of an event of an addressable kind; NULL for the other kinds */
					stored_at INTEGER NOT NULL, /* seconds since 1970: when the relay took the event */
					expires_at INTEGER, /* seconds since 1970; NULL ~ the event stays until the room is used up */
					rank INTEGER NOT NULL, /* what stands behind the key of the event, in sats: the events with the least make way first */
					size INTEGER NOT NULL, /* the bytes of json */
					json TEXT NOT NULL
				) WITHOUT ROWID;
				CREATE INDEX IF NOT EXISTS event_time_idx ON event (created_at DESC, id);
				CREATE INDEX IF NOT EXISTS event_kind_idx ON event (kind, created_at DESC);
				CREATE INDEX IF NOT EXISTS event_author_idx ON event (pubkey, kind, created_at DESC);
				CREATE INDEX IF NOT EXISTS event_expiry_idx ON event (expires_at) WHERE expires_at IS NOT NULL;
				CREATE INDEX IF NOT EXISTS event_rank_idx ON event (rank, stored_at, id);
				CREATE TABLE IF NOT EXISTS tag (
					name TEXT NOT NULL, /* one letter */
					value TEXT NOT NULL,
					event_id TEXT NOT NULL REFERENCES event (id) ON DELETE CASCADE,
					PRIMARY KEY (name, value, event_id)
				) WITHOUT ROWID;
				CREATE INDEX IF NOT EXISTS tag_event_idx ON tag (event_id);
				CREATE TABLE IF NOT EXISTS sender (
					pubkey TEXT NOT NULL PRIMARY KEY, /* a key that has shown a fidelity bond */
					bond TEXT NOT NULL, /* the bond: its transaction and the number of its output */
					amount INTEGER NOT NULL, /* what the bond holds, in sats */
					unlock_height INTEGER NOT NULL, /* the height that the bond is locked until */
					last_seen INTEGER NOT NULL /* seconds since 1970: when the relay last took an event of the key */
				) WITHOUT ROWID;
				CREATE INDEX IF NOT EXISTS sender_bond_idx ON sender (bond);
				""");
			Execute(connection, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {SchemaVersion}"));

			var store = new RelayEventStore(connection, limits, servedKinds);
			store.CountEvents();

			return store;
		}
		catch
		{
			connection.Dispose();
			throw;
		}
	}

	/// <summary>Keeps an event, as far as the rules of the protocol and the room for events have it.</summary>
	/// <param name="expiresAt">When the event is forgotten, in seconds since 1970. Null for an event that stays.</param>
	/// <param name="now">The time, in seconds since 1970. What has expired by then makes way before an event is refused for want of room.</param>
	/// <param name="rank">What stands behind the key of the event, in sats, see <see cref="RelaySenderRules"/>. Nothing for a key without a fidelity bond.</param>
	public StoreOutcome Add(RelayEvent relayEvent, long? expiresAt, long now, long rank = 0)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			using var transaction = _connection.BeginTransaction();

			try
			{
				if (Exists(relayEvent.Id, transaction))
				{
					return StoreOutcome.Duplicate;
				}

				string? address = relayEvent.IsAddressable ? relayEvent.Address : null;

				if ((relayEvent.IsReplaceable || relayEvent.IsAddressable) && !MakeWayForNewer(relayEvent, address, transaction))
				{
					return StoreOutcome.Superseded;
				}

				if (!MakeRoom(relayEvent, now, rank, transaction))
				{
					// What was removed to make way stays removed only if the event is kept.
					transaction.Rollback();
					CountEvents();
					return StoreOutcome.Full;
				}

				Insert(relayEvent, address, expiresAt, now, rank, transaction);
				transaction.Commit();

				_count++;
				_bytes += relayEvent.Size;

				return StoreOutcome.Stored;
			}
			catch
			{
				// Nothing of what was begun is kept, so the count is taken anew.
				if (transaction.Connection is not null)
				{
					transaction.Rollback();
				}

				CountEvents();
				throw;
			}
		}
	}

	/// <summary>
	/// Finds stored events that a filter asks for, the newest first. Of events of the same second the one with the
	/// lower id comes first. The limit of the filter is for the caller to keep: this hands out one page.
	/// </summary>
	/// <param name="now">The time, in seconds since 1970. Events that have expired by then are not handed out.</param>
	/// <param name="pageSize">How many events are handed out at most.</param>
	/// <param name="after">The last event of the page before. Null for the first page.</param>
	public IReadOnlyList<StoredEvent> Query(RelayFilter filter, long now, int pageSize, StoredEvent? after = null)
	{
		if (filter.IsEmpty || pageSize <= 0)
		{
			return [];
		}

		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			using var command = _connection.CreateCommand();
			var sql = new StringBuilder("SELECT created_at, id, json FROM event WHERE (expires_at IS NULL OR expires_at > $now)");

			command.Parameters.AddWithValue("$now", now);

			AppendIn(sql, command, "id", "i", filter.Ids);
			AppendIn(sql, command, "pubkey", "p", filter.Authors);
			AppendIn(sql, command, "kind", "k", filter.Kinds?.Select(kind => (object)kind));

			if (filter.Since is { } since)
			{
				sql.Append(" AND created_at >= $since");
				command.Parameters.AddWithValue("$since", since);
			}

			if (filter.Until is { } until)
			{
				sql.Append(" AND created_at <= $until");
				command.Parameters.AddWithValue("$until", until);
			}

			var tagNumber = 0;

			foreach (var (name, values) in filter.Tags)
			{
				var prefix = string.Create(CultureInfo.InvariantCulture, $"t{tagNumber++}_");

				sql.Append(CultureInfo.InvariantCulture, $" AND EXISTS (SELECT 1 FROM tag WHERE tag.event_id = event.id AND tag.name = ${prefix}n");
				command.Parameters.AddWithValue($"${prefix}n", name);
				AppendIn(sql, command, "tag.value", prefix, values);
				sql.Append(')');
			}

			if (_servedKinds is not null)
			{
				AppendServedKinds(sql, _servedKinds);
			}

			if (after is not null)
			{
				sql.Append(" AND (created_at < $after_time OR (created_at = $after_time AND id > $after_id))");
				command.Parameters.AddWithValue("$after_time", after.CreatedAt);
				command.Parameters.AddWithValue("$after_id", after.Id);
			}

			sql.Append(" ORDER BY created_at DESC, id ASC LIMIT $limit");
			command.Parameters.AddWithValue("$limit", pageSize);
			command.CommandText = sql.ToString();

			var events = new List<StoredEvent>();

			using var reader = command.ExecuteReader();

			while (reader.Read())
			{
				events.Add(new StoredEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
			}

			return events;
		}
	}

	/// <summary>Forgets the events that have expired by the given time.</summary>
	/// <param name="now">The time, in seconds since 1970.</param>
	/// <returns>How many events were forgotten.</returns>
	public int DeleteExpired(long now)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			using var command = _connection.CreateCommand();
			command.CommandText = "DELETE FROM event WHERE expires_at IS NOT NULL AND expires_at <= $now RETURNING size";
			command.Parameters.AddWithValue("$now", now);

			return CountDeleted(command);
		}
	}

	/// <summary>Tells what the store knows of a key that has shown a fidelity bond. Null for a key that has shown none.</summary>
	public SenderBond? GetSender(string publicKey)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			return ReadSender(publicKey, transaction: null);
		}
	}

	/// <summary>
	/// Takes note that a key has shown a fidelity bond which the node has found. The key shares the bond with the keys
	/// that have shown the same one, in equal parts, and the events of all of them stand for their share from now on.
	/// A key that had shown another bond before leaves that one.
	/// </summary>
	/// <remarks>
	/// Where every key needs a share of a certain size, a bond stands behind as many keys as it holds such shares, and
	/// those are the keys that showed it last: for a further key, the one that the relay heard of the longest ago is
	/// struck, and its events go with it. Only whoever owns the bond can show it for a key, so it is the owner who
	/// chooses, by using a key, which keys the bond stands behind.
	/// </remarks>
	/// <param name="bond">The name of the bond, see <see cref="RelayBondClaim.GetBondId"/>.</param>
	/// <param name="amount">What the bond holds, in sats.</param>
	/// <param name="requiredShare">What the share of every key of the bond has to be at least. Nothing where a key needs no bond.</param>
	/// <param name="now">The time, in seconds since 1970.</param>
	/// <returns>The share of the key, in sats, or null if the bond holds less than one key needs.</returns>
	public long? Join(string publicKey, string bond, long amount, uint unlockHeight, long requiredShare, long now)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			return Change<long?>(transaction =>
			{
				var before = ReadSender(publicKey, transaction);

				if (before?.Bond == bond)
				{
					Touch(publicKey, now, transaction);
					return before.Share;
				}

				if (requiredShare > 0)
				{
					var room = amount / requiredShare;

					if (room == 0)
					{
						return null;
					}

					while (CountKeys(bond, transaction) >= room)
					{
						StrikeKeyHeardOfLongestAgo(bond, transaction);
					}
				}

				using (var command = _connection.CreateCommand())
				{
					command.Transaction = transaction;
					command.CommandText = "INSERT OR REPLACE INTO sender (pubkey, bond, amount, unlock_height, last_seen) VALUES ($pubkey, $bond, $amount, $unlock_height, $now)";
					command.Parameters.AddWithValue("$pubkey", publicKey);
					command.Parameters.AddWithValue("$bond", bond);
					command.Parameters.AddWithValue("$amount", amount);
					command.Parameters.AddWithValue("$unlock_height", unlockHeight);
					command.Parameters.AddWithValue("$now", now);
					command.ExecuteNonQuery();
				}

				RankEventsOfBond(bond, transaction);

				if (before is not null)
				{
					// The keys that stay with the bond of before have more of it each.
					RankEventsOfBond(before.Bond, transaction);
				}

				return amount / CountKeys(bond, transaction);
			});
		}
	}

	/// <summary>Takes note that the relay took an event of a key that has shown a bond: the bond stands behind the key for as long as the relay keeps events of it.</summary>
	public void Touch(string publicKey, long now)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			Touch(publicKey, now, transaction: null);
		}
	}

	/// <summary>
	/// Strikes the keys from the book whose bond no longer stands behind them: the relay took no event of the key for
	/// as long as it keeps events, or the bond has unlocked and can be spent. The keys that shared a bond with such a
	/// key have more of it each from now on.
	/// </summary>
	/// <param name="now">The time, in seconds since 1970.</param>
	/// <param name="retention">For how long the relay keeps an event, in seconds.</param>
	/// <param name="tipHeight">The height of the chain, if it is known.</param>
	/// <param name="dropEvents">Whether the events of these keys are dropped: they are where a key needs a bond. Otherwise they stay, with nothing behind them.</param>
	/// <returns>How many keys were struck.</returns>
	public int ForgetSenders(long now, long retention, uint? tipHeight, bool dropEvents)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			return Change(transaction =>
			{
				var gone = new List<(string PublicKey, string Bond)>();

				using (var command = _connection.CreateCommand())
				{
					command.Transaction = transaction;
					command.CommandText = "DELETE FROM sender WHERE last_seen + $retention <= $now OR unlock_height <= $tip RETURNING pubkey, bond";
					command.Parameters.AddWithValue("$retention", retention);
					command.Parameters.AddWithValue("$now", now);
					command.Parameters.AddWithValue("$tip", tipHeight is { } height ? height : -1L);

					using var reader = command.ExecuteReader();

					while (reader.Read())
					{
						gone.Add((reader.GetString(0), reader.GetString(1)));
					}
				}

				foreach (var (publicKey, _) in gone)
				{
					if (dropEvents)
					{
						DropEventsOfKey(publicKey, transaction);
					}
					else
					{
						using var command = _connection.CreateCommand();
						command.Transaction = transaction;
						command.CommandText = "UPDATE event SET rank = 0 WHERE pubkey = $pubkey AND rank != $whitelisted";
						command.Parameters.AddWithValue("$pubkey", publicKey);
						command.Parameters.AddWithValue("$whitelisted", RelaySenderRules.WhitelistRank);
						command.ExecuteNonQuery();
					}
				}

				foreach (var bond in gone.Select(sender => sender.Bond).Distinct(StringComparer.Ordinal))
				{
					RankEventsOfBond(bond, transaction);
				}

				return gone.Count;
			});
		}
	}

	/// <summary>
	/// Brings what the store has in line with the rules that the relay starts with. The store of a file may be from
	/// a run with other rules: the keys that are on the list now need no bond, a bond stands behind as many keys as
	/// it holds shares of the size that a key needs now, and where a key needs a bond the events of the keys without
	/// one are dropped.
	/// </summary>
	public void Reconcile(RelaySenderRules rules)
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_isDisposed, this);

			Change(transaction =>
			{
				foreach (var publicKey in rules.Whitelist)
				{
					using var command = _connection.CreateCommand();
					command.Transaction = transaction;
					command.CommandText = "DELETE FROM sender WHERE pubkey = $pubkey";
					command.Parameters.AddWithValue("$pubkey", publicKey);
					command.ExecuteNonQuery();
				}

				// Of more keys than a bond holds shares for, the ones that the relay heard of last stay.
				if (rules.RequiredShare > 0)
				{
					foreach (var (bond, amount) in ReadBonds(transaction))
					{
						var room = amount / rules.RequiredShare;

						while (CountKeys(bond, transaction) > room)
						{
							StrikeKeyHeardOfLongestAgo(bond, transaction);
						}
					}
				}

				using (var command = _connection.CreateCommand())
				{
					command.Transaction = transaction;
					command.CommandText = $"UPDATE event SET rank = COALESCE((SELECT {ShareSql("s")} FROM sender s WHERE s.pubkey = event.pubkey), 0)";
					command.ExecuteNonQuery();
				}

				foreach (var publicKey in rules.Whitelist)
				{
					using var command = _connection.CreateCommand();
					command.Transaction = transaction;
					command.CommandText = "UPDATE event SET rank = $whitelisted WHERE pubkey = $pubkey";
					command.Parameters.AddWithValue("$whitelisted", RelaySenderRules.WhitelistRank);
					command.Parameters.AddWithValue("$pubkey", publicKey);
					command.ExecuteNonQuery();
				}

				if (rules.IsBondRequired)
				{
					using var command = _connection.CreateCommand();
					command.Transaction = transaction;
					command.CommandText = "DELETE FROM event WHERE rank < $required RETURNING size";
					command.Parameters.AddWithValue("$required", rules.RequiredShare);
					CountDeleted(command);
				}

				return 0;
			});
		}
	}

	public void Dispose()
	{
		lock (_lock)
		{
			if (_isDisposed)
			{
				return;
			}

			_isDisposed = true;
			_connection.Dispose();
		}
	}

	/// <summary>
	/// Makes changes to the tables that belong together: all of them are kept, or none of them. If none are, the
	/// events are counted anew, as some may have been taken off the count on the way.
	/// </summary>
	private T Change<T>(Func<SqliteTransaction, T> change)
	{
		using var transaction = _connection.BeginTransaction();

		try
		{
			var result = change(transaction);
			transaction.Commit();

			return result;
		}
		catch
		{
			if (transaction.Connection is not null)
			{
				transaction.Rollback();
			}

			CountEvents();
			throw;
		}
	}

	/// <summary>What a key of the given table row gets of its bond, as SQL: what the bond holds, by the number of keys that have shown it.</summary>
	private static string ShareSql(string table) =>
		$"({table}.amount / (SELECT COUNT(*) FROM sender shared WHERE shared.bond = {table}.bond))";

	private SenderBond? ReadSender(string publicKey, SqliteTransaction? transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"SELECT bond, amount, unlock_height, {ShareSql("sender")} FROM sender WHERE pubkey = $pubkey";
		command.Parameters.AddWithValue("$pubkey", publicKey);

		using var reader = command.ExecuteReader();

		return reader.Read()
			? new SenderBond(reader.GetString(0), reader.GetInt64(1), (uint)reader.GetInt64(2), reader.GetInt64(3))
			: null;
	}

	private long CountKeys(string bond, SqliteTransaction transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT COUNT(*) FROM sender WHERE bond = $bond";
		command.Parameters.AddWithValue("$bond", bond);

		return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
	}

	/// <summary>Strikes the key of a bond that the relay heard of the longest ago, and drops the events of that key: nothing stands behind them any more.</summary>
	private void StrikeKeyHeardOfLongestAgo(string bond, SqliteTransaction transaction)
	{
		string publicKey;

		using (var command = _connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText =
				"DELETE FROM sender WHERE pubkey = (SELECT pubkey FROM sender WHERE bond = $bond ORDER BY last_seen ASC, pubkey DESC LIMIT 1) RETURNING pubkey";
			command.Parameters.AddWithValue("$bond", bond);

			publicKey = (string)command.ExecuteScalar()!;
		}

		DropEventsOfKey(publicKey, transaction);
	}

	/// <summary>Drops the events of a key that nothing stands behind any more. What the key sent while it was on the list of the user stays.</summary>
	private void DropEventsOfKey(string publicKey, SqliteTransaction transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM event WHERE pubkey = $pubkey AND rank != $whitelisted RETURNING size";
		command.Parameters.AddWithValue("$pubkey", publicKey);
		command.Parameters.AddWithValue("$whitelisted", RelaySenderRules.WhitelistRank);
		CountDeleted(command);
	}

	/// <summary>Lists the bonds of the book, with what each holds.</summary>
	private List<(string Bond, long Amount)> ReadBonds(SqliteTransaction transaction)
	{
		var bonds = new List<(string Bond, long Amount)>();

		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT bond, MAX(amount) FROM sender GROUP BY bond";

		using var reader = command.ExecuteReader();

		while (reader.Read())
		{
			bonds.Add((reader.GetString(0), reader.GetInt64(1)));
		}

		return bonds;
	}

	private void Touch(string publicKey, long now, SqliteTransaction? transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "UPDATE sender SET last_seen = $now WHERE pubkey = $pubkey";
		command.Parameters.AddWithValue("$now", now);
		command.Parameters.AddWithValue("$pubkey", publicKey);
		command.ExecuteNonQuery();
	}

	/// <summary>Writes into the events of the keys of a bond what stands behind them: the share that each key has of the bond now.</summary>
	private void RankEventsOfBond(string bond, SqliteTransaction transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText =
			$"UPDATE event SET rank = (SELECT {ShareSql("s")} FROM sender s WHERE s.pubkey = event.pubkey) "
			+ "WHERE pubkey IN (SELECT pubkey FROM sender WHERE bond = $bond) AND rank != $whitelisted";
		command.Parameters.AddWithValue("$bond", bond);
		command.Parameters.AddWithValue("$whitelisted", RelaySenderRules.WhitelistRank);
		command.ExecuteNonQuery();
	}

	private static long GetSchemaVersion(SqliteConnection connection)
	{
		using var command = connection.CreateCommand();
		command.CommandText = "PRAGMA user_version";

		var version = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

		if (version != 0)
		{
			return version;
		}

		// A file that was never used has no tables and no version yet, and nothing in it that could be of another version.
		command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'";

		return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 ? SchemaVersion : 0;
	}

	private static void Execute(SqliteConnection connection, string sql)
	{
		using var command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	/// <summary>Adds the condition that a column has one of some values. Nothing is added for values that are not asked for.</summary>
	private static void AppendIn(StringBuilder sql, SqliteCommand command, string column, string prefix, IEnumerable<object>? values)
	{
		if (values is null)
		{
			return;
		}

		sql.Append(CultureInfo.InvariantCulture, $" AND {column} IN (");

		var number = 0;

		foreach (var value in values)
		{
			var parameter = string.Create(CultureInfo.InvariantCulture, $"${prefix}{number}");

			sql.Append(number++ == 0 ? "" : ",").Append(parameter);
			command.Parameters.AddWithValue(parameter, value);
		}

		sql.Append(')');
	}

	/// <summary>Adds the condition that an event is of a kind that is handed out. The kinds are numbers, so they are written into the question.</summary>
	private static void AppendServedKinds(StringBuilder sql, KindList kinds)
	{
		sql.Append(" AND (0");

		foreach (var (first, last) in kinds.GetSpans())
		{
			sql.Append(CultureInfo.InvariantCulture, $" OR kind BETWEEN {first} AND {last}");
		}

		sql.Append(')');
	}

	private void CountEvents()
	{
		using var command = _connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*), COALESCE(SUM(size), 0) FROM event";

		using var reader = command.ExecuteReader();
		reader.Read();

		_count = reader.GetInt64(0);
		_bytes = reader.GetInt64(1);
	}

	private bool Exists(string id, SqliteTransaction transaction)
	{
		using var command = _connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT 1 FROM event WHERE id = $id";
		command.Parameters.AddWithValue("$id", id);

		return command.ExecuteScalar() is not null;
	}

	/// <summary>
	/// Removes the events whose place the given event takes: the ones of its key and kind, and of its name if the kind
	/// is addressable, that are older, or of the same second with a higher id.
	/// </summary>
	/// <returns>False if the store has one that is newer, or of the same second with a lower id: then nothing is removed, and the given event is not to be kept.</returns>
	private bool MakeWayForNewer(RelayEvent relayEvent, string? address, SqliteTransaction transaction)
	{
		const string SameSlot = "pubkey = $pubkey AND kind = $kind AND address IS $address";

		using (var newerCommand = _connection.CreateCommand())
		{
			newerCommand.Transaction = transaction;
			newerCommand.CommandText = $"SELECT 1 FROM event WHERE {SameSlot} AND (created_at > $created_at OR (created_at = $created_at AND id < $id)) LIMIT 1";
			AddSlot(newerCommand);

			if (newerCommand.ExecuteScalar() is not null)
			{
				return false;
			}
		}

		using var deleteCommand = _connection.CreateCommand();
		deleteCommand.Transaction = transaction;
		deleteCommand.CommandText = $"DELETE FROM event WHERE {SameSlot} RETURNING size";
		AddSlot(deleteCommand);
		CountDeleted(deleteCommand);

		return true;

		void AddSlot(SqliteCommand command)
		{
			command.Parameters.AddWithValue("$pubkey", relayEvent.PublicKey);
			command.Parameters.AddWithValue("$kind", relayEvent.Kind);
			command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
			command.Parameters.AddWithValue("$created_at", relayEvent.CreatedAt);
			command.Parameters.AddWithValue("$id", relayEvent.Id);
		}
	}

	/// <summary>
	/// Sees to it that there is room for an event. What has expired makes way first. Then one event at a time does: the
	/// one with the least behind its key, and of several such the one that the store has had the longest. Nothing makes
	/// way that has more behind it than the new event, so an event with less behind it than everything in a full store
	/// is not kept.
	/// </summary>
	/// <returns>False if there is no room.</returns>
	private bool MakeRoom(RelayEvent relayEvent, long now, long rank, SqliteTransaction transaction)
	{
		bool IsFull() => _count + 1 > _limits.MaxStoredEvents || _bytes + relayEvent.Size > _limits.MaxStoredBytes;

		if (IsFull())
		{
			using var command = _connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM event WHERE expires_at IS NOT NULL AND expires_at <= $now RETURNING size";
			command.Parameters.AddWithValue("$now", now);
			CountDeleted(command);
		}

		while (IsFull())
		{
			using var command = _connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM event WHERE id = (SELECT id FROM event WHERE rank <= $rank ORDER BY rank ASC, stored_at ASC, id ASC LIMIT 1) RETURNING size";
			command.Parameters.AddWithValue("$rank", rank);

			if (CountDeleted(command) == 0)
			{
				return false;
			}
		}

		return true;
	}

	private void Insert(RelayEvent relayEvent, string? address, long? expiresAt, long now, long rank, SqliteTransaction transaction)
	{
		using (var command = _connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "INSERT INTO event (id, pubkey, created_at, kind, address, stored_at, expires_at, rank, size, json) VALUES ($id, $pubkey, $created_at, $kind, $address, $stored_at, $expires_at, $rank, $size, $json)";
			command.Parameters.AddWithValue("$stored_at", now);
			command.Parameters.AddWithValue("$rank", rank);
			command.Parameters.AddWithValue("$id", relayEvent.Id);
			command.Parameters.AddWithValue("$pubkey", relayEvent.PublicKey);
			command.Parameters.AddWithValue("$created_at", relayEvent.CreatedAt);
			command.Parameters.AddWithValue("$kind", relayEvent.Kind);
			command.Parameters.AddWithValue("$address", (object?)address ?? DBNull.Value);
			command.Parameters.AddWithValue("$expires_at", (object?)expiresAt ?? DBNull.Value);
			command.Parameters.AddWithValue("$size", relayEvent.Size);
			command.Parameters.AddWithValue("$json", relayEvent.Json);
			command.ExecuteNonQuery();
		}

		using var tagCommand = _connection.CreateCommand();
		tagCommand.Transaction = transaction;
		tagCommand.CommandText = "INSERT OR IGNORE INTO tag (name, value, event_id) VALUES ($name, $value, $event_id)";

		var name = tagCommand.Parameters.Add("$name", SqliteType.Text);
		var value = tagCommand.Parameters.Add("$value", SqliteType.Text);
		tagCommand.Parameters.AddWithValue("$event_id", relayEvent.Id);

		foreach (var tag in relayEvent.Tags)
		{
			// Tags with a name of one letter are the ones that can be asked for, by what follows the name.
			if (tag.Length > 1 && tag[0] is [(>= 'a' and <= 'z') or (>= 'A' and <= 'Z')] && tag[1].Length <= MaxIndexedTagValueLength)
			{
				name.Value = tag[0];
				value.Value = tag[1];
				tagCommand.ExecuteNonQuery();
			}
		}
	}

	/// <summary>Runs a command that removes events and tells the size of each, and takes them off the count.</summary>
	/// <returns>How many events were removed.</returns>
	private int CountDeleted(SqliteCommand command)
	{
		var deleted = 0;

		using var reader = command.ExecuteReader();

		while (reader.Read())
		{
			deleted++;
			_bytes -= reader.GetInt64(0);
		}

		_count -= deleted;

		return deleted;
	}
}
