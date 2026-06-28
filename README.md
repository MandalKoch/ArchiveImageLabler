# ArchiveImageLabler

ArchiveImageLabler is a local-first media library for large folders that contain loose images, audio, videos, zip/rar archives, archive folders, and archives inside other archives. It runs as a Dockerized .NET 10 Blazor app, opens in your browser at `localhost`, and keeps your original media folder mounted read-only.

The app is built for browsing first: scan a mounted folder, see previews immediately as archives are discovered, open sources as pages, view images larger, play audio/video, and add your own tags, descriptions, and 1-5 star ratings.

## What It Does

- Browses raw image files: `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, `.bmp`.
- Browses raw audio files: `.mp3`, `.m4a`, `.aac`, `.wav`, `.flac`, `.ogg`, `.opus`.
- Browses raw video files: `.mp4`, `.m4v`, `.mov`, `.webm`, `.ogv`, `.avi`, `.mkv`.
- Browses media inside `.zip`, `.cbz`, `.rar`, `.cbr`, `.7z`, `.cb7`, `.tar`, `.cbt`, and common compressed tar files without extracting the whole archive.
- Browses media inside nested archives with a default depth limit of `3`.
- Preserves folder structure inside archives, including `archive!folder/folder/image.jpg` and `archive!folder/folder/inner.zip`.
- Shows representative previews for folders and archives.
- Lets you choose custom preview images for archives, groups, and folders.
- Opens folders, archives, nested archive folders, and nested archives as their own pages.
- Opens archive Source pages immediately, then unpacks media to temporary app-data cache with a progress bar and loads each image as soon as it is ready.
- Lets you ignore archive and nested archive sources so future scans skip loading them while their existing preview remains visible.
- Lets you reorder archive cards and sidebar archive rows by drag and drop.
- Stores metadata in SQLite:
  - Tags
  - Description
  - Optional 1-5 star rating
- Filters by search text, rating, and unrated items.
- Shows live app memory usage in the header while browsing or scanning.
- Shows app-data and temporary cache disk usage in the header.
- Keeps the mounted source library read-only.

## Runtime Model

Docker Compose is the main runtime path.

The Compose file stays in the repository root. The Dockerfile lives next to the Blazor app code at [src/Dockerfile](src/Dockerfile), while the Docker build context remains the repository root so project references such as [servicedefaults](servicedefaults) are available during publish.

```text
Host folder configured by .env       Docker container
ARCHIVEIMAGELABLER_LIBRARY_PATH  ->  /library   read-only media library
ARCHIVEIMAGELABLER_DATA_PATH     ->  /app/data  SQLite, app keys, temp cache
```

SQLite is stored at:

```text
/app/data/archiveimagelabler.db
```

The default browser URL is:

```text
http://localhost:5080
```

Docker Compose binds the app to `127.0.0.1` only, so it is not exposed to your LAN by default.

## Quick Start

1. Copy [.env.example](.env.example) to `.env`.
2. Set `ARCHIVEIMAGELABLER_LIBRARY_PATH` to your real media folder and `ARCHIVEIMAGELABLER_DATA_PATH` to a writable `.archiveimagelabler` folder beside it:

```env
ARCHIVEIMAGELABLER_LIBRARY_PATH=D:/Images
ARCHIVEIMAGELABLER_DATA_PATH=D:/Images/.archiveimagelabler
Library__ScanParallelism=4
```

3. Start Docker Desktop.
4. Run:

```powershell
docker compose up --build
```

5. Open:

```text
http://localhost:5080
```

6. Click `Scan`.

`Scan` performs a lazy maintenance pass: it refreshes folders and loose media, queues archive work in the background, adds each discovered archive as soon as it is indexed, and prunes entries that are no longer available. Opening an archive page performs the deeper archive scan when needed. `Rescan` clears the current UI, deletes indexed assets from SQLite, runs a fresh lazy scan, and releases currently held UI references before scanning starts.

Archive Source pages render before media extraction is complete. For archive sources, the page creates a temporary extraction session under `/app/data/cache/source-pages`, shows unpack progress, and displays media as each file is ready. The temporary session is deleted when the Source page is closed or replaced.

## Local Development

Restore and build:

```powershell
dotnet restore
dotnet build --no-restore
```

Run without Docker:

```powershell
dotnet run --urls http://localhost:5071
```

For debugging, the intended development path is the Aspire AppHost in [apphost](apphost). The AppHost was created from the official `aspire-apphost` template and launches the Blazor app from [src](src). Later it can also launch local services such as Ollama or background indexing services. Docker Compose remains the runtime/deployment path.

Run the Aspire AppHost:

```powershell
dotnet run --project .\apphost\ArchiveImageLabler.AppHost.csproj
```

For local non-Docker runs, the development config points at:

```text
D:\Images
```

Change `Library:RootPath` in [src/appsettings.Development.json](src/appsettings.Development.json) if needed.

## Docker

Validate Compose:

```powershell
docker compose config
```

Build and run:

```powershell
docker compose up --build
```

The Compose build uses:

```yaml
build:
  context: .
  dockerfile: src/Dockerfile
```

Stop:

```powershell
docker compose down
```

Remove the SQLite/app-data volume:

```powershell
docker compose down
```

SQLite and app keys live in the host data folder configured by `ARCHIVEIMAGELABLER_DATA_PATH`.

Health check endpoint:

```text
http://localhost:5080/health
```

## Rider

Rider is the primary IDE for this repository.

Shared Rider run/debug profiles are stored in [.run](.run):

- `ArchiveImageLabler: Aspire AppHost`
- `ArchiveImageLabler: local http`
- `ArchiveImageLabler: Docker Compose`

Development launch profiles enable `Debug__StopApplicationOnLastBrowserClose=true`. When the last Blazor browser tab/window closes, the app stops itself so the Rider debug session can end naturally. Docker runtime profiles do not enable this behavior.

For Rider memory profiling and debug monitor, prefer `ArchiveImageLabler: local http`. Aspire launches the Blazor app as a child resource process, and Docker runs it inside a container, so Rider may otherwise attach to the AppHost/container instead of the actual Blazor app process.

If Rider complains that the `.NET Launch Settings Profile` configuration type is disabled, reload the project and make sure Rider's bundled .NET/ASP.NET support is enabled. The checked-in profiles use Rider's `.NET Launch Settings Profile` run configuration type.

## Documentation

- [Implemented features](features/implemented-features.md)
- [Planned features](features/planned-features.md)
- [Codex project guide](AGENTS.md)

## Status

This is an early local image-library build. The current focus is reliable indexing, previewing, metadata editing, and Docker runtime behavior. Ollama/local LLM features are planned for a later phase.
