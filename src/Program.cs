using ArchiveImageLabler.Components;
using ArchiveImageLabler.Data;
using ArchiveImageLabler.Models;
using ArchiveImageLabler.Services;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpCompress.Common;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<LibraryOptions>(builder.Configuration.GetSection("Library"));

var libraryOptions = builder.Configuration.GetSection("Library").Get<LibraryOptions>() ?? new LibraryOptions();
Directory.CreateDirectory(libraryOptions.DataPath);
var keyPath = Path.Combine(libraryOptions.DataPath, "keys");
Directory.CreateDirectory(keyPath);
var dbPath = Path.Combine(libraryOptions.DataPath, "archiveimagelabler.db");

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
    .SetApplicationName("ArchiveImageLabler");

builder.Services.AddDbContextFactory<LibraryDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddScoped<LibraryQueries>();
builder.Services.AddSingleton<BackgroundScanQueue>();
builder.Services.AddHostedService<BackgroundScanWorker>();
builder.Services.AddSingleton<ImageContentService>();
builder.Services.AddSingleton<SourceExtractionCache>();

if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Debug:StopApplicationOnLastBrowserClose"))
{
    builder.Services.AddSingleton<CircuitHandler, StopApplicationOnLastBrowserCloseCircuitHandler>();
}

var app = builder.Build();

app.MapDefaultEndpoints();

await app.Services.GetRequiredService<SourceExtractionCache>().ClearAsync();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<LibraryDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await EnsureLibrarySchemaAsync(db);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/api/assets/{id:long}/content", async (
    long id,
    IDbContextFactory<LibraryDbContext> dbFactory,
    ImageContentService imageContent,
    CancellationToken cancellationToken) =>
{
    await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
    var asset = await db.Assets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == id, cancellationToken);
    if (asset is null || !asset.IsAvailable || asset.IsIgnored || !asset.IsMedia)
    {
        return Results.NotFound();
    }

    try
    {
        var content = await imageContent.OpenAsync(asset, cancellationToken);
        return Results.File(content.Stream, content.ContentType, enableRangeProcessing: true);
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SharpCompressException)
    {
        return Results.Problem("The media file could not be opened.", statusCode: StatusCodes.Status422UnprocessableEntity);
    }
});

app.MapGet("/api/source-cache/{sessionId}/{assetId:long}", (
    string sessionId,
    long assetId,
    SourceExtractionCache extractionCache) =>
{
    var file = extractionCache.GetFile(sessionId, assetId);
    if (file is null || !System.IO.File.Exists(file.Path))
    {
        return Results.NotFound();
    }

    return Results.File(System.IO.File.OpenRead(file.Path), file.ContentType, enableRangeProcessing: true);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task EnsureLibrarySchemaAsync(LibraryDbContext db)
{
    await db.Database.EnsureCreatedAsync();

    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    await using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Assets');";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }
    }

    if (!columns.Contains(nameof(LibraryAsset.IsIgnored)))
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN IsIgnored INTEGER NOT NULL DEFAULT 0;");
    }

    if (!columns.Contains(nameof(LibraryAsset.SortKey)))
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN SortKey TEXT NOT NULL DEFAULT '';");
    }

    if (!columns.Contains(nameof(LibraryAsset.LabelName)))
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN LabelName TEXT NOT NULL DEFAULT '';");
    }

    if (!columns.Contains(nameof(LibraryAsset.DisplayOrder)))
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN DisplayOrder INTEGER NULL;");
    }

    if (!columns.Contains(nameof(LibraryAsset.PreviewAssetId)))
    {
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Assets ADD COLUMN PreviewAssetId INTEGER NULL;");
    }

    var assetsMissingSortKeys = await db.Assets
        .Where(asset => asset.SortKey == string.Empty)
        .ToListAsync();

    if (assetsMissingSortKeys.Count > 0)
    {
        foreach (var asset in assetsMissingSortKeys)
        {
            asset.SortKey = NaturalSortKey.Build(string.IsNullOrWhiteSpace(asset.RelativePath) ? asset.Name : asset.RelativePath);
        }

        await db.SaveChangesAsync();
    }

    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Assets_IsIgnored ON Assets (IsIgnored);");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Assets_SortKey ON Assets (SortKey);");
}
