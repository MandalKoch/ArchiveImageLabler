using ArchiveImageLabler.Data;
using ArchiveImageLabler.Models;
using ArchiveImageLabler.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

var envPath = GetEnvPath(args);
var envValues = envPath is null ? [] : LoadEnvFile(envPath);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

builder.Configuration.AddInMemoryCollection(MapArchiveEnvironment(envValues));
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.Configure<LibraryOptions>(builder.Configuration.GetSection("Library"));
var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
Directory.CreateDirectory(libraryOptions.DataPath);
var dbPath = Path.Combine(libraryOptions.DataPath, "archiveimagelabler.db");

builder.Services.AddDbContextFactory<LibraryDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<LibraryScanner>();

using var host = builder.Build();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

try
{
    using var scope = host.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
    await using (var db = await dbFactory.CreateDbContextAsync(cancellation.Token))
    {
        await LibrarySchemaInitializer.EnsureAsync(db, cancellation.Token);
    }

    var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
    Console.WriteLine($"Scanning: {libraryOptions.RootPath}");
    Console.WriteLine($"Database: {dbPath}");
    Console.WriteLine("Counting folders and files...");
    var totalWork = CountFileSystemEntries(libraryOptions.RootPath, cancellation.Token);
    Console.WriteLine($"Total work: {totalWork.Total} folders/files" + (totalWork.Errors > 0 ? $" ({totalWork.Errors} count errors)" : string.Empty));

    var overallProgress = new OverallProgress(totalWork.Total);
    var lastProgress = DateTimeOffset.MinValue;
    var startedAt = Stopwatch.GetTimestamp();
    var progress = new Progress<ScanProgress>(value =>
    {
        var now = DateTimeOffset.UtcNow;
        if (value.Phase != "Scan complete" && now - lastProgress < TimeSpan.FromMilliseconds(750))
        {
            return;
        }

        lastProgress = now;
        Console.WriteLine(FormatProgress(value, overallProgress));
    });

    var summary = await scanner.ScanAsync(progress, cancellation.Token);
    var elapsed = Stopwatch.GetElapsedTime(startedAt);
    Console.WriteLine($"Done in {elapsed}. {summary.Images} media, {summary.Containers} containers, {summary.Pruned} pruned, {summary.Errors} errors.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Scan cancelled.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string? GetEnvPath(string[] args)
{
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] is "--env" or "--env-file")
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{args[index]} requires a file path.");
            }

            return Path.GetFullPath(args[index + 1]);
        }

        const string envPrefix = "--env=";
        if (args[index].StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(args[index][envPrefix.Length..]);
        }

        const string envFilePrefix = "--env-file=";
        if (args[index].StartsWith(envFilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(args[index][envFilePrefix.Length..]);
        }
    }

    return null;
}

static Dictionary<string, string?> LoadEnvFile(string path)
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Env file not found: {path}", path);
    }

    foreach (var line in File.ReadLines(path))
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            continue;
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex <= 0)
        {
            continue;
        }

        var key = trimmed[..equalsIndex].Trim();
        var value = trimmed[(equalsIndex + 1)..].Trim().Trim('"');
        values[key] = value;
    }

    return values;
}

static Dictionary<string, string?> MapArchiveEnvironment(IReadOnlyDictionary<string, string?> envValues)
{
    var configuration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    AddIfPresent("ARCHIVEIMAGELABLER_LIBRARY_PATH", "Library:RootPath");
    AddIfPresent("ARCHIVEIMAGELABLER_DATA_PATH", "Library:DataPath");

    foreach (var (key, value) in envValues.Where(item => item.Key.Contains("__", StringComparison.Ordinal)))
    {
        configuration[key.Replace("__", ":")] = value;
    }

    return configuration;

    void AddIfPresent(string envKey, string configurationKey)
    {
        var value = Environment.GetEnvironmentVariable(envKey);
        if (string.IsNullOrWhiteSpace(value) && envValues.TryGetValue(envKey, out var envFileValue))
        {
            value = envFileValue;
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            configuration[configurationKey] = value;
        }
    }
}

static FileSystemWork CountFileSystemEntries(string rootPath, CancellationToken cancellationToken)
{
    var root = Path.GetFullPath(rootPath);
    if (!Directory.Exists(root))
    {
        return new FileSystemWork(0, 1);
    }

    var total = 0;
    var errors = 0;
    var pending = new Stack<string>();
    pending.Push(root);

    while (pending.Count > 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = pending.Pop();
        total++;

        try
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (string.Equals(Path.GetFileName(childDirectory), ".archiveimagelabler", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(childDirectory);
            }

            foreach (var _ in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors++;
        }
    }

    return new FileSystemWork(total, errors);
}

static string FormatProgress(ScanProgress progress, OverallProgress overallProgress)
{
    var prefix = ProgressPrefix(progress, overallProgress);
    var counts = $"{progress.Images} media, {progress.Containers} containers, {progress.Errors} errors";
    if (progress.WorkTotal is > 0)
    {
        var done = Math.Clamp(progress.WorkDone.GetValueOrDefault(), 0, progress.WorkTotal.Value);
        var percent = done * 100d / progress.WorkTotal.Value;
        counts = $"{counts}, {done}/{progress.WorkTotal.Value} {progress.WorkLabel ?? "items"} ({percent:0.#}%)";
    }

    return $"{prefix}{progress.Phase}: {progress.CurrentItem} ({counts})";
}

static string ProgressPrefix(ScanProgress progress, OverallProgress overallProgress)
{
    if (progress.WorkTotal is > 0)
    {
        var done = Math.Clamp(progress.WorkDone.GetValueOrDefault(), 0, progress.WorkTotal.Value);
        return $"[{done} / {progress.WorkTotal.Value}] ";
    }

    overallProgress.Report(progress);
    return overallProgress.Total > 0
        ? $"[{overallProgress.Done} / {overallProgress.Total}] "
        : "[0 / 0] ";
}

internal sealed class OverallProgress(int total)
{
    private readonly HashSet<string> _seenItems = new(StringComparer.OrdinalIgnoreCase);

    public int Total { get; } = Math.Max(0, total);

    public int Done { get; private set; }

    public void Report(ScanProgress progress)
    {
        if (progress.Phase == "Scan complete")
        {
            Done = Total;
            return;
        }

        if (Total == 0 || string.IsNullOrWhiteSpace(progress.CurrentItem))
        {
            return;
        }

        if (progress.Phase is "Preparing archive previews" or "Pruning missing entries" or "Fast scan complete")
        {
            return;
        }

        var key = progress.CurrentItem.Replace('\\', '/');
        if (_seenItems.Add(key))
        {
            Done = Math.Min(Total, Done + 1);
        }
    }
}

internal sealed record FileSystemWork(int Total, int Errors);
