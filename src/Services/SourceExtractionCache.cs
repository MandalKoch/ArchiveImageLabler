using System.Collections.Concurrent;
using ArchiveImageLabler.Data;
using ArchiveImageLabler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArchiveImageLabler.Services;

public sealed class SourceExtractionCache(
    IDbContextFactory<LibraryDbContext> dbFactory,
    ImageContentService imageContentService,
    IOptions<LibraryOptions> options,
    ILogger<SourceExtractionCache> logger)
{
    private readonly ConcurrentDictionary<string, SourceExtractionSession> _sessions = new(StringComparer.Ordinal);
    private readonly string _rootPath = Path.Combine(options.Value.DataPath, "cache", "source-pages");

    public async Task<SourceExtractionSessionSummary> ExtractAsync(
        long containerId,
        IReadOnlyCollection<long> mediaIds,
        IProgress<SourceExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootPath);

        var sessionId = Guid.NewGuid().ToString("N");
        var sessionPath = Path.Combine(_rootPath, sessionId);
        Directory.CreateDirectory(sessionPath);

        var files = new ConcurrentDictionary<long, SourceExtractedFile>();
        var session = new SourceExtractionSession(sessionId, sessionPath, files);
        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new InvalidOperationException("Unable to allocate extraction session.");
        }

        progress?.Report(new SourceExtractionProgress(sessionId, "Preparing extraction", string.Empty, 0, mediaIds.Count, null));

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var mediaAssets = await db.Assets
                .AsNoTracking()
                .Where(asset =>
                    mediaIds.Contains(asset.Id) &&
                    asset.IsAvailable &&
                    !asset.IsIgnored &&
                    (asset.Kind == AssetKinds.Image || asset.Kind == AssetKinds.Audio || asset.Kind == AssetKinds.Video))
                .ToListAsync(cancellationToken);
            var mediaOrder = mediaIds
                .Select((id, index) => new { id, index })
                .ToDictionary(item => item.id, item => item.index);
            mediaAssets = mediaAssets
                .OrderBy(asset => mediaOrder.GetValueOrDefault(asset.Id, int.MaxValue))
                .ThenBy(asset => asset.Id)
                .ToList();

            var parentById = await db.Assets
                .AsNoTracking()
                .Where(asset =>
                    asset.IsAvailable &&
                    (asset.Kind == AssetKinds.Folder || asset.Kind == AssetKinds.Zip))
                .Select(asset => new { asset.Id, asset.ParentId })
                .ToDictionaryAsync(asset => asset.Id, asset => asset.ParentId, cancellationToken);

            var extracted = 0;
            foreach (var asset in mediaAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDescendantOf(asset.ParentId, containerId, parentById))
                {
                    continue;
                }

                progress?.Report(new SourceExtractionProgress(sessionId, "Extracting media", asset.RelativePath, extracted, mediaAssets.Count, null));
                await ExtractAssetAsync(asset, sessionPath, files, cancellationToken);
                extracted++;
                progress?.Report(new SourceExtractionProgress(sessionId, "Extracted media", asset.RelativePath, extracted, mediaAssets.Count, asset.Id));
            }

            progress?.Report(new SourceExtractionProgress(sessionId, "Extraction complete", string.Empty, extracted, mediaAssets.Count, null));
            return new SourceExtractionSessionSummary(sessionId, files.Count);
        }
        catch
        {
            await DeleteSessionAsync(sessionId);
            throw;
        }
    }

    public SourceExtractedFile? GetFile(string sessionId, long assetId)
    {
        return _sessions.TryGetValue(sessionId, out var session) &&
            session.Files.TryGetValue(assetId, out var file)
            ? file
            : null;
    }

    public async Task DeleteSessionAsync(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(session.Path))
                {
                    Directory.Delete(session.Path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Unable to delete source extraction session {SessionId}", sessionId);
            }
        });
    }

    private async Task ExtractAssetAsync(
        LibraryAsset asset,
        string sessionPath,
        ConcurrentDictionary<long, SourceExtractedFile> files,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(asset.Name);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bin";
        }

        var outputPath = Path.Combine(sessionPath, $"{asset.Id}{extension}");
        var content = await imageContentService.OpenAsync(asset, cancellationToken);
        await using (content.Stream)
        await using (var output = File.Create(outputPath))
        {
            await content.Stream.CopyToAsync(output, cancellationToken);
        }

        files[asset.Id] = new SourceExtractedFile(outputPath, content.ContentType);
    }

    private static bool IsDescendantOf(long? parentId, long containerId, IReadOnlyDictionary<long, long?> parentById)
    {
        while (parentId is long currentParentId)
        {
            if (currentParentId == containerId)
            {
                return true;
            }

            if (!parentById.TryGetValue(currentParentId, out parentId))
            {
                return false;
            }
        }

        return false;
    }

    private sealed record SourceExtractionSession(
        string Id,
        string Path,
        ConcurrentDictionary<long, SourceExtractedFile> Files);
}

public sealed record SourceExtractionSessionSummary(string SessionId, int FileCount);

public sealed record SourceExtractionProgress(
    string SessionId,
    string Phase,
    string CurrentItem,
    int Extracted,
    int Total,
    long? AssetId);

public sealed record SourceExtractedFile(string Path, string ContentType);
