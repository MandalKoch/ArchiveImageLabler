# ArchiveImageLabeler

ArchiveImageLabeler is a local-first media browser for large image and media libraries. It scans a read-only mounted library, indexes loose media plus archive contents, streams previews on demand, and stores user metadata in SQLite. Archive candidates include `.zip`, `.cbz`, `.rar`, `.cbr`, `.7z`, `.cb7`, `.tar`, `.cbt`, and common compressed tar files.

Scans run incrementally: archive candidates are queued in the background and appear as they are indexed, so you do not have to wait for the full scan before browsing. Archive Source pages render immediately, unpack media into a temporary cache under `/app/data/cache/source-pages`, show extraction progress, and display media as soon as each file is ready. Temporary Source-page cache data is deleted when the page is closed or replaced.

The container is designed for local use with Docker Compose. Your media library is mounted read-only at `/library`; app data, SQLite, and Data Protection keys are stored under `/app/data`.

## Quick Start

Create a `.env` file next to `docker-compose.yml`:

```env
ARCHIVEIMAGELABLER_LIBRARY_PATH=D:/Images
ARCHIVEIMAGELABLER_DATA_PATH=D:/Images/.archiveimagelabler
ARCHIVEIMAGELABLER_HTTP_PORT=5080
Library__ScanParallelism=4
```

Run:

```powershell
docker compose up
```

Open:

```text
http://localhost:5080
```

## Compose Example

```yaml
services:
  archiveimagelabeler:
    image: mandaldev/archiveimagelabeler:latest
    ports:
      - "127.0.0.1:5080:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Library__RootPath: /library
      Library__DataPath: /app/data
      Library__ScanParallelism: 4
    volumes:
      - type: bind
        source: D:/Images
        target: /library
        read_only: true
      - type: bind
        source: D:/Images/.archiveimagelabler
        target: /app/data
```

## Tags

- `latest`: newest build from the default branch
- `main` or `master`: latest build from that branch
- `vX.Y.Z`: manually tagged releases
- `sha-...`: exact source commit builds

## Data And Privacy

ArchiveImageLabeler does not modify the mounted media library. User metadata is stored in SQLite at:

```text
/app/data/archiveimagelabler.db
```

Keep `/app/data` mounted to persistent host storage if you want labels, descriptions, ratings, and app keys to survive container rebuilds.

The app also uses `/app/data/cache` for temporary Source-page media extraction. Keep this path on writable storage, but do not back it up as user metadata.

## Browsing Features

- Browse loose images, audio, videos, archive files, archive folders, and nested archives.
- Ignore archives while keeping their existing preview visible.
- Reorder archive cards and sidebar archive rows by drag and drop.
- Select custom preview images for archives, archive groups, and folders.
- View memory usage plus app-data/cache disk usage from the header.

## Health Check

The container exposes:

```text
http://localhost:5080/health
```

Docker Compose uses this endpoint to report container health.
