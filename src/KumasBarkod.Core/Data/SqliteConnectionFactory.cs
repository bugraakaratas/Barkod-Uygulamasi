using Microsoft.Data.Sqlite;

namespace KumasBarkod.Core.Data;

/// <summary>
/// Veritabanı bağlantılarını tek yerden, aynı ayarlarla açar.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Açık bir bağlantı döner. Her bağlantıda:
    /// WAL (okuma yazmayı engellemez), synchronous=FULL (elektrik kesintisinde
    /// onaylanmış işlem kaybolmaz), meşgul beklemesi 5 sn.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            PRAGMA busy_timeout = 5000;
            """;
        command.ExecuteNonQuery();

        return connection;
    }
}
