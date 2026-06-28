# ArchiveImageLabeler

ArchiveImageLabeler is a local-first media browser for large image and media libraries. It scans a read-only mounted library, indexes loose media plus archive contents, streams previews on demand, and stores user metadata in SQLite.

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

## Health Check

The container exposes:

```text
http://localhost:5080/health
```

Docker Compose uses this endpoint to report container health.
