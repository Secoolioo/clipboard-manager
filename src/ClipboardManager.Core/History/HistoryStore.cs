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
    public const int SchemaVersion = 2;

    private const string Category = "Store";
    private const string AllEntries = "temp.all_entries";
    private const string EntryColumns = "id, hash, kind, char_count, line_count, width, height, byte_count, created_at, last_used_at, pinned_at, search_head, thumb, text, image";

    // The view leaves the image itself out: nothing that loads or searches the index can ever read a multi-megabyte blob.
    private const string IndexColumns = "id, hash, kind, char_count, line_count, width, height, byte_count, created_at, last_used_at, pinned_at, search_head, thumb, text";
    private const string ViewColumns = "id, char_count, line_count, created_at, last_used_at, pinned_at, COALESCE(search_head, text) AS search_text, in_mem, kind, width, height, byte_count, thumb";
    private const string Version1Columns = "id, hash, char_count, line_count, created_at, last_used_at, pinned_at, search_head, text";
    private const int KindText = 0;
    private const int KindImage = 1;

    private readonly AppPaths? _paths;
    private readonly FileLog _log;
    private SqliteConnection _db = null!;
    private SqliteTransaction? _tx;
    private HistoryLimits _limits;
    private long _nextId;
    private long _maxLastUsed;

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

    /// <summary>The stored PNG of an image entry, read from the tables directly (the view never carries it).</summary>
    public byte[]? GetImage(long id)
    {
        using var cmd = Command("SELECT image FROM main.entries WHERE id = $id UNION ALL SELECT image FROM mem.entries WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as byte[];
    }

    public CaptureResult Capture(string text, byte[] hash, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hash);
        return CaptureCore(text, null, hash, nowMs);
    }

    public CaptureResult CaptureImage(CapturedImage image, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(image);
        return CaptureCore(string.Empty, image, ContentHash.ComputeImage(image.Png), nowMs);
    }

    private CaptureResult CaptureCore(string text, CapturedImage? image, byte[] hash, long nowMs) =>
        InTransaction(() =>
        {
            using (var top = Command($"SELECT id, hash FROM {AllEntries} ORDER BY last_used_at DESC, id DESC LIMIT 1"))
            using (var reader = top.ExecuteReader())
            {
                if (reader.Read() && reader.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(hash))
                {
                    // Same content as the newest entry (sync tools, apps re-setting the clipboard): no write at all.
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
            var now = Monotonic(nowMs);
            if (existing is { } found)
            {
                id = found;
                UpdateLastUsed(id, now);
                outcome = CaptureOutcome.Promoted;
            }
            else
            {
                id = _nextId++;
                Insert(PersistsUnpinned ? "main" : "mem", id, text, image, hash, now, now, pinnedAtMs: null);
                outcome = CaptureOutcome.Inserted;
            }

            var removed = ApplyRetention();
            return new CaptureResult(outcome, Find(id), removed);
        });

    public HistoryEntry? Promote(long id, long nowMs) =>
        InTransaction(() => UpdateLastUsed(id, Monotonic(nowMs)) ? Find(id) : null);

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
            var now = Monotonic(nowMs);

            // An unpinned entry becomes the newest history entry, so retention never deletes the
            // very entry the user just unpinned.
            using (var update = Command(pinned
                ? $"UPDATE {schema}.entries SET pinned_at = $now WHERE id = $id"
                : $"UPDATE {schema}.entries SET pinned_at = NULL, last_used_at = $now WHERE id = $id"))
            {
                update.Parameters.AddWithValue("$now", now);
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
            Scrub(PurgeWal);
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

            var image = entry.IsImage ? GetImage(id) : null;
            DeleteRows([id]);
            return new DeletedEntry(entry, text, image);
        });

        if (deleted is not null)
        {
            Scrub(PurgeWal);
        }

        return deleted;
    }

    /// <summary>Re-inserts an entry deleted in the same popup session (undo).</summary>
    public (HistoryEntry? Entry, IReadOnlyList<long> Removed) Restore(DeletedEntry deleted)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        return InTransaction<(HistoryEntry?, IReadOnlyList<long>)>(() =>
        {
            var entry = deleted.Entry;
            CapturedImage? image = null;
            if (entry.Image is { } info)
            {
                if (deleted.ImageData is null)
                {
                    return (null, HistoryChangeBatch.NoIds);
                }

                image = new CapturedImage(deleted.ImageData, info.Thumbnail, info.Width, info.Height);
            }

            var hash = image is null ? ContentHash.Compute(deleted.Text) : ContentHash.ComputeImage(image.Png);
            using (var find = Command($"SELECT id FROM {AllEntries} WHERE hash = $hash"))
            {
                find.Parameters.AddWithValue("$hash", hash);
                if (find.ExecuteScalar() is long existing)
                {
                    return (Find(existing), HistoryChangeBatch.NoIds);
                }
            }

            var id = Locate(entry.Id) is null && entry.Id < _nextId ? entry.Id : _nextId++;
            var schema = entry.IsPinned || PersistsUnpinned ? "main" : "mem";
            Insert(schema, id, deleted.Text, image, hash, entry.CreatedAtMs, entry.LastUsedAtMs, entry.PinnedAtMs);
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

        Scrub(PurgeWal);
        Scrub(Vacuum);
        DeleteQuarantine();
        return LoadAll();
    }

    /// <summary>
    /// Switches between persistent and memory-only history (pins stay persistent in both). When
    /// switching back to persistent, <paramref name="discardMemoryEntries"/> decides whether the
    /// entries collected in memory are written to disk or dropped.
    /// </summary>
    public IReadOnlyList<HistoryEntry> SetMemoryOnly(bool memoryOnly, bool discardMemoryEntries = false)
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
                if (!discardMemoryEntries)
                {
                    Execute($"INSERT INTO main.entries ({EntryColumns}) SELECT {EntryColumns} FROM mem.entries");
                }

                Execute("DELETE FROM mem.entries");
            }

            return 0;
        });

        Mode = target;
        if (memoryOnly)
        {
            Scrub(PurgeWal);
            Scrub(Vacuum);
            DeleteQuarantine();
        }
        else
        {
            // What was collected in memory is in main now (or discarded): give its RAM back.
            Scrub(() => Execute("VACUUM mem"));
        }

        return LoadAll();
    }

    /// <summary>Number of unpinned entries currently held only in memory.</summary>
    public int CountMemoryOnlyEntries() =>
        Convert.ToInt32(Scalar("SELECT COUNT(*) FROM mem.entries WHERE pinned_at IS NULL"), CultureInfo.InvariantCulture);

    public IReadOnlyList<long> SetLimits(HistoryLimits limits)
    {
        _limits = limits;
        var removed = InTransaction(ApplyRetention);
        if (removed.Count > 0)
        {
            // The user confirmed these deletions: scrub them from the WAL like any other delete.
            Scrub(PurgeWal);
        }

        return removed;
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

            // A second session holding the exclusive lock should be detected quickly, not after 30 s.
            DefaultTimeout = 3,
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
        _maxLastUsed = Convert.ToInt64(Scalar($"SELECT COALESCE(MAX(last_used_at), 0) FROM {AllEntries}"), CultureInfo.InvariantCulture);

        // Read every row once here, inside the recovery path: a damaged data page must lead to
        // quarantine and a fresh database, not to a store that fails on first use.
        _ = LoadAll();
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

        var rebuilt = InTransaction(() =>
        {
            var rebuild = false;
            if (version < 1)
            {
                // A new database gets the latest layout directly.
                CreateEntriesTable("main");
            }
            else
            {
                if (version < 2)
                {
                    // Version 2 adds the image columns. A rebuild instead of ALTER TABLE ... ADD COLUMN
                    // keeps the large columns last (secure_delete zeroes the pages of the old table).
                    CreateEntriesTable("main", "entries_v2");
                    Execute($"INSERT INTO main.entries_v2 ({Version1Columns}) SELECT {Version1Columns} FROM main.entries");
                    Execute("DROP TABLE main.entries");
                    Execute("ALTER TABLE main.entries_v2 RENAME TO entries");
                    rebuild = true;
                }

                // Future migrations: if (version < 3) { ... }
            }

            Execute($"PRAGMA main.user_version = {SchemaVersion}");
            return rebuild;
        });

        if (rebuilt)
        {
            // The copied table and the zeroed old pages would otherwise stay in the WAL until the next checkpoint.
            PurgeWal();
        }
    }

    private void AttachMemory()
    {
        Execute("ATTACH DATABASE ':memory:' AS mem");
        CreateEntriesTable("mem");
        Execute($"CREATE TEMP VIEW all_entries AS SELECT {IndexColumns}, 0 AS in_mem FROM main.entries UNION ALL SELECT {IndexColumns}, 1 AS in_mem FROM mem.entries");
    }

    // Large columns last: loading the index reads the columns up to thumb (a few KB at most), never
    // the text of long entries or an image. Images have an empty text and kind = 1.
    private void CreateEntriesTable(string schema, string table = "entries") => Execute(
        $"""
        CREATE TABLE IF NOT EXISTS {schema}.{table} (
            id           INTEGER PRIMARY KEY,
            hash         BLOB    NOT NULL UNIQUE,
            kind         INTEGER NOT NULL DEFAULT 0,
            char_count   INTEGER NOT NULL,
            line_count   INTEGER NOT NULL,
            width        INTEGER NULL,
            height       INTEGER NULL,
            byte_count   INTEGER NOT NULL DEFAULT 0,
            created_at   INTEGER NOT NULL,
            last_used_at INTEGER NOT NULL,
            pinned_at    INTEGER NULL,
            search_head  TEXT    NULL,
            thumb        BLOB    NULL,
            text         TEXT    NOT NULL,
            image        BLOB    NULL
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

    private void Insert(string schema, long id, string text, CapturedImage? image, byte[] hash, long createdAtMs, long lastUsedAtMs, long? pinnedAtMs)
    {
        using var cmd = Command(
            $"INSERT INTO {schema}.entries ({EntryColumns}) VALUES ($id, $hash, $kind, $chars, $lines, $width, $height, $bytes, $created, $used, $pinned, $head, $thumb, $text, $image)");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$kind", image is null ? KindText : KindImage);
        cmd.Parameters.AddWithValue("$chars", text.Length);
        cmd.Parameters.AddWithValue("$lines", TextMetrics.CountLines(text));
        cmd.Parameters.AddWithValue("$width", image is null ? DBNull.Value : image.Width);
        cmd.Parameters.AddWithValue("$height", image is null ? DBNull.Value : image.Height);
        cmd.Parameters.AddWithValue("$bytes", image is null ? 0L : image.Png.LongLength);
        cmd.Parameters.AddWithValue("$created", createdAtMs);
        cmd.Parameters.AddWithValue("$used", lastUsedAtMs);
        cmd.Parameters.AddWithValue("$pinned", pinnedAtMs is { } p ? p : DBNull.Value);
        cmd.Parameters.AddWithValue("$head", text.Length > SearchHeadLength ? text[..SearchHeadLength] : DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", image is null ? DBNull.Value : image.Thumbnail);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$image", image is null ? DBNull.Value : image.Png);
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
        PinnedAtMs: r.IsDBNull(5) ? null : r.GetInt64(5),
        Image: r.GetInt64(8) == KindImage ? ReadImage(r) : null);

    private static EntryImage ReadImage(SqliteDataReader r) => new(
        Width: r.IsDBNull(9) ? 0 : r.GetInt32(9),
        Height: r.IsDBNull(10) ? 0 : r.GetInt32(10),
        ByteCount: r.GetInt64(11),
        Thumbnail: r.IsDBNull(12) ? Array.Empty<byte>() : r.GetFieldValue<byte[]>(12));

    private List<(long Id, long Chars, long Bytes, bool IsImage)> UnpinnedByRecency()
    {
        var rows = new List<(long, long, long, bool)>();
        using var cmd = Command($"SELECT id, char_count, byte_count, kind FROM {AllEntries} WHERE pinned_at IS NULL ORDER BY last_used_at DESC, id DESC");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3) == KindImage));
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

    /// <summary>
    /// Unpinned entries beyond the count limit or a size budget. Strictly oldest first: beyond the
    /// count limit everything older goes; once a text no longer fits the text budget, it and every
    /// older text goes, and images have their own budget the same way. A large picture therefore never
    /// pushes texts out of the history, and many texts never push out pictures.
    /// </summary>
    private List<long> SelectDoomed(HistoryLimits limits)
    {
        var doomed = new List<long>();
        var (kept, chars, bytes) = (0, 0L, 0L);
        var (textFull, imagesFull) = (false, false);
        foreach (var (id, entryChars, entryBytes, isImage) in UnpinnedByRecency())
        {
            // The newest entry always survives, even if it alone exceeds a budget.
            if (kept > 0)
            {
                if (isImage)
                {
                    imagesFull |= bytes + entryBytes > limits.MaxImageBytes;
                }
                else
                {
                    textFull |= chars + entryChars > limits.MaxTotalChars;
                }

                if (kept >= limits.MaxItems || (isImage ? imagesFull : textFull))
                {
                    doomed.Add(id);
                    continue;
                }
            }

            kept++;
            chars += entryChars;
            bytes += entryBytes;
        }

        return doomed;
    }

    /// <summary>Timestamps never go backwards (clock changes), so ordering and retention stay correct.</summary>
    private long Monotonic(long nowMs)
    {
        _maxLastUsed = Math.Max(nowMs, _maxLastUsed + 1);
        return _maxLastUsed;
    }

    /// <summary>
    /// Scrubbing (checkpoint, vacuum) runs after the change was committed. If it fails the change
    /// still stands and must still be published, so failures are logged, not thrown.
    /// </summary>
    private void Scrub(Action scrub)
    {
        try
        {
            scrub();
        }
        catch (SqliteException ex)
        {
            _log.Warning(Category, "Scrubbing deleted data from the database files failed", ex);
        }
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
        // An in-memory database keeps freed pages allocated until it is vacuumed; with pictures in the
        // history that can be the whole image budget of RAM.
        Execute("VACUUM mem");
        Execute("VACUUM main");
        if (Mode != StoreMode.MemoryFallback)
        {
            PurgeWal();
        }
    }

    private sealed class NewerSchemaException : Exception
    {
    }
}
