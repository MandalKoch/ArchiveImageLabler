using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Threading.Channels;
using ArchiveImageLabler.Data;
using ArchiveImageLabler.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace ArchiveImageLabler.Services;

public sealed class LibraryScanner(
    IDbContextFactory<LibraryDbContext> dbFactory,
    IOptions<LibraryOptions> options,
    ILogger<LibraryScanner> logger)
{
    private readonly LibraryOptions _options = options.Value;

    public Task<int> CountRootArchivesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CountRootArchives(cancellationToken), cancellationToken);
    }

    public async Task<ScanSummary> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summary = await ScanRootAsync(progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("Pruning missing entries", _options.RootPath, summary.Images, summary.Containers, summary.Errors));
        var pruneSummary = await PruneUnavailableAsync(cancellationToken);
        summary.Pruned = pruneSummary.Pruned;
        progress?.Report(new ScanProgress("Scan complete", _options.RootPath, summary.Images, summary.Containers, summary.Errors));
        return summary;
    }

    public async Task<ScanSummary> RescanAsync(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.DataPath);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var assetsToDelete = await db.Assets.CountAsync(cancellationToken);
        progress?.Report(new ScanProgress("Deleting indexed assets", _options.RootPath, 0, 0, 0, 0, assetsToDelete, "assets"));
        var removed = await DeleteAllAssetsAsync(db, MutationBatchSize, progress, _options.RootPath, assetsToDelete, cancellationToken);

        var summary = await ScanRootAsync(progress, cancellationToken);
        summary.Pruned = removed;
        return summary;
    }

    public async Task<ScanSummary> PruneUnavailableAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        return new ScanSummary
        {
            Pruned = await PruneUnavailableAsync(db, MutationBatchSize, cancellationToken)
        };
    }

    private int CountRootArchives(CancellationToken cancellationToken)
    {
        var rootPath = Path.GetFullPath(_options.RootPath);
        if (!Directory.Exists(rootPath))
        {
            return 0;
        }

        var archiveCount = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryPath = pending.Pop();

            foreach (var childDirectory in EnumerateDirectoriesForPreflight(directoryPath))
            {
                pending.Push(childDirectory);
            }

            foreach (var filePath in EnumerateFilesForPreflight(directoryPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ImageFormat.IsArchive(filePath))
                {
                    archiveCount++;
                }
            }
        }

        return archiveCount;
    }

    private IEnumerable<string> EnumerateDirectoriesForPreflight(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to enumerate directory {DirectoryPath} while counting archives", directoryPath);
            return [];
        }
    }

    private IEnumerable<string> EnumerateFilesForPreflight(string directoryPath)
    {
        try
        {
            return Directory.EnumerateFiles(directoryPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Unable to enumerate files in {DirectoryPath} while counting archives", directoryPath);
            return [];
        }
    }

    private async Task<ScanSummary> ScanRootAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.DataPath);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var assets = await db.Assets.ToDictionaryAsync(asset => asset.StableKey, cancellationToken);
        MarkRootScanTargetsUnavailable(assets.Values);

        var summary = new ScanSummary();
        var rootPath = Path.GetFullPath(_options.RootPath);
        if (!Directory.Exists(rootPath))
        {
            var rootMissing = Upsert(
                db,
                assets,
                stableKey: $"folder:{rootPath}",
                parent: null,
                name: Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                kind: AssetKinds.Folder,
                sourceType: AssetSourceTypes.FileSystemFolder,
                sourcePath: rootPath,
                entryChain: null,
                relativePath: string.Empty,
                contentType: null,
                sizeBytes: null,
                modifiedAt: null,
                depth: 0);

            rootMissing.IsAvailable = false;
            rootMissing.ScanError = $"Mounted library path not found: {rootPath}";
            await db.SaveChangesAsync(cancellationToken);
            summary.Errors = 1;
            return summary;
        }

        var root = Upsert(
            db,
            assets,
            stableKey: $"folder:{rootPath}",
            parent: null,
            name: string.IsNullOrWhiteSpace(Path.GetFileName(rootPath)) ? rootPath : Path.GetFileName(rootPath),
            kind: AssetKinds.Folder,
            sourceType: AssetSourceTypes.FileSystemFolder,
            sourcePath: rootPath,
            entryChain: null,
            relativePath: string.Empty,
            contentType: null,
            sizeBytes: null,
            modifiedAt: Directory.GetLastWriteTimeUtc(rootPath),
            depth: 0);

        progress?.Report(new ScanProgress("Scanning folders", rootPath, summary.Images, summary.Containers, summary.Errors));
        var discovery = await DiscoverFileSystemAssetsAsync(rootPath, progress, cancellationToken);
        progress?.Report(new ScanProgress(
            "Preparing archive previews",
            $"{discovery.ArchiveFiles.Count} archives found",
            summary.Images,
            summary.Containers,
            summary.Errors,
            0,
            discovery.ArchiveFiles.Count,
            "archives"));
        await ApplyFileSystemDiscoveryAsync(db, assets, root, discovery, summary, progress, cancellationToken);
        MarkMissingArchiveDescendantsUnavailable(assets.Values);

        await db.SaveChangesAsync(cancellationToken);
        progress?.Report(new ScanProgress("Fast scan complete", rootPath, summary.Images, summary.Containers, summary.Errors));
        return summary;
    }

    public async Task<ScanSummary> ScanContainerAsync(long containerId, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ScanContainerAsync(containerId, pruneUnavailable: false, progress, cancellationToken);
    }

    public async Task<ScanSummary> RescanContainerAndPruneAsync(long containerId, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return await ScanContainerAsync(containerId, pruneUnavailable: true, progress, cancellationToken);
    }

    private async Task<ScanSummary> ScanContainerAsync(long containerId, bool pruneUnavailable, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var parent = await db.Assets.SingleOrDefaultAsync(asset => asset.Id == containerId, cancellationToken);
        if (parent is null || parent.Kind != AssetKinds.Zip || parent.SourcePath is null)
        {
            return new ScanSummary();
        }

        parent.SourceType = AssetSourceTypes.ZipArchive;
        parent.ScanError = null;

        var assets = await db.Assets.ToDictionaryAsync(asset => asset.StableKey, cancellationToken);

        if (parent.IsIgnored)
        {
            await db.SaveChangesAsync(cancellationToken);
            progress?.Report(new ScanProgress("Archive ignored", parent.RelativePath, 0, 0, 0));
            return new ScanSummary();
        }

        MarkArchiveDescendantsUnavailable(assets.Values, parent.StableKey);

        var summary = new ScanSummary();
        var entryChain = string.IsNullOrWhiteSpace(parent.EntryChain)
            ? []
            : parent.EntryChain.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        progress?.Report(new ScanProgress("Scanning archive", parent.RelativePath, 0, 0, 0));
        await ScanZipFileAsync(db, assets, parent, parent.SourcePath, entryChain, parent.Depth + 1, summary, progress, cancellationToken);
        MarkMissingArchiveDescendantsUnavailable(assets.Values);

        if (pruneUnavailable)
        {
            progress?.Report(new ScanProgress("Pruning missing archive entries", parent.RelativePath, summary.Images, summary.Containers, summary.Errors));
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            summary.Pruned = await PruneUnavailableAsync(db, MutationBatchSize, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        progress?.Report(new ScanProgress("Archive scan complete", parent.RelativePath, summary.Images, summary.Containers, summary.Errors));
        return summary;
    }

    private async Task<bool> ScanZipPreviewAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string archiveDisplayPath,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var zipStream = OpenArchiveStream(zipPath, entryChain);
            using var archive = ArchiveFactory.OpenArchive(zipStream, new ReaderOptions());
            return await ScanZipPreviewEntriesAsync(db, assets, parent, zipPath, entryChain, archiveDisplayPath, archive, depth, summary, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SharpCompressException)
        {
            parent.ScanError = ex.Message;
            summary.Errors++;
            logger.LogWarning(ex, "Unable to scan archive preview {ZipPath}", zipPath);
            return false;
        }
    }

    private async Task<bool> ScanZipPreviewEntriesAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string archiveDisplayPath,
        IArchive archive,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var entryName = LastArchivePathSegment(entry.Key);
            var nextChain = entryChain.Concat([entry.Key]).ToArray();
            var relativePath = $"{archiveDisplayPath}!{entry.Key}";

            if (ImageFormat.IsMedia(entryName))
            {
                var mediaKind = ImageFormat.MediaKindFor(entryName);
                var stableEntryPath = string.Join("!", nextChain);
                Upsert(
                    db,
                    assets,
                    stableKey: $"{parent.StableKey}!preview:{stableEntryPath}",
                    parent: parent,
                    name: BuildArchiveMediaDisplayName(nextChain, entryName),
                    kind: mediaKind,
                    sourceType: ArchiveMediaSourceType(mediaKind, entryChain.Count > 0),
                    sourcePath: zipPath,
                    entryChain: string.Join('\n', nextChain),
                    relativePath: relativePath,
                    contentType: ImageFormat.ContentTypeFor(entryName),
                    sizeBytes: entry.Size,
                    modifiedAt: ArchiveModifiedAt(entry),
                    depth: depth);

                summary.Images++;
                progress?.Report(new ScanProgress("Found archive preview", relativePath, summary.Images, summary.Containers, summary.Errors));
                return true;
            }

            if (ImageFormat.IsArchive(entryName) && depth < _options.MaxNestedZipDepth)
            {
                if (IsArchiveEntryIgnored(assets, parent.StableKey, nextChain))
                {
                    progress?.Report(new ScanProgress("Skipped ignored nested archive", relativePath, summary.Images, summary.Containers, summary.Errors));
                    continue;
                }

                var found = await ScanNestedZipPreviewEntryAsync(db, assets, parent, zipPath, nextChain, relativePath, entry, depth + 1, summary, progress, cancellationToken);
                if (found)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> ScanNestedZipPreviewEntryAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string archiveDisplayPath,
        IArchiveEntry zipEntry,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (zipEntry.Size > _options.MaxNestedZipBytesInMemory)
            {
                summary.Errors++;
                logger.LogWarning(
                    "Skipped nested archive preview {ArchivePath} because it is {NestedZipBytes} bytes and the configured in-memory limit is {MaxNestedZipBytesInMemory} bytes.",
                    archiveDisplayPath,
                    zipEntry.Size,
                    _options.MaxNestedZipBytesInMemory);
                return false;
            }

            using var nestedZipStream = new MemoryStream();
            await using (var entryStream = await zipEntry.OpenEntryStreamAsync(cancellationToken))
            {
                await entryStream.CopyToAsync(nestedZipStream, cancellationToken);
            }

            nestedZipStream.Position = 0;
            using var nestedArchive = ArchiveFactory.OpenArchive(nestedZipStream, new ReaderOptions());
            return await ScanZipPreviewEntriesAsync(db, assets, parent, zipPath, entryChain, archiveDisplayPath, nestedArchive, depth, summary, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SharpCompressException)
        {
            logger.LogWarning(ex, "Unable to scan nested archive preview {ArchivePath}", archiveDisplayPath);
            return false;
        }
    }

    private async Task<FileSystemDiscovery> DiscoverFileSystemAssetsAsync(
        string rootPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var discovery = new FileSystemDiscovery();
        var targets = Channel.CreateUnbounded<DirectoryScanTarget>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        var pendingTargets = 1;
        await targets.Writer.WriteAsync(new DirectoryScanTarget($"folder:{rootPath}", rootPath, 0), cancellationToken);

        var workers = Enumerable.Range(0, ScanParallelism)
            .Select(_ => Task.Run(ProcessDirectoriesAsync, cancellationToken))
            .ToArray();

        await Task.WhenAll(workers);
        return discovery;

        async Task ProcessDirectoriesAsync()
        {
            await foreach (var target in targets.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    progress?.Report(new ScanProgress("Discovering folders", target.DirectoryPath, 0, 0, 0));
                    await DiscoverDirectoryAsync(target);
                }
                finally
                {
                    if (Interlocked.Decrement(ref pendingTargets) == 0)
                    {
                        targets.Writer.TryComplete();
                    }
                }
            }
        }

        async Task DiscoverDirectoryAsync(DirectoryScanTarget target)
        {
            string[] childDirectories;
            string[] files;

            try
            {
                childDirectories = Directory.GetDirectories(target.DirectoryPath);
                files = Directory.GetFiles(target.DirectoryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                discovery.DirectoryErrors.Add(new DirectoryScanError(target.StableKey, ex.Message));
                return;
            }

            foreach (var childDirectory in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(childDirectory);
                if (string.Equals(directory.Name, ".archiveimagelabler", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stableKey = $"folder:{directory.FullName}";
                discovery.Folders.Add(new DiscoveredFolder(
                    stableKey,
                    target.StableKey,
                    directory.Name,
                    directory.FullName,
                    Path.GetRelativePath(rootPath, directory.FullName),
                    directory.LastWriteTimeUtc,
                    target.Depth + 1));

                Interlocked.Increment(ref pendingTargets);
                await targets.Writer.WriteAsync(new DirectoryScanTarget(stableKey, directory.FullName, target.Depth + 1), cancellationToken);
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DiscoverFileAsync(target, filePath);
            }
        }

        async Task DiscoverFileAsync(DirectoryScanTarget parent, string filePath)
        {
            FileInfo file;
            try
            {
                file = new FileInfo(filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                discovery.FileErrors.Add(new FileScanError(filePath, Path.GetRelativePath(rootPath, filePath), "Unable to inspect file"));
                logger.LogWarning(ex, "Unable to inspect file {FilePath}", filePath);
                return;
            }

            if (ImageFormat.IsMedia(filePath))
            {
                try
                {
                    var mediaKind = ImageFormat.MediaKindFor(file.Name);
                    discovery.MediaFiles.Add(new DiscoveredMediaFile(
                        parent.StableKey,
                        file.Name,
                        mediaKind,
                        file.FullName,
                        Path.GetRelativePath(rootPath, file.FullName),
                        ImageFormat.ContentTypeFor(file.Name),
                        file.Length,
                        file.LastWriteTimeUtc,
                        parent.Depth + 1));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    discovery.FileErrors.Add(new FileScanError(file.FullName, Path.GetRelativePath(rootPath, file.FullName), "Unable to read media metadata"));
                    logger.LogWarning(ex, "Unable to read media metadata {FilePath}", file.FullName);
                }

                return;
            }

            if (!ImageFormat.IsArchive(filePath))
            {
                return;
            }

            var relativePath = Path.GetRelativePath(rootPath, filePath);
            progress?.Report(new ScanProgress("Queued archive", relativePath, 0, 0, 0));

            try
            {
                discovery.ArchiveFiles.Add(new DiscoveredArchiveFile(
                    parent.StableKey,
                    file.Name,
                    file.FullName,
                    relativePath,
                    file.Length,
                    file.LastWriteTimeUtc,
                    parent.Depth + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                discovery.FileErrors.Add(new FileScanError(file.FullName, relativePath, "Unable to inspect archive metadata"));
                logger.LogWarning(ex, "Unable to inspect archive metadata {ArchivePath}", file.FullName);
            }
        }
    }

    private async Task ApplyFileSystemDiscoveryAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        FileSystemDiscovery discovery,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var foldersByKey = new Dictionary<string, LibraryAsset>(StringComparer.Ordinal)
        {
            [parent.StableKey] = parent
        };

        foreach (var folder in discovery.Folders
            .OrderBy(folder => folder.Depth)
            .ThenBy(folder => folder.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!foldersByKey.TryGetValue(folder.ParentStableKey, out var folderParent))
            {
                continue;
            }

            var child = Upsert(
                db,
                assets,
                stableKey: folder.StableKey,
                parent: folderParent,
                name: folder.Name,
                kind: AssetKinds.Folder,
                sourceType: AssetSourceTypes.FileSystemFolder,
                sourcePath: folder.FullPath,
                entryChain: null,
                relativePath: folder.RelativePath,
                contentType: null,
                sizeBytes: null,
                modifiedAt: folder.ModifiedAt,
                depth: folder.Depth);

            foldersByKey[folder.StableKey] = child;
            summary.Containers++;
            progress?.Report(new ScanProgress("Scanning folders", child.RelativePath, summary.Images, summary.Containers, summary.Errors));
            await FlushScanBatchAsync(db, summary, SaveBatchSize, cancellationToken);
        }

        foreach (var error in discovery.DirectoryErrors.OrderBy(error => error.StableKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (foldersByKey.TryGetValue(error.StableKey, out var folder))
            {
                folder.ScanError = error.Message;
            }

            summary.Errors++;
        }

        foreach (var error in discovery.FileErrors.OrderBy(error => error.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.Errors++;
            progress?.Report(new ScanProgress(error.ProgressPhase, error.RelativePath, summary.Images, summary.Containers, summary.Errors));
        }

        foreach (var mediaFile in discovery.MediaFiles.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!foldersByKey.TryGetValue(mediaFile.ParentStableKey, out var mediaParent))
            {
                continue;
            }

            Upsert(
                db,
                assets,
                stableKey: $"file:{mediaFile.FullPath}",
                parent: mediaParent,
                name: mediaFile.Name,
                kind: mediaFile.Kind,
                sourceType: FileSystemMediaSourceType(mediaFile.Kind),
                sourcePath: mediaFile.FullPath,
                entryChain: null,
                relativePath: mediaFile.RelativePath,
                contentType: mediaFile.ContentType,
                sizeBytes: mediaFile.SizeBytes,
                modifiedAt: mediaFile.ModifiedAt,
                depth: mediaFile.Depth);

            summary.Images++;
            progress?.Report(new ScanProgress("Scanning loose media", mediaFile.RelativePath, summary.Images, summary.Containers, summary.Errors));
            await FlushScanBatchAsync(db, summary, SaveBatchSize, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        var archiveFiles = discovery.ArchiveFiles
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
        var archiveGroupsById = assets.Values
            .Where(asset => asset.SourceType == AssetSourceTypes.ArchiveGroup)
            .ToDictionary(asset => asset.Id);
        var unchangedArchiveKeysByPath = assets.Values
            .Where(asset =>
                asset.Kind == AssetKinds.Zip &&
                string.IsNullOrWhiteSpace(asset.EntryChain) &&
                !string.IsNullOrWhiteSpace(asset.SourcePath) &&
                asset.SizeBytes is not null &&
                asset.ModifiedAt is not null)
            .GroupBy(asset => asset.SourcePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(asset => asset.UpdatedAt).First(),
                StringComparer.OrdinalIgnoreCase);
        var scannedArchives = 0;
        var archiveTotal = archiveFiles.Count;
        var fingerprintedArchives = await FingerprintArchivesAsync(archiveFiles, unchangedArchiveKeysByPath, summary, progress, cancellationToken);

        foreach (var fingerprintedArchive in fingerprintedArchives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var archiveFile = fingerprintedArchive.Archive;
            if (!foldersByKey.TryGetValue(archiveFile.ParentStableKey, out var archiveParent))
            {
                scannedArchives++;
                progress?.Report(new ScanProgress(
                    "Skipped archive",
                    archiveFile.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    scannedArchives,
                    archiveTotal,
                    "archives"));
                continue;
            }

            if (fingerprintedArchive.Error is not null || fingerprintedArchive.StableKey is null)
            {
                summary.Errors++;
                scannedArchives++;
                progress?.Report(new ScanProgress(
                    "Unable to read archive",
                    archiveFile.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    scannedArchives,
                    archiveTotal,
                    "archives"));
                continue;
            }

            var stableKey = fingerprintedArchive.StableKey;
            if (assets.TryGetValue(stableKey, out var existingZipAsset) && existingZipAsset.IsAvailable)
            {
                scannedArchives++;
                progress?.Report(new ScanProgress(
                    "Skipped duplicate archive",
                    archiveFile.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    scannedArchives,
                    archiveTotal,
                    "archives"));
                continue;
            }

            assets.TryGetValue(stableKey, out var previousArchiveAsset);
            var previousArchiveSourcePath = previousArchiveAsset?.SourcePath;
            var previousArchiveRelativePath = previousArchiveAsset?.RelativePath;
            var archiveParentForUpsert = archiveParent;
            var archiveDepth = archiveFile.Depth;
            if (previousArchiveAsset?.ParentId is long previousParentId &&
                archiveGroupsById.TryGetValue(previousParentId, out var archiveGroup) &&
                archiveGroup.IsAvailable)
            {
                archiveParentForUpsert = archiveGroup;
                archiveDepth = archiveGroup.Depth + 1;
            }

            var zipAsset = Upsert(
                db,
                assets,
                stableKey: stableKey,
                parent: archiveParentForUpsert,
                name: archiveFile.Name,
                kind: AssetKinds.Zip,
                sourceType: AssetSourceTypes.ZipArchivePreview,
                sourcePath: archiveFile.FullPath,
                entryChain: null,
                relativePath: archiveFile.RelativePath,
                contentType: null,
                sizeBytes: archiveFile.SizeBytes,
                modifiedAt: archiveFile.ModifiedAt,
                depth: archiveDepth);

            summary.Containers++;
            if (!string.Equals(previousArchiveSourcePath, zipAsset.SourcePath, StringComparison.Ordinal) ||
                !string.Equals(previousArchiveRelativePath, zipAsset.RelativePath, StringComparison.Ordinal))
            {
                UpdateArchiveDescendantPaths(assets.Values, zipAsset.StableKey, zipAsset.SourcePath, previousArchiveRelativePath, zipAsset.RelativePath);
            }

            await db.SaveChangesAsync(cancellationToken);
            progress?.Report(new ScanProgress(
                "Found archive",
                zipAsset.RelativePath,
                summary.Images,
                summary.Containers,
                summary.Errors,
                scannedArchives + 1,
                archiveTotal,
                "archives"));
            if (zipAsset.IsIgnored)
            {
                progress?.Report(new ScanProgress(
                    "Skipped ignored archive",
                    zipAsset.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    scannedArchives,
                    archiveTotal,
                    "archives"));
            }
            else if (ArchiveHasAvailablePreview(assets.Values, zipAsset.StableKey))
            {
                progress?.Report(new ScanProgress(
                    "Reused archive preview",
                    zipAsset.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    scannedArchives,
                    archiveTotal,
                    "archives"));
            }
            else
            {
                var archiveProgress = progress is null
                    ? null
                    : new Progress<ScanProgress>(value => progress.Report(value with
                    {
                        WorkDone = scannedArchives,
                        WorkTotal = archiveTotal,
                        WorkLabel = "archives"
                    }));
                await ScanZipPreviewAsync(db, assets, zipAsset, archiveFile.FullPath, [], zipAsset.RelativePath, 1, summary, archiveProgress, cancellationToken);
            }

            scannedArchives++;
            progress?.Report(new ScanProgress(
                "Archive quick scan complete",
                zipAsset.RelativePath,
                summary.Images,
                summary.Containers,
                summary.Errors,
                scannedArchives,
                archiveTotal,
                "archives"));
            await FlushScanBatchAsync(db, summary, SaveBatchSize, cancellationToken);
        }
    }

    private async Task<List<FingerprintedArchiveFile>> FingerprintArchivesAsync(
        IReadOnlyList<DiscoveredArchiveFile> archiveFiles,
        IReadOnlyDictionary<string, LibraryAsset> unchangedArchiveKeysByPath,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (archiveFiles.Count == 0)
        {
            return [];
        }

        var fingerprintedArchives = new ConcurrentBag<FingerprintedArchiveFile>();
        var completed = 0;
        await Parallel.ForEachAsync(
            archiveFiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ScanParallelism,
                CancellationToken = cancellationToken
            },
            async (archiveFile, token) =>
            {
                if (TryGetUnchangedArchiveStableKey(archiveFile, unchangedArchiveKeysByPath, out var cachedStableKey))
                {
                    fingerprintedArchives.Add(new FingerprintedArchiveFile(archiveFile, cachedStableKey, null, IsCached: true));
                    var cachedDone = Interlocked.Increment(ref completed);
                    progress?.Report(new ScanProgress(
                        "Reused archive hash",
                        archiveFile.RelativePath,
                        summary.Images,
                        summary.Containers,
                        summary.Errors,
                        cachedDone,
                        archiveFiles.Count,
                        "archives"));
                    return;
                }

                progress?.Report(new ScanProgress(
                    "Fingerprinting archive",
                    archiveFile.RelativePath,
                    summary.Images,
                    summary.Containers,
                    summary.Errors,
                    completed,
                    archiveFiles.Count,
                    "archives"));

                try
                {
                    var stableKey = await CreateArchiveStableKeyAsync(new FileInfo(archiveFile.FullPath), token);
                    fingerprintedArchives.Add(new FingerprintedArchiveFile(archiveFile, stableKey, null, IsCached: false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Unable to fingerprint archive {ArchivePath}", archiveFile.FullPath);
                    fingerprintedArchives.Add(new FingerprintedArchiveFile(archiveFile, null, ex, IsCached: false));
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new ScanProgress(
                        "Fingerprinted archive",
                        archiveFile.RelativePath,
                        summary.Images,
                        summary.Containers,
                        summary.Errors,
                        done,
                        archiveFiles.Count,
                        "archives"));
                }
            });

        return fingerprintedArchives
            .OrderBy(file => file.Archive.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryGetUnchangedArchiveStableKey(
        DiscoveredArchiveFile archiveFile,
        IReadOnlyDictionary<string, LibraryAsset> unchangedArchiveKeysByPath,
        [NotNullWhen(true)] out string? stableKey)
    {
        stableKey = null;
        if (!unchangedArchiveKeysByPath.TryGetValue(archiveFile.FullPath, out var existingArchive) ||
            existingArchive.SizeBytes != archiveFile.SizeBytes ||
            existingArchive.ModifiedAt != archiveFile.ModifiedAt)
        {
            return false;
        }

        stableKey = existingArchive.StableKey;
        return true;
    }

    private async Task ScanZipFileAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var zipStream = OpenArchiveStream(zipPath, entryChain);
            using var archive = ArchiveFactory.OpenArchive(zipStream, new ReaderOptions());
            await ScanZipEntriesAsync(db, assets, parent, zipPath, entryChain, parent.RelativePath, archive, depth, summary, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SharpCompressException)
        {
            parent.ScanError = ex.Message;
            summary.Errors++;
            logger.LogWarning(ex, "Unable to scan archive {ZipPath}", zipPath);
        }
    }

    private async Task ScanZipEntriesAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string archiveDisplayPath,
        IArchive archive,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var entryName = LastArchivePathSegment(entry.Key);
            var nextChain = entryChain.Concat([entry.Key]).ToArray();
            var relativePath = $"{archiveDisplayPath}!{entry.Key}";
            var entryParent = UpsertZipFolderParents(db, assets, parent, zipPath, archiveDisplayPath, entry.Key, depth);

            if (ImageFormat.IsMedia(entryName))
            {
                var mediaKind = ImageFormat.MediaKindFor(entryName);
                Upsert(
                    db,
                    assets,
                    stableKey: $"{entryParent.StableKey}!{entryName}",
                    parent: entryParent,
                    name: BuildArchiveMediaDisplayName(nextChain, entryName),
                    kind: mediaKind,
                    sourceType: ArchiveMediaSourceType(mediaKind, entryChain.Count > 0),
                    sourcePath: zipPath,
                    entryChain: string.Join('\n', nextChain),
                    relativePath: relativePath,
                    contentType: ImageFormat.ContentTypeFor(entryName),
                    sizeBytes: entry.Size,
                    modifiedAt: ArchiveModifiedAt(entry),
                    depth: depth);

                summary.Images++;
                await FlushScanBatchAsync(db, summary, SaveBatchSize, cancellationToken);
                progress?.Report(new ScanProgress("Scanning archive", relativePath, summary.Images, summary.Containers, summary.Errors));
                continue;
            }

            if (ImageFormat.IsArchive(entryName))
            {
                if (depth >= _options.MaxNestedZipDepth)
                {
                    logger.LogInformation(
                        "Skipped nested archive {ArchivePath} because the configured depth limit is {MaxNestedZipDepth}.",
                        relativePath,
                        _options.MaxNestedZipDepth);
                    continue;
                }

                var nestedZip = UpsertNestedZip(db, assets, entryParent, zipPath, nextChain, relativePath, entry, depth);
                summary.Containers++;
                await FlushScanBatchAsync(db, summary, SaveBatchSize, cancellationToken);

                if (nestedZip.IsIgnored)
                {
                    progress?.Report(new ScanProgress("Skipped ignored nested archive", relativePath, summary.Images, summary.Containers, summary.Errors));
                    continue;
                }

                progress?.Report(new ScanProgress("Scanning nested archive", relativePath, summary.Images, summary.Containers, summary.Errors));
                await ScanNestedZipEntryAsync(db, assets, nestedZip, zipPath, nextChain, relativePath, entry, depth + 1, summary, progress, cancellationToken);
            }
        }
    }

    private async Task ScanNestedZipEntryAsync(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string archiveDisplayPath,
        IArchiveEntry zipEntry,
        int depth,
        ScanSummary summary,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (zipEntry.Size > _options.MaxNestedZipBytesInMemory)
            {
                parent.ScanError = $"Skipped nested archive because it is larger than the configured in-memory limit: {archiveDisplayPath}";
                summary.Errors++;
                logger.LogWarning(
                    "Skipped nested archive {ArchivePath} because it is {NestedZipBytes} bytes and the configured in-memory limit is {MaxNestedZipBytesInMemory} bytes.",
                    archiveDisplayPath,
                    zipEntry.Size,
                    _options.MaxNestedZipBytesInMemory);
                return;
            }

            using var nestedZipStream = new MemoryStream();
            await using (var entryStream = await zipEntry.OpenEntryStreamAsync(cancellationToken))
            {
                await entryStream.CopyToAsync(nestedZipStream, cancellationToken);
            }

            nestedZipStream.Position = 0;
            using var nestedArchive = ArchiveFactory.OpenArchive(nestedZipStream, new ReaderOptions());
            await ScanZipEntriesAsync(db, assets, parent, zipPath, entryChain, archiveDisplayPath, nestedArchive, depth, summary, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SharpCompressException)
        {
            parent.ScanError = $"Unable to scan nested archive: {archiveDisplayPath}";
            summary.Errors++;
            logger.LogWarning(ex, "Unable to scan nested archive {ArchivePath}", archiveDisplayPath);
        }
    }

    private int SaveBatchSize => Math.Max(1, _options.ScanSaveBatchSize);

    private int ScanParallelism => Math.Min(Math.Max(1, _options.ScanParallelism), 64);

    private int MutationBatchSize => Math.Max(1, _options.DatabaseMutationBatchSize);

    private static async Task FlushScanBatchAsync(LibraryDbContext db, ScanSummary summary, int batchSize, CancellationToken cancellationToken)
    {
        var scanned = summary.Images + summary.Containers;
        if (scanned == 1 || scanned % batchSize == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static Stream OpenArchiveStream(string archivePath, IReadOnlyList<string> entryChain)
    {
        if (entryChain.Count == 0)
        {
            return File.OpenRead(archivePath);
        }

        Stream current = File.OpenRead(archivePath);
        try
        {
            foreach (var entryPath in entryChain)
            {
                using var archive = ArchiveFactory.OpenArchive(current, new ReaderOptions());
                var entry = archive.Entries.FirstOrDefault(entry => string.Equals(entry.Key, entryPath, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"Missing nested archive entry: {entryPath}");
                var next = new MemoryStream();
                using (var entryStream = entry.OpenEntryStream())
                {
                    entryStream.CopyTo(next);
                }

                current.Dispose();
                next.Position = 0;
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static async Task<string> CreateArchiveStableKeyAsync(FileInfo file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenRead();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            hash.AppendData(buffer.AsSpan(0, bytesRead));
        }

        return $"{ImageFormat.ArchiveStableKeyPrefix(file.Name)}:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    private static LibraryAsset UpsertNestedZip(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset parent,
        string zipPath,
        IReadOnlyList<string> entryChain,
        string relativePath,
        IArchiveEntry entry,
        int depth)
    {
        var entryName = LastArchivePathSegment(entry.Key ?? string.Empty);
        return Upsert(
            db,
            assets,
            stableKey: $"{parent.StableKey}!{entryName}",
            parent: parent,
            name: entryName,
            kind: AssetKinds.Zip,
            sourceType: AssetSourceTypes.NestedZipArchive,
            sourcePath: zipPath,
            entryChain: string.Join('\n', entryChain),
            relativePath: relativePath,
            contentType: null,
            sizeBytes: entry.Size,
            modifiedAt: ArchiveModifiedAt(entry),
            depth: depth);
    }

    private static LibraryAsset UpsertZipFolderParents(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        LibraryAsset archiveParent,
        string zipPath,
        string archiveDisplayPath,
        string entryFullName,
        int depth)
    {
        var parts = entryFullName
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length <= 1)
        {
            return archiveParent;
        }

        var parent = archiveParent;
        var displayPath = archiveDisplayPath;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            var folderName = parts[index];
            displayPath = $"{displayPath}!{folderName}";
            parent = Upsert(
                db,
                assets,
                stableKey: $"{parent.StableKey}!folder:{folderName}",
                parent: parent,
                name: folderName,
                kind: AssetKinds.Folder,
                sourceType: AssetSourceTypes.ZipFolderEntry,
                sourcePath: zipPath,
                entryChain: null,
                relativePath: displayPath,
                contentType: null,
                sizeBytes: null,
                modifiedAt: null,
                depth: depth + index);
        }

        return parent;
    }

    private static string FileSystemMediaSourceType(string mediaKind)
    {
        return mediaKind switch
        {
            AssetKinds.Audio => AssetSourceTypes.FileSystemAudio,
            AssetKinds.Video => AssetSourceTypes.FileSystemVideo,
            _ => AssetSourceTypes.FileSystemImage
        };
    }

    private static string ArchiveMediaSourceType(string mediaKind, bool nested)
    {
        return mediaKind switch
        {
            AssetKinds.Audio => nested ? AssetSourceTypes.NestedZipAudioEntry : AssetSourceTypes.ZipAudioEntry,
            AssetKinds.Video => nested ? AssetSourceTypes.NestedZipVideoEntry : AssetSourceTypes.ZipVideoEntry,
            _ => nested ? AssetSourceTypes.NestedZipImageEntry : AssetSourceTypes.ZipImageEntry
        };
    }

    private static string BuildArchiveMediaDisplayName(IReadOnlyList<string> entryChain, string mediaName)
    {
        if (entryChain.Count <= 1)
        {
            return mediaName;
        }

        var nestedZipNames = entryChain
            .Take(entryChain.Count - 1)
            .Where(ImageFormat.IsArchive)
            .Select(LastArchivePathSegment)
            .ToArray();

        return nestedZipNames.Length == 0
            ? mediaName
            : $"{string.Join(" / ", nestedZipNames)} / {mediaName}";
    }

    private static string LastArchivePathSegment(string path)
    {
        var normalized = path.Replace('\\', '/');
        var lastSeparator = normalized.LastIndexOf('/');
        return lastSeparator < 0 ? normalized : normalized[(lastSeparator + 1)..];
    }

    private static DateTimeOffset? ArchiveModifiedAt(IArchiveEntry entry)
    {
        if (entry.LastModifiedTime is not { } value)
        {
            return null;
        }

        var specified = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Local)
            : value;

        return new DateTimeOffset(specified);
    }

    private static bool IsArchiveEntryIgnored(
        Dictionary<string, LibraryAsset> assets,
        string rootArchiveStableKey,
        IReadOnlyList<string> entryChain)
    {
        var stableKey = BuildArchiveEntryContainerStableKey(rootArchiveStableKey, entryChain);
        return assets.TryGetValue(stableKey, out var asset) && asset.IsIgnored;
    }

    private static bool ArchiveHasAvailablePreview(IEnumerable<LibraryAsset> assets, string archiveStableKey)
    {
        var previewPrefix = archiveStableKey + "!preview:";
        return assets.Any(asset =>
            asset.IsAvailable &&
            asset.IsMedia &&
            asset.StableKey.StartsWith(previewPrefix, StringComparison.Ordinal));
    }

    private static void UpdateArchiveDescendantPaths(
        IEnumerable<LibraryAsset> assets,
        string archiveStableKey,
        string? sourcePath,
        string? oldArchiveRelativePath,
        string newArchiveRelativePath)
    {
        var descendantPrefix = archiveStableKey + "!";
        foreach (var asset in assets.Where(asset => asset.StableKey.StartsWith(descendantPrefix, StringComparison.Ordinal)))
        {
            asset.SourcePath = sourcePath;
            if (!string.IsNullOrWhiteSpace(oldArchiveRelativePath) &&
                asset.RelativePath.StartsWith(oldArchiveRelativePath + "!", StringComparison.Ordinal))
            {
                asset.RelativePath = newArchiveRelativePath + asset.RelativePath[oldArchiveRelativePath.Length..];
            }
        }
    }

    private static string BuildArchiveEntryContainerStableKey(string rootArchiveStableKey, IReadOnlyList<string> entryChain)
    {
        var stableKey = rootArchiveStableKey;
        foreach (var entryPath in entryChain)
        {
            var parts = entryPath
                .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            foreach (var folder in parts.Take(parts.Length - 1))
            {
                stableKey = $"{stableKey}!folder:{folder}";
            }

            stableKey = $"{stableKey}!{parts[^1]}";
        }

        return stableKey;
    }

    private static void MarkRootScanTargetsUnavailable(IEnumerable<LibraryAsset> assets)
    {
        foreach (var asset in assets.Where(IsRootScanTarget))
        {
            asset.IsAvailable = false;
            asset.ScanError = null;
        }
    }

    private static bool IsRootScanTarget(LibraryAsset asset)
    {
        if (asset.SourceType is AssetSourceTypes.FileSystemFolder or
            AssetSourceTypes.FileSystemImage or
            AssetSourceTypes.FileSystemAudio or
            AssetSourceTypes.FileSystemVideo)
        {
            return true;
        }

        return asset.Kind == AssetKinds.Zip && string.IsNullOrWhiteSpace(asset.EntryChain);
    }

    private static void MarkArchiveDescendantsUnavailable(IEnumerable<LibraryAsset> assets, string archiveStableKey)
    {
        var prefix = archiveStableKey + "!";
        foreach (var asset in assets.Where(asset => asset.StableKey.StartsWith(prefix, StringComparison.Ordinal)))
        {
            asset.IsAvailable = false;
            asset.ScanError = null;
        }
    }

    private static void MarkMissingArchiveDescendantsUnavailable(IEnumerable<LibraryAsset> assets)
    {
        var assetList = assets.ToList();
        var missingArchives = assetList
            .Where(asset => asset.Kind == AssetKinds.Zip && !asset.IsAvailable)
            .Select(asset => asset.StableKey + "!")
            .ToArray();

        if (missingArchives.Length == 0)
        {
            return;
        }

        foreach (var asset in assetList.Where(asset => missingArchives.Any(prefix => asset.StableKey.StartsWith(prefix, StringComparison.Ordinal))))
        {
            asset.IsAvailable = false;
            asset.ScanError = null;
        }
    }

    private static async Task<int> PruneUnavailableAsync(LibraryDbContext db, int batchSize, CancellationToken cancellationToken)
    {
        var pruned = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leafIds = await db.Assets
                .Where(asset => !asset.IsAvailable && !db.Assets.Any(child => child.ParentId == asset.Id))
                .OrderBy(asset => asset.Id)
                .Select(asset => asset.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (leafIds.Count == 0)
            {
                return pruned;
            }

            pruned += await db.Assets
                .Where(asset => leafIds.Contains(asset.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static async Task<int> DeleteAllAssetsAsync(
        LibraryDbContext db,
        int batchSize,
        IProgress<ScanProgress>? progress,
        string currentItem,
        int totalAssets,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leafIds = await db.Assets
                .Where(asset => !db.Assets.Any(child => child.ParentId == asset.Id))
                .OrderBy(asset => asset.Id)
                .Select(asset => asset.Id)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (leafIds.Count == 0)
            {
                progress?.Report(new ScanProgress("Deleted indexed assets", currentItem, 0, 0, 0, deleted, totalAssets, "assets"));
                return deleted;
            }

            deleted += await db.Assets
                .Where(asset => leafIds.Contains(asset.Id))
                .ExecuteDeleteAsync(cancellationToken);

            progress?.Report(new ScanProgress("Deleting indexed assets", currentItem, 0, 0, 0, deleted, totalAssets, "assets"));
        }
    }

    private static LibraryAsset Upsert(
        LibraryDbContext db,
        Dictionary<string, LibraryAsset> assets,
        string stableKey,
        LibraryAsset? parent,
        string name,
        string kind,
        string sourceType,
        string? sourcePath,
        string? entryChain,
        string relativePath,
        string? contentType,
        long? sizeBytes,
        DateTimeOffset? modifiedAt,
        int depth)
    {
        if (!assets.TryGetValue(stableKey, out var asset))
        {
            asset = new LibraryAsset
            {
                StableKey = stableKey,
                Name = name,
                Kind = kind,
                SourceType = sourceType
            };

            assets.Add(stableKey, asset);
            db.Assets.Add(asset);
        }

        asset.Parent = parent;
        asset.Name = name;
        asset.Kind = kind;
        asset.SourceType = sourceType;
        asset.SourcePath = sourcePath;
        asset.EntryChain = entryChain;
        asset.RelativePath = relativePath;
        asset.SortKey = NaturalSortKey.Build(string.IsNullOrWhiteSpace(relativePath) ? name : relativePath);
        asset.ContentType = contentType;
        asset.SizeBytes = sizeBytes;
        asset.ModifiedAt = modifiedAt;
        asset.Depth = depth;
        asset.IsAvailable = true;
        asset.ScanError = null;
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        return asset;
    }
}

internal sealed class FileSystemDiscovery
{
    public ConcurrentBag<DiscoveredFolder> Folders { get; } = [];

    public ConcurrentBag<DiscoveredMediaFile> MediaFiles { get; } = [];

    public ConcurrentBag<DiscoveredArchiveFile> ArchiveFiles { get; } = [];

    public ConcurrentBag<DirectoryScanError> DirectoryErrors { get; } = [];

    public ConcurrentBag<FileScanError> FileErrors { get; } = [];
}

internal sealed record DirectoryScanTarget(
    string StableKey,
    string DirectoryPath,
    int Depth);

internal sealed record DiscoveredFolder(
    string StableKey,
    string ParentStableKey,
    string Name,
    string FullPath,
    string RelativePath,
    DateTimeOffset? ModifiedAt,
    int Depth);

internal sealed record DiscoveredMediaFile(
    string ParentStableKey,
    string Name,
    string Kind,
    string FullPath,
    string RelativePath,
    string? ContentType,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    int Depth);

internal sealed record DiscoveredArchiveFile(
    string ParentStableKey,
    string Name,
    string FullPath,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    int Depth);

internal sealed record FingerprintedArchiveFile(
    DiscoveredArchiveFile Archive,
    string? StableKey,
    Exception? Error,
    bool IsCached);

internal sealed record DirectoryScanError(
    string StableKey,
    string Message);

internal sealed record FileScanError(
    string Path,
    string RelativePath,
    string ProgressPhase);

public sealed class ScanSummary
{
    public int Images { get; set; }

    public int Containers { get; set; }

    public int Errors { get; set; }

    public int Pruned { get; set; }
}

public sealed record ScanProgress(
    string Phase,
    string CurrentItem,
    int Images,
    int Containers,
    int Errors,
    int? WorkDone = null,
    int? WorkTotal = null,
    string? WorkLabel = null);
