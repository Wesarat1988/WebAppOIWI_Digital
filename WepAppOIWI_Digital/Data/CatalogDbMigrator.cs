using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace WepAppOIWI_Digital.Data;

public static class CatalogDbMigrator
{
    public static async Task EnsureSchemaAsync(
        IDbContextFactory<AppDbContext> factory,
        ILogger<CatalogDbMigrator> logger,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var hasColumn = await ColumnExistsAsync(connection, "Documents", "UpdatedAtUnixMs", cancellationToken).ConfigureAwait(false);

        if (!hasColumn)
        {
            logger.LogInformation("Adding UpdatedAtUnixMs column to Documents table.");
            await ExecuteNonQueryAsync(connection, "ALTER TABLE Documents ADD COLUMN UpdatedAtUnixMs INTEGER NOT NULL DEFAULT 0;", cancellationToken).ConfigureAwait(false);
        }

        logger.LogDebug("Backfilling UpdatedAtUnixMs values based on UpdatedAt column.");
        const string backfillSql = @"UPDATE Documents
SET UpdatedAtUnixMs = CASE
    WHEN UpdatedAt IS NOT NULL THEN CAST(strftime('%s', UpdatedAt) AS INTEGER) * 1000
    ELSE 0
END;";
        await ExecuteNonQueryAsync(connection, backfillSql, cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Ensuring index IX_Documents_UpdatedAtUnixMs exists.");
        await ExecuteNonQueryAsync(connection, "CREATE INDEX IF NOT EXISTS IX_Documents_UpdatedAtUnixMs ON Documents(UpdatedAtUnixMs DESC);", cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(1))
            {
                var name = reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
