using System.ComponentModel.DataAnnotations;

namespace ArchiveImageLabler.Models;

public sealed class LibraryAsset
{
    public long Id { get; set; }

    public long? ParentId { get; set; }

    public LibraryAsset? Parent { get; set; }

    public List<LibraryAsset> Children { get; set; } = [];

    [MaxLength(2048)]
    public required string StableKey { get; set; }

    [MaxLength(255)]
    public required string Name { get; set; }

    [MaxLength(255)]
    public string LabelName { get; set; } = string.Empty;

    [MaxLength(64)]
    public required string Kind { get; set; }

    [MaxLength(64)]
    public required string SourceType { get; set; }

    [MaxLength(2048)]
    public string? SourcePath { get; set; }

    [MaxLength(4096)]
    public string? EntryChain { get; set; }

    [MaxLength(4096)]
    public string RelativePath { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string SortKey { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? ContentType { get; set; }

    public long? SizeBytes { get; set; }

    public DateTimeOffset? ModifiedAt { get; set; }

    public bool IsAvailable { get; set; } = true;

    public bool IsIgnored { get; set; }

    public int Depth { get; set; }

    [MaxLength(1024)]
    public string? ScanError { get; set; }

    [MaxLength(4000)]
    public string Description { get; set; } = string.Empty;

    public int? Rating { get; set; }

    [MaxLength(1000)]
    public string Tags { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsImage => Kind == AssetKinds.Image;

    public bool IsAudio => Kind == AssetKinds.Audio;

    public bool IsVideo => Kind == AssetKinds.Video;

    public bool IsMedia => IsImage || IsAudio || IsVideo;

    public bool IsContainer => Kind == AssetKinds.Folder || Kind == AssetKinds.Zip;
}

public static class AssetKinds
{
    public const string Folder = "Folder";
    public const string Zip = "Zip";
    public const string Image = "Image";
    public const string Audio = "Audio";
    public const string Video = "Video";
}

public static class AssetSourceTypes
{
    public const string FileSystemFolder = "FileSystemFolder";
    public const string FileSystemImage = "FileSystemImage";
    public const string FileSystemAudio = "FileSystemAudio";
    public const string FileSystemVideo = "FileSystemVideo";
    public const string ArchiveGroup = "ArchiveGroup";
    public const string ZipArchivePreview = "ZipArchivePreview";
    public const string ZipArchive = "ZipArchive";
    public const string ZipFolderEntry = "ZipFolderEntry";
    public const string ZipImageEntry = "ZipImageEntry";
    public const string ZipAudioEntry = "ZipAudioEntry";
    public const string ZipVideoEntry = "ZipVideoEntry";
    public const string NestedZipArchive = "NestedZipArchive";
    public const string NestedZipImageEntry = "NestedZipImageEntry";
    public const string NestedZipAudioEntry = "NestedZipAudioEntry";
    public const string NestedZipVideoEntry = "NestedZipVideoEntry";
}
