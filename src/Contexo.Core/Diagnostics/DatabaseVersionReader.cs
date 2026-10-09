using Microsoft.Data.Sqlite;

namespace Contexo.Core.Diagnostics;

/// <summary>Reads <c>PRAGMA user_version</c> (the schema version) from the database file without changing it.</summary>
public static class DatabaseVersionReader
{
    /// <returns>The schema version, or null when the file does not exist or cannot be read.</returns>
    public static async Task<int?> TryReadAsync(string databasePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? null : Convert.ToInt32(value);
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
