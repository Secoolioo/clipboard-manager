using System.Globalization;
using ClipboardManager.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Core.History;

/// <summary>
/// SQLite-backed history. Not thread-safe by design: exactly one owner (<see cref="DbWorker"/>)
/// calls it. Unpinned entries live in an attached in-memory database while
/// <see cref="StoreMode.MemoryOnlyByChoice"/> is active, so they never touch the disk.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    public const int SearchHeadLength = 4096;
    public const int SchemaVersion = 1;

    private const string Category = "Store";
    private const string AllEntries = "temp.all_entries";
    private const string EntryColumns = "id, hash, char_count, line_count, created_at, last_used_at, pinned_at, search_head, text";
    private const string ViewColumns = "id, char_count, line_count, created_at, last_used_at, pinned_at, COALESCE(search_head, text) AS search_text, in_mem";

    private readonly AppPaths? _paths;
    private readonly FileLog _log;
    private SqliteConnection _db = null!;
    private SqliteTransaction? _tx;
    private HistoryLimits _limits;
    private long _nextId;

    private HistoryStore(AppPaths? paths, HistoryLimits limits, FileLog log)
    {
        _paths = paths;
        _limits = limits;
        _log = log;
    }

    public StoreMode Mode { get; private set; }

    public StoreNotice Notice { get; private set; }

    /// <summary>Opens (and migrates or recovers) the store. Never throws for database problems; falls back to memory instead.</summary>
    public static HistoryStore Open(AppPaths? paths, bool memoryOnly, HistoryLimits limits, FileLog log)
    {
        SqliteBootstrap.Initialize();
        var store = new HistoryStore(paths, limits, log);
        store.OpenCore(memoryOnly);
        return store;
    }

    private bool PersistsUnpinned => Mode == StoreMode.Persistent;

    public IReadOnlyList<HistoryEntry> LoadAll()
    {
        var list = new List<HistoryEntry>();
        using var cmd = Command($"SELECT {ViewColumns} FROM {AllEntries}");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadEntry(reader));
        }

        return list;
    }

    public string? GetText(long id)
    {
        using var cmd = Command($"SELECT text FROM {AllEntries} WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    public CaptureResult Capture(string text, byte[] hash, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hash);

        return InTransaction(() =>
        {
            using (var top = Command($"SELECT id, hash FROM {AllEntries} ORDER BY last_used_at DESC, id DESC LIMIT 1"))
            using (var reader = top.ExecuteReader())
            {
                if (reader.Read() && reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(hash))
                {
                    // Same text as the newest entry (sync tools, apps re-setting the clipboard): no write at all.
                    var topId = reader.GetInt64(0);
                    reader.Close();
                    return new CaptureResult(CaptureOutcome.Unchanged, Find(topId), HistoryChangeBatch.NoIds);
                }
            }

            long? existing;
            using (var find = Command($"SELECT id FROM {AllEntries} WHERE hash = $hash"))
            {
                find.Parameters.AddWithValue("$hash", hash);
                existing = find.ExecuteScalar() as long?;
            }

            CaptureOutcome outcome;
            long id;
            if (existing is { } found)
            {
                id = found;
                UpdateLastUsed(id, nowMs);
                outcome = CaptureOutcome.Promoted;
            }
            else
            {
                id = _nextId++;
                Insert(PersistsUnpinned ? "main" : "mem", id, text, hash, nowMs, nowMs, pinnedAtMs: null);
                outcome = CaptureOutcome.Inserted;
            }

            var removed = ApplyRetention();
            return new CaptureResult(outcome, Find(id), removed);
        });
    }

    public HistoryEntry? Promote(long id, long nowMs) =>
        InTransaction(() => UpdateLastUsed(id, nowMs) ? Find(id) : null);

    public (HistoryEntry? Entry, IReadOnlyList<long> Removed) SetPinned(long id, bool pinned, long nowMs)
    {
        var result = InTransaction<(HistoryEntry?, IReadOnlyList<long>)>(() =>
        {
            var location = Locate(id);
            if (location is null)
            {
                return (null, HistoryChangeBatch.NoIds);
            }

            var schema = location.Value.InMemory ? "mem" : "main";
            using (var update = Command($"UPDATE {schema}.entries SET pinned_at = $p WHERE id = $id"))
            {
                update.Parameters.AddWithValue("$p", pinned ? nowMs : DBNull.Value);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }

            // Pinned entries are always persisted; unpinned ones follow the store mode.
            var targetSchema = pinned || PersistsUnpinned ? "main" : "mem";
            if (targetSchema != schema)
            {
                Move(id, schema, targetSchema);
            }

            var removed = pinned ? HistoryChangeBatch.NoIds : ApplyRetention();
            return (Find(id), removed);
        });

        if (!pinned && !PersistsUnpinned)
        {
            PurgeWal();
        }

        return result;
    }

    public DeletedEntry? Delete(long id)
    {
        var deleted = InTransaction(() =>
        {
            var entry = Find(id);
            var text = GetText(id);
            if (entry is null || text is null)
            {
                return null;
            }

            DeleteRows([id]);
            return new DeletedEntry(entry, text);
        });

        if (deleted is not null)
        {
            PurgeWal();
        }

        return deleted;
    }

    /// <summary>Re-inserts an entry deleted in the same popup session (undo).</summary>
    public (HistoryEntry? Entry, IReadOnlyList<long> Removed) Restore(DeletedEntry deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        return InTransaction<(HistoryEntry?, IReadOnlyList<long>)>(() =>
        {
            var hash = ContentHash.Compute(deleted.Text);
            using (var find = Command($"SELECT id FROM {AllEntries} WHERE hash = $hash"))
            {
                find.Parameters.AddWithValue("$hash", hash);
                if (find.ExecuteScalar() is long existing)
                {
                    return (Find(existing), HistoryChangeBatch.NoIds);
                }
            }

            var entry = deleted.Entry;
            var id = Locate(entry.Id) is null && entry.Id < _nextId ? entry.Id : _nextId++;
            var schema = entry.IsPinned || PersistsUnpinned ? "main" : "mem";
            Insert(schema, id, deleted.Text, hash, entry.CreatedAtMs, entry.LastUsedAtMs, entry.PinnedAtMs);
            var removed = entry.IsPinned ? HistoryChangeBatch.NoIds : ApplyRetention();
            return (Find(id), removed);
        });
    }

    /// <summary>Deletes history (optionally including pins) and scrubs the files on disk.</summary>
    public IReadOnlyList<HistoryEntry> Clear(bool includePinned)
    {
        InTransaction(() =>
        {
            var condition = includePinned ? string.Empty : " WHERE pinned_at IS NULL";
            Execute($"DELETE FROM main.entries{condition}");
            Execute($"DELETE FROM mem.entries{condition}");
            return 0;
        });

        PurgeWal();
        Vacuum();
        DeleteQuarantine();
        return LoadAll();
    }

    /// <summary>Switches between persistent and memory-only history (pins stay persistent in both).</summary>
    public IReadOnlyList<HistoryEntry> SetMemoryOnly(bool memoryOnly)
    {
        if (Mode == StoreMode.MemoryFallback)
        {
            return LoadAll();
        }

        var target = memoryOnly ? StoreMode.MemoryOnlyByChoice : StoreMode.Persistent;
        if (target == Mode)
        {
            return LoadAll();
        }

        InTransaction(() =>
        {
            if (memoryOnly)
            {
                Execute($"INSERT INTO mem.entries ({EntryColumns}) SELECT {EntryColumns} FROM main.entries WHERE pinned_at IS NULL");
                Execute("DELETE FROM main.entries WHERE pinned_at IS NULL");
            }
            else
            {
                Execute($"INSERT INTO main.entries ({EntryColumns}) SELECT {EntryColumns} FROM mem.entries");
                Execute("DELETE FROM mem.entries");
            }

            return 0;
        });

        Mode = target;
        if (memoryOnly)
        {
            PurgeWal();
            Vacuum();
            DeleteQuarantine();
        }

        return LoadAll();
    }

    public IReadOnlyList<long> SetLimits(HistoryLimits limits)
    {
        _limits = limits;
        return InTransaction(ApplyRetention);
    }

    /// <summary>How many unpinned entries a new limit would delete (for the confirmation dialog).</summary>
    public int CountExceeding(HistoryLimits limits) => SelectDoomed(limits).Count;

    public void Dispose()
    {
        try
        {
            PurgeWal();
        }
        catch (SqliteException ex)
        {
            _log.Warning(Category, "Checkpoint on close failed", ex);
        }

        _db.Dispose();
        if (_paths is not null && Mode != StoreMode.MemoryFallback)
        {
            TryDelete(_paths.SessionMarker);
        }
    }

    // ---- opening, migration, recovery -------------------------------------------------------

    private void OpenCore(bool memoryOnly)
    {
        if (_paths is null)
        {
            OpenMemory(StoreNotice.None);
            return;
        }

        try
        {
            OpenFile(memoryOnly);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            _log.Warning(Category, "Database corrupt, moving it to quarantine", ex);
            _db?.Dispose();
            Quarantine();
            try
            {
                OpenFile(memoryOnly);
                Notice = StoreNotice.RecoveredFromCorruption;
            }
            catch (Exception retry) when (retry is SqliteException or IOException or UnauthorizedAccessException)
            {
                _log.Error(Category, "Database unusable after recovery", retry);
                _db?.Dispose();
                OpenMemory(StoreNotice.DatabaseUnavailable);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            _log.Warning(Category, "Database locked by another instance", ex);
            _db?.Dispose();
            OpenMemory(StoreNotice.DatabaseInUse);
        }
        catch (NewerSchemaException)
        {
            _log.Warning(Category, "Database was created by a newer version; not touching it");
            _db?.Dispose();
            OpenMemory(StoreNotice.DatabaseFromNewerVersion);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _log.Error(Category, "Database unavailable", ex);
            _db?.Dispose();
            OpenMemory(StoreNotice.DatabaseUnavailable);
        }
    }

    private void OpenFile(bool memoryOnly)
    {
        _paths!.EnsureCreated();
        var unclean = File.Exists(_paths.SessionMarker);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.HistoryDatabase,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();

        Execute("PRAGMA main.journal_mode = WAL");
        Execute("PRAGMA main.locking_mode = EXCLUSIVE");
        Execute("PRAGMA main.synchronous = FULL");
        Execute("PRAGMA secure_delete = ON");
        Execute("PRAGMA temp_store = MEMORY");

        // The first write takes the exclusive lock (a second session of the same user fails here).
        Migrate();

        if (unclean)
        {
            var check = Convert.ToString(Scalar("PRAGMA main.quick_check"), CultureInfo.InvariantCulture);
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteException("quick_check failed", 11);
            }
        }

        AttachMemory();
        Mode = memoryOnly ? StoreMode.MemoryOnlyByChoice : StoreMode.Persistent;
        if (memoryOnly)
        {
            // Unpinned rows written while the setting was off move to memory and are scrubbed from disk.
            InTransaction(() =>
            {
                Execute($"INSERT INTO mem.entries ({EntryColumns}) SELECT {EntryColumns} FROM main.entries WHERE pinned_at IS NULL");
                Execute("DELETE FROM main.entries WHERE pinned_at IS NULL");
                return 0;
            });
            PurgeWal();
        }

        _nextId = Convert.ToInt64(Scalar($"SELECT COALESCE(MAX(id), 0) + 1 FROM {AllEntries}"), CultureInfo.InvariantCulture);
        File.WriteAllText(_paths.SessionMarker, string.Empty);
    }

    private void OpenMemory(StoreNotice notice)
    {
        _db = new SqliteConnection("Data Source=:memory:;Pooling=False");
        _db.Open();
        Execute("PRAGMA temp_store = MEMORY");
        Migrate();
        AttachMemory();
        Mode = StoreMode.MemoryFallback;
        Notice = notice;
        _nextId = 1;
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA main.user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion)
        {
            throw new NewerSchemaException();
        }

        InTransaction(() =>
        {
            if (version < 1)
            {
                CreateEntriesTable("main");
            }

            // Future migrations: if (version < 2) { ... }
            Execute($"PRAGMA main.user_version = {SchemaVersion}");
            return 0;
        });
    }

    private void AttachMemory()
    {
        Execute("ATTACH DATABASE ':memory:' AS mem");
        CreateEntriesTable("mem");
        Execute($"CREATE TEMP VIEW all_entries AS SELECT {EntryColumns}, 0 AS in_mem FROM main.entries UNION ALL SELECT {EntryColumns}, 1 AS in_mem FROM mem.entries");
    }

    private void CreateEntriesTable(string schema) => Execute(
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.entries (
            id           INTEGER PRIMARY KEY,
            hash         BLOB    NOT NULL UNIQUE,
            char_count   INTEGER NOT NULL,
            line_count   INTEGER NOT NULL,
            created_at   INTEGER NOT NULL,
            last_used_at INTEGER NOT NULL,
            pinned_at    INTEGER NULL,
            search_head  TEXT    NULL,
            text         TEXT    NOT NULL
        )
        """);

    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    private void Quarantine()
    {
        var paths = _paths!;
        try
        {
            if (File.Exists(paths.HistoryDatabase))
            {
                File.Move(paths.HistoryDatabase, paths.QuarantineDatabase, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Category, "Could not move corrupt database", ex);
        }

        TryDelete(paths.HistoryDatabase + "-wal");
        TryDelete(paths.HistoryDatabase + "-shm");
        TryDelete(paths.SessionMarker);
    }

    private void DeleteQuarantine()
    {
        if (_paths is not null)
        {
            TryDelete(_paths.QuarantineDatabase);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(Category, "Could not delete a data file", ex);
        }
    }

    // ---- row helpers ------------------------------------------------------------------------

    private void Insert(string schema, long id, string text, byte[] hash, long createdAtMs, long lastUsedAtMs, long? pinnedAtMs)
    {
        using var cmd = Command(
            $"INSERT INTO {schema}.entries ({EntryColumns}) VALUES ($id, $hash, $chars, $lines, $created, $used, $pinned, $head, $text)");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$chars", text.Length);
        cmd.Parameters.AddWithValue("$lines", TextMetrics.CountLines(text));
        cmd.Parameters.AddWithValue("$created", createdAtMs);
        cmd.Parameters.AddWithValue("$used", lastUsedAtMs);
        cmd.Parameters.AddWithValue("$pinned", pinnedAtMs is { } p ? p : DBNull.Value);
        cmd.Parameters.AddWithValue("$head", text.Length > SearchHeadLength ? text[..SearchHeadLength] : DBNull.Value);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.ExecuteNonQuery();
    }

    private bool UpdateLastUsed(long id, long nowMs)
    {
        var location = Locate(id);
        if (location is null)
        {
            return false;
        }

        using var cmd = Command($"UPDATE {(location.Value.InMemory ? "mem" : "main")}.entries SET last_used_at = $now WHERE id = $id");
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return true;
    }

    private void Move(long id, string fromSchema, string toSchema)
    {
        using (var copy = Command($"INSERT INTO {toSchema}.entries ({EntryColumns}) SELECT {EntryColumns} FROM {fromSchema}.entries WHERE id = $id"))
        {
            copy.Parameters.AddWithValue("$id", id);
            copy.ExecuteNonQuery();
        }

        using var delete = Command($"DELETE FROM {fromSchema}.entries WHERE id = $id");
        delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery();
    }

    private (bool InMemory, bool Pinned)? Locate(long id)
    {
        using var cmd = Command($"SELECT in_mem, pinned_at IS NOT NULL FROM {AllEntries} WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0) == 1, reader.GetInt64(1) == 1) : null;
    }

    private HistoryEntry? Find(long id)
    {
        using var cmd = Command($"SELECT {ViewColumns} FROM {AllEntries} WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static HistoryEntry ReadEntry(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        SearchText: r.GetString(6),
        CharCount: r.GetInt32(1),
        LineCount: r.GetInt32(2),
        CreatedAtMs: r.GetInt64(3),
        LastUsedAtMs: r.GetInt64(4),
        PinnedAtMs: r.IsDBNull(5) ? null : r.GetInt64(5));

    private List<(long Id, long Chars)> UnpinnedByRecency()
    {
        var rows = new List<(long, long)>();
        using var cmd = Command($"SELECT id, char_count FROM {AllEntries} WHERE pinned_at IS NULL ORDER BY last_used_at DESC, id DESC");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return rows;
    }

    private IReadOnlyList<long> ApplyRetention()
    {
        var doomed = SelectDoomed(_limits);
        if (doomed.Count > 0)
        {
            DeleteRows(doomed);
        }

        return doomed;
    }

    /// <summary>Oldest unpinned entries beyond the count limit or the total size budget.</summary>
    private List<long> SelectDoomed(HistoryLimits limits)
    {
        var doomed = new List<long>();
        var (kept, total) = (0, 0L);
        foreach (var (id, chars) in UnpinnedByRecency())
        {
            // The newest entry always survives, even if it alone exceeds the size budget.
            if (kept == 0 || (kept < limits.MaxItems && total + chars <= limits.MaxTotalChars))
            {
                kept++;
                total += chars;
            }
            else
            {
                doomed.Add(id);
            }
        }

        return doomed;
    }

    private void DeleteRows(IReadOnlyList<long> ids)
    {
        foreach (var schema in new[] { "main", "mem" })
        {
            using var cmd = Command($"DELETE FROM {schema}.entries WHERE id = $id");
            var parameter = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids)
            {
                parameter.Value = id;
                cmd.ExecuteNonQuery();
            }
        }
    }

    // ---- SQL plumbing -----------------------------------------------------------------------

    private T InTransaction<T>(Func<T> work)
    {
        if (_tx is not null)
        {
            return work();
        }

        _tx = _db.BeginTransaction();
        try
        {
            var result = work();
            _tx.Commit();
            return result;
        }
        catch
        {
            _tx.Rollback();
            throw;
        }
        finally
        {
            _tx.Dispose();
            _tx = null;
        }
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var cmd = Command(sql);
        return cmd.ExecuteScalar();
    }

    /// <summary>Moves committed pages into the database file and truncates the WAL, so deleted content is gone from it.</summary>
    private void PurgeWal()
    {
        if (Mode != StoreMode.MemoryFallback)
        {
            Execute("PRAGMA main.wal_checkpoint(TRUNCATE)");
        }
    }

    private void Vacuum()
    {
        if (Mode != StoreMode.MemoryFallback)
        {
            Execute("VACUUM main");
            PurgeWal();
        }
    }

    private sealed class NewerSchemaException : Exception
    {
    }
}
