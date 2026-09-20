using Microsoft.Data.Sqlite;

namespace Moongazing.OrionAudit.IntegrationTests;

/// <summary>
/// A throwaway SQLite database file, for the tests that need real cross-connection concurrency.
/// </summary>
/// <remarks>
/// <c>DataSource=:memory:</c> gives each connection its own private database, and the shared-cache
/// form (<c>mode=memory&amp;cache=shared</c>) serialises with table-level locks that surface as
/// <c>SQLITE_LOCKED</c> - which SQLite's busy handler does not wait on, so a contending writer can
/// only ever fail there, however correct the locking is. A file database is what a SQLite consumer
/// actually runs: the ordinary lock ladder, and the connection's busy timeout turning contention into
/// a wait.
/// </remarks>
internal sealed class TempSqliteDatabase : IDisposable
{
    private readonly string path;

    public TempSqliteDatabase()
    {
        path = Path.Combine(Path.GetTempPath(), "orionaudit-test-" + Guid.NewGuid().ToString("N") + ".db");
        ConnectionString = "Data Source=" + path;
    }

    /// <summary>The connection string every context in the test should be built from.</summary>
    public string ConnectionString { get; }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, so the file stays open after the last context is
        // disposed. Clearing the pools releases it; a leftover temp file is not worth failing a test
        // over if the platform still holds a handle.
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
