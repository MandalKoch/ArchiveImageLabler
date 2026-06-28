using ArchiveImageLabler.Models;

namespace ArchiveImageLabler.Services;

public static class ImageFormat
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp",
        ".bmp"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".m4a",
        ".aac",
        ".wav",
        ".flac",
        ".ogg",
        ".opus"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
        ".webm",
        ".ogv",
        ".avi",
        ".mkv"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".cbz",
        ".rar",
        ".cbr",
        ".7z",
        ".cb7",
        ".tar",
        ".cbt",
        ".tar.gz",
        ".tgz",
        ".tar.bz2",
        ".tbz",
        ".tbz2",
        ".tar.xz",
        ".txz"
    };

    private static readonly string[] CompoundArchiveExtensions =
    [
        ".tar.gz",
        ".tar.bz2",
        ".tar.xz"
    ];

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsMedia(string path) => IsImage(path) || IsAudio(path) || IsVideo(path);

    public static string MediaKindFor(string path)
    {
        if (IsAudio(path))
        {
            return AssetKinds.Audio;
        }

        if (IsVideo(path))
        {
            return AssetKinds.Video;
        }

        return AssetKinds.Image;
    }

    public static bool IsZip(string path) => ExtensionFor(path) is ".zip" or ".cbz";

    public static bool IsRar(string path) => ExtensionFor(path) is ".rar" or ".cbr";

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(ExtensionFor(path));

    public static string ArchiveStableKeyPrefix(string path)
    {
        return ExtensionFor(path) switch
        {
            ".zip" or ".cbz" => "zip",
            ".rar" or ".cbr" => "rar",
            ".7z" or ".cb7" => "7z",
            ".tar" or ".cbt" or ".tar.gz" or ".tgz" or ".tar.bz2" or ".tbz" or ".tbz2" or ".tar.xz" or ".txz" => "tar",
            _ => "archive"
        };
    }

    public static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".ogv" => "video/ogg",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }

    private static string ExtensionFor(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var compoundExtension in CompoundArchiveExtensions)
        {
            if (fileName.EndsWith(compoundExtension, StringComparison.OrdinalIgnoreCase))
            {
                return compoundExtension;
            }
        }

        return Path.GetExtension(path).ToLowerInvariant();
    }
}
