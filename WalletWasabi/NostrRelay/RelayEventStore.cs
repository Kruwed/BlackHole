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
/// The room for events has a limit, in events and in bytes. When it is used up, either the oldest events make way for
/// a new one, or the new one is refused: that is for whoever opens the store to say.
/// </para>
/// </remarks>
public sealed class RelayEventStore : IDisposable
{
	/// <summary>A tag is found by its value up to this many characters. A tag with a longer value is kept with its event, and cannot be asked for.</summary>
	public const int MaxIndexedTagValueLength = 1_024;

	private const string InMemory = ":memory:";

	private readonly SqliteConnection _connection;
	private readonly RelayLimits _limits;
	private readonly bool _dropsOldestWhenFull;
	private readonly KindList? _servedKinds;
	private readonly Lock _lock = new();

	private long _count;
	private long _bytes;
	private bool _isDisposed;

	private RelayEventStore(SqliteConnection connection, RelayLimits limits, bool dropsOldestWhenFull, KindList? servedKinds)
	{
		_connection = connection;
		_limits = limits;
		_dropsOldestWhenFull = dropsOldestWhenFull;
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
	/// <param name="dropsOldestWhenFull">Whether the oldest events make way when the room is used up. Otherwise new events are refused then.</param>
	/// <param name="servedKinds">The kinds of events that are handed out. What the file keeps of other kinds stays there, and is not handed out. Null hands out everything.</param>
	/// <exception cref="SqliteException">The file is there and cannot be used.</exception>
	public static RelayEventStore Open(string? filePath, RelayLimits limits, bool dropsOldestWhenFull, KindList? servedKinds = null)
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

			Execute(
				connection,
				"""
				CREATE TABLE IF NOT EXISTS event (
					id TEXT NOT NULL PRIMARY KEY, /* 64 characters of lowercase hex */
					pubkey TEXT NOT NULL,
					created_at INTEGER NOT NULL, /* seconds since 1970, as the maker of the event tells it */
					kind INTEGER NOT NULL,
					address TEXT, /* the name of an event of an addressable kind; NULL for the other kinds */
					expires_at INTEGER, /* seconds since 1970; NULL ~ the event stays until the room is used up */
					size INTEGER NOT NULL, /* the bytes of json */
					json TEXT NOT NULL
				) WITHOUT ROWID;
				CREATE INDEX IF NOT EXISTS event_time_idx ON event (created_at DESC, id);
				CREATE INDEX IF NOT EXISTS event_kind_idx ON event (kind, created_at DESC);
				CREATE INDEX IF NOT EXISTS event_author_idx ON event (pubkey, kind, created_at DESC);
				CREATE INDEX IF NOT EXISTS event_expiry_idx ON event (expires_at) WHERE expires_at IS NOT NULL;
				CREATE TABLE IF NOT EXISTS tag (
					name TEXT NOT NULL, /* one letter */
					value TEXT NOT NULL,
					event_id TEXT NOT NULL REFERENCES event (id) ON DELETE CASCADE,
					PRIMARY KEY (name, value, event_id)
				) WITHOUT ROWID;
				CREATE INDEX IF NOT EXISTS tag_event_idx ON tag (event_id);
				""");

			var store = new RelayEventStore(connection, limits, dropsOldestWhenFull, servedKinds);
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
	public StoreOutcome Add(RelayEvent relayEvent, long? expiresAt, long now)
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

				if (!MakeRoom(relayEvent, now, transaction))
				{
					// What was removed to make way stays removed only if the event is kept.
					transaction.Rollback();
					CountEvents();
					return StoreOutcome.Full;
				}

				Insert(relayEvent, address, expiresAt, transaction);
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
	/// Sees to it that there is room for an event. What has expired makes way first. Then, if the oldest events make
	/// way for new ones, the events that are older than the given one do: the store keeps the newest events, so an
	/// event that is older than everything in a full store is not kept.
	/// </summary>
	/// <returns>False if there is no room.</returns>
	private bool MakeRoom(RelayEvent relayEvent, long now, SqliteTransaction transaction)
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
			if (!_dropsOldestWhenFull)
			{
				return false;
			}

			// One in a hundred of what there is at a time, so that a large store is not at its limit again with the next event.
			using var command = _connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM event WHERE id IN (SELECT id FROM event WHERE created_at <= $created_at ORDER BY created_at ASC, id DESC LIMIT $batch) RETURNING size";
			command.Parameters.AddWithValue("$created_at", relayEvent.CreatedAt);
			command.Parameters.AddWithValue("$batch", Math.Clamp(_count / 100, 1, 1_000));

			if (CountDeleted(command) == 0)
			{
				return false;
			}
		}

		return true;
	}

	private void Insert(RelayEvent relayEvent, string? address, long? expiresAt, SqliteTransaction transaction)
	{
		using (var command = _connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "INSERT INTO event (id, pubkey, created_at, kind, address, expires_at, size, json) VALUES ($id, $pubkey, $created_at, $kind, $address, $expires_at, $size, $json)";
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
