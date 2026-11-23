using Microsoft.Data.Sqlite;

namespace Ledgerline.Tests.Support;

/// <summary>
/// A throwaway SQLite file per test class. A real file (not shared-cache in-memory) so concurrent
/// connections behave like the app: one writer at a time, readers never blocked thanks to WAL.
/// </summary>
internal sealed class SqliteTestDatabase : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"ledgerline-test-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={FilePath}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(FilePath + suffix);
            }
            catch (IOException)
            {
                // A lingering handle on Windows; the OS temp cleaner will get it.
            }
        }
    }
}
