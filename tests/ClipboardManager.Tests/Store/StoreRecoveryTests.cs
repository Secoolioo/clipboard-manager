using ClipboardManager.Core.Diagnostics;
using ClipboardManager.Core.History;
using Microsoft.Data.Sqlite;

namespace ClipboardManager.Tests.Store;

[Trait("Category", "Store")]
public sealed class StoreRecoveryTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private HistoryStore Open(bool memoryOnly = false) =>
        HistoryStore.Open(_dir.Paths, memoryOnly, HistoryLimits.For(100), FileLog.Null);

    [Fact]
    public void Garbage_file_is_quarantined_and_a_fresh_database_is_used()
    {
        File.WriteAllBytes(_dir.Paths.HistoryDatabase, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        using var store = Open();

        Assert.Equal(StoreMode.Persistent, store.Mode);
        Assert.Equal(StoreNotice.RecoveredFromCorruption, store.Notice);
        Assert.True(File.Exists(_dir.Paths.QuarantineDatabase));
        Assert.Equal(CaptureOutcome.Inserted, store.Capture("works", ContentHash.Compute("works"), 1).Outcome);
    }

    [Fact]
    public void Clear_deletes_the_quarantine_copy()
    {
        File.WriteAllBytes(_dir.Paths.HistoryDatabase, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        using var store = Open();

        store.Clear(includePinned: false);

        Assert.False(File.Exists(_dir.Paths.QuarantineDatabase));
    }

    [Fact]
    public void Database_from_a_newer_version_is_left_untouched()
    {
        using (var store = Open())
        {
            store.Capture("old", ContentHash.Compute("old"), 1);
        }

        SqliteBootstrap.Initialize();
        using (var raw = new SqliteConnection($"Data Source={_dir.Paths.HistoryDatabase};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(_dir.Paths.HistoryDatabase);
        using (var store = Open())
        {
            Assert.Equal(StoreMode.MemoryFallback, store.Mode);
            Assert.Equal(StoreNotice.DatabaseFromNewerVersion, store.Notice);
            store.Capture("new", ContentHash.Compute("new"), 2);
        }

        Assert.Equal(before, File.ReadAllBytes(_dir.Paths.HistoryDatabase));
    }

    [Fact]
    public void Second_instance_on_the_same_database_falls_back_to_memory()
    {
        using var first = Open();
        first.Capture("first", ContentHash.Compute("first"), 1);

        using var second = Open();

        Assert.Equal(StoreMode.MemoryFallback, second.Mode);
        Assert.Equal(StoreNotice.DatabaseInUse, second.Notice);
    }

    [Fact]
    public void Missing_data_directory_is_recreated()
    {
        Directory.Delete(_dir.Path, recursive: true);

        using var store = Open();

        Assert.Equal(StoreMode.Persistent, store.Mode);
        Assert.True(File.Exists(_dir.Paths.HistoryDatabase));
    }

    [Fact]
    public void Unclean_shutdown_marker_triggers_integrity_check_and_still_opens()
    {
        using (var store = Open())
        {
            store.Capture("x", ContentHash.Compute("x"), 1);
        }

        File.WriteAllText(_dir.Paths.SessionMarker, string.Empty);

        using var reopened = Open();
        Assert.Equal(StoreMode.Persistent, reopened.Mode);
        Assert.Single(reopened.LoadAll());
    }
}
