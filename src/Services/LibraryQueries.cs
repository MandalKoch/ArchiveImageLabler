using FileZipPreview.Data;
using FileZipPreview.Models;
using Microsoft.EntityFrameworkCore;

namespace FileZipPreview.Services;

public sealed class LibraryQueries(IDbContextFactory<LibraryDbContext> dbFactory)
{
    public async Task<LibrarySnapshot> GetSnapshotAsync(
        long? selectedContainerId,
        string search,
        int? rating,
        bool unratedOnly,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var containerRows = await db.Assets
            .AsNoTracking()
            .Where(asset => asset.IsAvailable && (asset.Kind == AssetKinds.Folder || asset.Kind == AssetKinds.Zip))
            .OrderBy(asset => asset.SortKey)
            .ThenBy(asset => asset.Id)
            .Select(asset => new ContainerRow(
                asset.Id,
                asset.ParentId,
                asset.Name,
                asset.LabelName,
                asset.Kind,
                asset.SourceType,
                asset.RelativePath,
                asset.Depth,
                asset.Description,
                asset.Tags,
                asset.Rating,
                asset.IsIgnored,
                asset.ScanError))
            .ToListAsync(cancellationToken);

        var containers = containerRows
            .Select(container => new AssetCard(
                container.Id,
                container.ParentId,
                container.Name,
                container.LabelName,
                container.Kind,
                container.SourceType,
                container.RelativePath,
                container.Depth,
                container.Description,
                container.Tags,
                container.Rating,
                container.IsIgnored,
                container.ScanError,
                0,
                0,
                null,
                null))
            .ToList();

        var containerIds = containerRows.Select(container => container.Id).ToArray();
        var childRows = await db.Assets
            .AsNoTracking()
            .Where(asset =>
                asset.IsAvailable &&
                !asset.IsIgnored &&
                asset.ParentId != null &&
                (asset.Kind == AssetKinds.Image || asset.Kind == AssetKinds.Audio || asset.Kind == AssetKinds.Video))
            .Select(asset => new MediaRow(asset.Id, asset.ParentId!.Value, asset.Kind, asset.SourceType))
            .ToListAsync(cancellationToken);

        var directChildCounts = await db.Assets
            .AsNoTracking()
            .Where(asset => asset.IsAvailable && asset.ParentId != null && containerIds.Contains(asset.ParentId.Value))
            .GroupBy(asset => asset.ParentId!.Value)
            .Select(group => new { ParentId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ParentId, row => row.Count, cancellationToken);

        var childrenByParent = containerRows
            .Where(container => container.ParentId is not null)
            .GroupBy(container => container.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        containers = containers
            .Select(container =>
            {
                var mediaRows = MediaForContainer(container, containerRows, childrenByParent, childRows);
                var preview = mediaRows
                    .Where(media => media.Kind is AssetKinds.Image or AssetKinds.Video)
                    .OrderBy(media => media.Kind == AssetKinds.Image ? 0 : 1)
                    .ThenBy(media => media.Id)
                    .FirstOrDefault();

                return container with
                {
                    ImageCount = mediaRows.Count,
                    ChildCount = directChildCounts.GetValueOrDefault(container.Id),
                    PreviewImageId = preview?.Id,
                    PreviewKind = preview?.Kind
                };
            })
            .ToList();

        var activeContainerId = selectedContainerId ?? containers.FirstOrDefault(container => container.ImageCount > 0)?.Id;

        var imageQuery = db.Assets
            .AsNoTracking()
            .Where(asset =>
                asset.IsAvailable &&
                !asset.IsIgnored &&
                (asset.Kind == AssetKinds.Image || asset.Kind == AssetKinds.Audio || asset.Kind == AssetKinds.Video));

        ContainerRow? activeContainer = null;
        if (activeContainerId is not null)
        {
            activeContainer = containerRows.SingleOrDefault(container => container.Id == activeContainerId.Value);
            if (activeContainer is not null)
            {
                var parentIds = ParentIdsForContainer(activeContainer, childrenByParent).ToArray();
                imageQuery = activeContainer.Kind == AssetKinds.Folder
                    ? imageQuery.Where(asset =>
                        asset.ParentId != null &&
                        parentIds.Contains(asset.ParentId.Value) &&
                        (activeContainer.SourceType != AssetSourceTypes.FileSystemFolder ||
                         asset.SourceType == AssetSourceTypes.FileSystemImage ||
                         asset.SourceType == AssetSourceTypes.FileSystemAudio ||
                         asset.SourceType == AssetSourceTypes.FileSystemVideo))
                    : imageQuery.Where(asset => asset.ParentId != null && parentIds.Contains(asset.ParentId.Value));
            }
        }

        imageQuery = ApplyFilters(imageQuery, search, rating, unratedOnly);

        var images = await imageQuery
            .OrderBy(asset => asset.SortKey)
            .ThenBy(asset => asset.Id)
            .Take(240)
            .Select(asset => new AssetCard(
                asset.Id,
                asset.ParentId,
                asset.Name,
                asset.LabelName,
                asset.Kind,
                asset.SourceType,
                asset.RelativePath,
                asset.Depth,
                asset.Description,
                asset.Tags,
                asset.Rating,
                asset.IsIgnored,
                asset.ScanError,
                0,
                0,
                asset.Id,
                asset.Kind))
            .ToListAsync(cancellationToken);

        var archives = activeContainer is null
            ? []
            : ArchiveCardsForContainer(activeContainer, containers, childrenByParent, search, rating, unratedOnly);

        var totals = new LibraryTotals(
            await db.Assets.CountAsync(asset =>
                asset.IsAvailable &&
                !asset.IsIgnored &&
                (asset.Kind == AssetKinds.Image || asset.Kind == AssetKinds.Audio || asset.Kind == AssetKinds.Video), cancellationToken),
            await db.Assets.CountAsync(asset => asset.IsAvailable && asset.Kind == AssetKinds.Zip, cancellationToken),
            await db.Assets.CountAsync(asset => !asset.IsAvailable, cancellationToken),
            await db.Assets.CountAsync(asset => asset.ScanError != null, cancellationToken));

        return new LibrarySnapshot(containers, images, archives, activeContainerId, totals);
    }

    public async Task<List<AssetCard>> GetTabImagesAsync(
        long containerId,
        string search,
        int? rating,
        bool unratedOnly,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var containerRows = await db.Assets
            .AsNoTracking()
            .Where(asset => asset.IsAvailable && (asset.Kind == AssetKinds.Folder || asset.Kind == AssetKinds.Zip))
            .Select(asset => new ContainerRow(
                asset.Id,
                asset.ParentId,
                asset.Name,
                asset.LabelName,
                asset.Kind,
                asset.SourceType,
                asset.RelativePath,
                asset.Depth,
                asset.Description,
                asset.Tags,
                asset.Rating,
                asset.IsIgnored,
                asset.ScanError))
            .ToListAsync(cancellationToken);

        var container = containerRows.SingleOrDefault(container => container.Id == containerId);
        if (container is null)
        {
            return [];
        }

        var childrenByParent = containerRows
            .Where(row => row.ParentId is not null)
            .GroupBy(row => row.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var parentIds = ParentIdsForContainer(container, childrenByParent).ToArray();
        var query = db.Assets
            .AsNoTracking()
            .Where(asset =>
                asset.IsAvailable &&
                !asset.IsIgnored &&
                (asset.Kind == AssetKinds.Image || asset.Kind == AssetKinds.Audio || asset.Kind == AssetKinds.Video));

        query = container.Kind == AssetKinds.Folder
            ? query.Where(asset =>
                asset.ParentId != null &&
                parentIds.Contains(asset.ParentId.Value) &&
                (container.SourceType != AssetSourceTypes.FileSystemFolder ||
                 asset.SourceType == AssetSourceTypes.FileSystemImage ||
                 asset.SourceType == AssetSourceTypes.FileSystemAudio ||
                 asset.SourceType == AssetSourceTypes.FileSystemVideo))
            : query.Where(asset => asset.ParentId != null && parentIds.Contains(asset.ParentId.Value));

        query = ApplyFilters(query, search, rating, unratedOnly);

        return await query
            .OrderBy(asset => asset.SortKey)
            .ThenBy(asset => asset.Id)
            .Take(500)
            .Select(asset => new AssetCard(
                asset.Id,
                asset.ParentId,
                asset.Name,
                asset.LabelName,
                asset.Kind,
                asset.SourceType,
                asset.RelativePath,
                asset.Depth,
                asset.Description,
                asset.Tags,
                asset.Rating,
                asset.IsIgnored,
                asset.ScanError,
                0,
                0,
                asset.Id,
                asset.Kind))
            .ToListAsync(cancellationToken);
    }

    public async Task<AssetCard?> GetAssetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Assets
            .AsNoTracking()
            .Where(asset => asset.Id == id)
            .Select(asset => new AssetCard(
                asset.Id,
                asset.ParentId,
                asset.Name,
                asset.LabelName,
                asset.Kind,
                asset.SourceType,
                asset.RelativePath,
                asset.Depth,
                asset.Description,
                asset.Tags,
                asset.Rating,
                asset.IsIgnored,
                asset.ScanError,
                0,
                0,
                asset.IsImage || asset.IsVideo ? asset.Id : null,
                asset.IsImage || asset.IsVideo ? asset.Kind : null))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveMetadataAsync(long id, string labelName, string description, string tags, int? rating, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Assets.SingleAsync(asset => asset.Id == id, cancellationToken);
        asset.LabelName = labelName.Trim();
        asset.Description = description.Trim();
        asset.Tags = NormalizeTags(tags);
        asset.Rating = rating is >= 1 and <= 5 ? rating : null;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetIgnoredAsync(long id, bool isIgnored, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Assets.SingleAsync(asset => asset.Id == id, cancellationToken);
        asset.IsIgnored = isIgnored;
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        if (asset.Kind == AssetKinds.Zip && isIgnored)
        {
            var descendantPrefix = asset.StableKey + "!";
            await db.Assets
                .Where(descendant => descendant.StableKey.StartsWith(descendantPrefix))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(descendant => descendant.IsAvailable, false)
                    .SetProperty(descendant => descendant.ScanError, (string?)null)
                    .SetProperty(descendant => descendant.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LabelSuggestions> GetLabelSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assets = await db.Assets
            .AsNoTracking()
            .Where(asset => asset.IsAvailable)
            .Select(asset => new
            {
                asset.Kind,
                asset.SourceType,
                asset.Name,
                asset.LabelName,
                asset.RelativePath,
                asset.Description,
                asset.Tags
            })
            .ToListAsync(cancellationToken);

        var labelArchives = assets
            .Where(asset => IsLabelArchive(asset.Kind, asset.SourceType))
            .ToList();

        var tags = assets
            .SelectMany(asset => SplitTags(asset.Tags))
            .Concat(labelArchives.SelectMany(asset => FileNameSuggestionParts(asset.Name)).Select(tag => tag.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag)
            .Take(80)
            .ToList();

        var names = labelArchives
            .SelectMany(asset => new[] { asset.LabelName, CleanArchiveName(asset.Name) }.Concat(FileNameSuggestionParts(asset.Name)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .Take(80)
            .ToList();

        var text = labelArchives
            .Select(asset => asset.Description)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .Take(40)
            .ToList();

        return new LabelSuggestions(tags, names, text);
    }

    private static List<MediaRow> MediaForContainer(
        AssetCard container,
        List<ContainerRow> containers,
        Dictionary<long, List<ContainerRow>> childrenByParent,
        List<MediaRow> media)
    {
        var containerRow = containers.Single(row => row.Id == container.Id);
        var parentIds = ParentIdsForContainer(containerRow, childrenByParent).ToHashSet();

        return container.Kind == AssetKinds.Folder
            ? media
                .Where(item =>
                    parentIds.Contains(item.ParentId) &&
                    (container.SourceType != AssetSourceTypes.FileSystemFolder || IsFileSystemMediaSource(item.SourceType)))
                .ToList()
            : media
                .Where(item => parentIds.Contains(item.ParentId))
                .ToList();
    }

    private static bool IsFileSystemMediaSource(string sourceType)
    {
        return sourceType is AssetSourceTypes.FileSystemImage or AssetSourceTypes.FileSystemAudio or AssetSourceTypes.FileSystemVideo;
    }

    private static List<AssetCard> ArchiveCardsForContainer(
        ContainerRow container,
        List<AssetCard> containers,
        Dictionary<long, List<ContainerRow>> childrenByParent,
        string search,
        int? rating,
        bool unratedOnly)
    {
        var parentIds = ParentIdsForContainer(container, childrenByParent).ToHashSet();
        return containers
            .Where(asset =>
                asset.Kind == AssetKinds.Zip &&
                asset.Id != container.Id &&
                asset.ParentId is not null &&
                parentIds.Contains(asset.ParentId.Value))
            .Where(asset => MatchesFilters(asset, search, rating, unratedOnly))
            .OrderBy(asset => NaturalSortKey.Build(asset.RelativePath))
            .ThenBy(asset => asset.Id)
            .Take(120)
            .ToList();
    }

    private static IEnumerable<long> ParentIdsForContainer(
        ContainerRow container,
        Dictionary<long, List<ContainerRow>> childrenByParent)
    {
        yield return container.Id;

        if (!childrenByParent.TryGetValue(container.Id, out var children))
        {
            yield break;
        }

        var childKinds = container.Kind == AssetKinds.Folder && container.SourceType == AssetSourceTypes.FileSystemFolder
            ? [AssetKinds.Folder]
            : new[] { AssetKinds.Folder, AssetKinds.Zip };

        foreach (var child in children.Where(child => childKinds.Contains(child.Kind) && !child.IsIgnored))
        {
            foreach (var childId in ParentIdsForContainer(child, childrenByParent))
            {
                yield return childId;
            }
        }
    }

    private static IQueryable<LibraryAsset> ApplyFilters(IQueryable<LibraryAsset> query, string search, int? rating, bool unratedOnly)
    {
        foreach (var token in SearchTokens(search))
        {
            var pattern = $"%{EscapeLikePattern(token)}%";
            query = query.Where(asset =>
                EF.Functions.Like(asset.Name, pattern, "\\") ||
                EF.Functions.Like(asset.LabelName, pattern, "\\") ||
                EF.Functions.Like(asset.RelativePath, pattern, "\\") ||
                EF.Functions.Like(asset.Tags, pattern, "\\") ||
                EF.Functions.Like(asset.Description, pattern, "\\"));
        }

        if (rating is >= 1 and <= 5)
        {
            query = query.Where(asset => asset.Rating >= rating);
        }

        if (unratedOnly)
        {
            query = query.Where(asset => asset.Rating == null);
        }

        return query;
    }

    private static bool MatchesFilters(AssetCard asset, string search, int? rating, bool unratedOnly)
    {
        if (!SearchTokens(search).All(token => MatchesSearchToken(asset, token)))
        {
            return false;
        }

        if (rating is >= 1 and <= 5 && asset.Rating < rating)
        {
            return false;
        }

        return !unratedOnly || asset.Rating is null;
    }

    private static IEnumerable<string> SearchTokens(string search)
    {
        return search
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => !string.IsNullOrWhiteSpace(token));
    }

    private static bool MatchesSearchToken(AssetCard asset, string token)
    {
        return asset.Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            asset.LabelName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            asset.RelativePath.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            asset.Tags.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            asset.Description.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    private static string NormalizeTags(string tags)
    {
        return string.Join(", ",
            SplitTags(tags)
                .Select(tag => tag.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SplitTags(string tags)
    {
        return tags.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag));
    }

    private static string CleanArchiveName(string value)
    {
        return Path.GetFileNameWithoutExtension(value)
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
    }

    private static IEnumerable<string> FileNameSuggestionParts(string fileName)
    {
        return Path.GetFileNameWithoutExtension(fileName)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Replace('_', ' ').Trim())
            .Where(part => part.Length > 2 && !part.All(char.IsDigit));
    }

    private static bool IsLabelArchive(string kind, string sourceType)
    {
        return kind == AssetKinds.Zip &&
            sourceType is AssetSourceTypes.ZipArchivePreview or AssetSourceTypes.ZipArchive;
    }

    private sealed record ContainerRow(
        long Id,
        long? ParentId,
        string Name,
        string LabelName,
        string Kind,
        string SourceType,
        string RelativePath,
        int Depth,
        string Description,
        string Tags,
        int? Rating,
        bool IsIgnored,
        string? ScanError);

    private sealed record MediaRow(long Id, long ParentId, string Kind, string SourceType);
}

public sealed record LibrarySnapshot(
    List<AssetCard> Containers,
    List<AssetCard> Images,
    List<AssetCard> Archives,
    long? ActiveContainerId,
    LibraryTotals Totals);

public sealed record AssetCard(
    long Id,
    long? ParentId,
    string Name,
    string LabelName,
    string Kind,
    string SourceType,
    string RelativePath,
    int Depth,
    string Description,
    string Tags,
    int? Rating,
    bool IsIgnored,
    string? ScanError,
    int ImageCount,
    int ChildCount,
    long? PreviewImageId,
    string? PreviewKind)
{
    public bool IsImage => Kind == AssetKinds.Image;

    public bool IsAudio => Kind == AssetKinds.Audio;

    public bool IsVideo => Kind == AssetKinds.Video;

    public bool IsMedia => IsImage || IsAudio || IsVideo;

    public string DisplayName => string.IsNullOrWhiteSpace(LabelName) ? Name : LabelName;
}

public sealed record LibraryTotals(int Images, int Archives, int Missing, int Errors);

public sealed record LabelSuggestions(
    List<string> Tags,
    List<string> Names,
    List<string> Text);
