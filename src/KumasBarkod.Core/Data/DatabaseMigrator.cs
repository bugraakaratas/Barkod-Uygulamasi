using Microsoft.Data.Sqlite;

namespace KumasBarkod.Core.Data;

/// <summary>
/// Veritabanını en son şema sürümüne getirir. Sürüm, SQLite'ın
/// <c>PRAGMA user_version</c> alanında tutulur. Her göç kendi işleminde
/// (transaction) uygulanır: yarıda kesilirse hiçbir parçası kalmaz.
/// </summary>
public static class DatabaseMigrator
{
    /// <summary>İstasyon (QC) veritabanı.</summary>
    /// <returns>Uygulanan göç sayısı.</returns>
    public static int Migrate(SqliteConnectionFactory factory) => Migrate(factory, Migrations.All);

    /// <summary>Verilen göç listesiyle (ör. sunucu veritabanı). Sürümler 1'den başlar, artan sırada.</summary>
    /// <returns>Uygulanan göç sayısı.</returns>
    public static int Migrate(SqliteConnectionFactory factory, IReadOnlyList<Migration> migrations)
    {
        using var connection = factory.Open();

        var latest = migrations[^1].Version;
        var current = GetSchemaVersion(connection);
        if (current > latest)
        {
            throw new InvalidOperationException(
                $"Veritabanı şeması (v{current}) bu uygulama sürümünden (v{latest}) yeni. " +
                "Uygulamayı güncelleyin; eski sürümle açmak veriyi bozabilir.");
        }

        var applied = 0;
        foreach (var migration in migrations.Where(m => m.Version > current))
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql + $"\nPRAGMA user_version = {migration.Version};";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            applied++;
        }

        return applied;
    }

    public static int GetSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
