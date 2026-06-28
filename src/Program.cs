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
    await LibrarySchemaInitializer.EnsureAsync(db);
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
