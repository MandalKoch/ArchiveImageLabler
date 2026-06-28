using ArchiveImageLabler.Data;
using ArchiveImageLabler.Models;
using Microsoft.EntityFrameworkCore;

namespace ArchiveImageLabler.Services;

public static class LibrarySchemaInitializer
{
    public static async Task EnsureAsync(LibraryDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Assets');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!columns.Contains(nameof(LibraryAsset.IsIgnored)))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN IsIgnored INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains(nameof(LibraryAsset.SortKey)))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN SortKey TEXT NOT NULL DEFAULT '';", cancellationToken);
        }

        if (!columns.Contains(nameof(LibraryAsset.LabelName)))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN LabelName TEXT NOT NULL DEFAULT '';", cancellationToken);
        }

        if (!columns.Contains(nameof(LibraryAsset.DisplayOrder)))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN DisplayOrder INTEGER NULL;", cancellationToken);
        }

        if (!columns.Contains(nameof(LibraryAsset.PreviewAssetId)))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN PreviewAssetId INTEGER NULL;", cancellationToken);
        }

        var assetsMissingSortKeys = await db.Assets
            .Where(asset => asset.SortKey == string.Empty)
            .ToListAsync(cancellationToken);

        if (assetsMissingSortKeys.Count > 0)
        {
            foreach (var asset in assetsMissingSortKeys)
            {
                asset.SortKey = NaturalSortKey.Build(string.IsNullOrWhiteSpace(asset.RelativePath) ? asset.Name : asset.RelativePath);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Assets_IsIgnored ON Assets (IsIgnored);", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Assets_SortKey ON Assets (SortKey);", cancellationToken);
    }
}
