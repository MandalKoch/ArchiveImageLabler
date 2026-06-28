using FileZipPreview.Models;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace FileZipPreview.Services;

public sealed class ImageContentService
{
    public async Task<ImageContent> OpenAsync(LibraryAsset asset, CancellationToken cancellationToken = default)
    {
        if (!asset.IsMedia)
        {
            throw new InvalidOperationException("Only media assets can be streamed.");
        }

        if (asset.SourceType is AssetSourceTypes.FileSystemImage or AssetSourceTypes.FileSystemAudio or AssetSourceTypes.FileSystemVideo)
        {
            if (asset.SourcePath is null)
            {
                throw new FileNotFoundException("Image source path is missing.");
            }

            return new ImageContent(File.OpenRead(asset.SourcePath), asset.ContentType ?? ImageFormat.ContentTypeFor(asset.SourcePath));
        }

        if (asset.SourcePath is null || string.IsNullOrWhiteSpace(asset.EntryChain))
        {
            throw new FileNotFoundException("Archive image source is incomplete.");
        }

        var chain = asset.EntryChain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var stream = await OpenArchiveEntryAsync(asset.SourcePath, chain, cancellationToken);
        return new ImageContent(stream, asset.ContentType ?? ImageFormat.ContentTypeFor(asset.Name));
    }

    private static async Task<Stream> OpenArchiveEntryAsync(string archivePath, IReadOnlyList<string> chain, CancellationToken cancellationToken)
    {
        Stream current = File.OpenRead(archivePath);
        try
        {
            for (var i = 0; i < chain.Count; i++)
            {
                var archive = ArchiveFactory.OpenArchive(current, new ReaderOptions());
                var entry = archive.Entries.FirstOrDefault(entry => string.Equals(entry.Key, chain[i], StringComparison.Ordinal));
                if (entry is null)
                {
                    archive.Dispose();
                    throw new FileNotFoundException($"Archive entry not found: {chain[i]}");
                }

                if (i == chain.Count - 1)
                {
                    try
                    {
                        var entryStream = await entry.OpenEntryStreamAsync(cancellationToken);
                        return new OwnedArchiveEntryStream(entryStream, archive, current);
                    }
                    catch
                    {
                        archive.Dispose();
                        throw;
                    }
                }

                var next = new MemoryStream();
                await using (var entryStream = await entry.OpenEntryStreamAsync(cancellationToken))
                {
                    await entryStream.CopyToAsync(next, cancellationToken);
                }

                archive.Dispose();
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

    private sealed class OwnedArchiveEntryStream(Stream inner, IDisposable archive, Stream archiveSource) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                archive.Dispose();
                archiveSource.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            archive.Dispose();
            await archiveSource.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}

public sealed record ImageContent(Stream Stream, string ContentType);
