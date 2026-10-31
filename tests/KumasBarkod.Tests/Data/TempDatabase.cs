using KumasBarkod.Core.Data;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Data;

/// <summary>
/// Her test için geçici klasörde gerçek bir dosya veritabanı.
/// (WAL bellek içi veritabanında çalışmaz, bu yüzden dosya kullanılır.)
/// </summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "kumas-tests", Guid.NewGuid().ToString("N"));
        Factory = new SqliteConnectionFactory(Path.Combine(_directory, "test.db"));
    }

    public SqliteConnectionFactory Factory { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
