# Implemented Features

## Runtime

- .NET 10 Blazor Web App.
- Dockerfile for containerized runtime, colocated with the Blazor app project under `src`.
- Docker Compose remains in the repository root and builds with root context plus `src/Dockerfile`.
- Docker Compose setup with:
  - App published on `http://localhost:5080` through a localhost-only port bind.
  - Read-only mounted media library at `/library`.
  - Persistent SQLite/app data bind mount at `/app/data`, intended to point at `.archiveimagelabler` beside the media library.
- `.env.example` for local Docker settings.
- Docker health check endpoint at `/health`.
- Non-root container runtime user.
- Data Protection keys persisted under the app data path.
- Official `aspire-apphost` template project for Rider/debug orchestration.
- Rider run/debug profiles for local HTTP, Aspire AppHost, and Docker Compose workflows.
- App-level logging config keeps EF Core logs at warning and higher.

## Library Scanning

- Manual scan from the app UI.
- Scan runs as a lazy maintenance pass that reuses archive previews, skips full archive depth scans, and prunes unavailable entries.
- Background scan queue and worker so archive candidates are indexed one at a time without blocking the whole UI.
- Scan results are saved incrementally so newly discovered archives can appear before the full scan completes.
- Recursive folder scanning.
- Raw image indexing for:
  - `.jpg`
  - `.jpeg`
  - `.png`
  - `.gif`
  - `.webp`
  - `.bmp`
- Raw audio indexing for:
  - `.mp3`
  - `.m4a`
  - `.aac`
  - `.wav`
  - `.flac`
  - `.ogg`
  - `.opus`
- Raw video indexing for:
  - `.mp4`
  - `.m4v`
  - `.mov`
  - `.webm`
  - `.ogv`
  - `.avi`
  - `.mkv`
- Zip and rar archive indexing.
- Media indexing inside zip/rar archives.
- Nested zip/rar archive media indexing with a default depth limit of `3`.
- Archive folder containers for paths inside archive files.
- Nested archives can be represented as lightweight archive entries under their archive folder.
- Ignored archive and nested archive sources are skipped before they are opened.
- Ignored archives keep their existing preview and descendants visible, but are greyed in the overview.
- Duplicate archive copies are deduplicated by archive content hash.
- Stable asset keys so user metadata can survive rescans.
- Missing/unavailable assets are marked instead of immediately deleted.
- Empty, unreadable, corrupt, and depth-limited archives can be surfaced with scan errors.
- Scan cancellation from the UI.
- Configurable scan save and database mutation batch sizes.
- Configurable in-memory size limit for nested archive loading.

## Browsing

- Main screen with source list and representative previews.
- Collapsible nested source rows in the sidebar.
- Sidebar preserves nested source order instead of sorting every level alphabetically.
- Archive rows can be reordered by drag and drop in the sidebar.
- Sidebar archive-only toggle that hides folders and shows archives directly.
- Folder previews show archive cards that can be clicked to select the archive in the sidebar.
- Archive cards can be reordered by drag and drop in the overview.
- Browser-generated first-frame previews for video files such as MP4.
- Preview grid before opening a source page.
- Source pages for folders, archive files, archive folders, and nested archive containers.
- Archive Source pages render immediately, unpack media to temporary app-data cache in the background, show extraction progress, and display media as each file is ready.
- Larger image viewer.
- Audio/video playback in the viewer with browser-native controls.
- Video playback loops by default.
- Previous/next navigation in the viewer.
- Keyboard arrow navigation in the viewer.
- Bottom filmstrip in the viewer.
- Live process memory widget in the app header.
- App-data and temporary cache disk usage widget in the app header.
- Image streaming endpoint with range processing.
- Source-page cache streaming endpoint for temporarily unpacked media.
- Rescan clears the UI and releases visible image/source references before rebuilding the index.

## Metadata

- SQLite-backed metadata.
- Metadata for media files and containers.
- Tags.
- Description.
- Optional 1-5 star rating.
- Ignore flag for archive and nested archive sources.
- Metadata editing drawer.
- Preview image selection for archives, archive groups, and folders from the edit drawer.
- Preview image selection for archive cards from the overview.
- Manual archive display order persisted through rescans.
- Tag normalization by comma-separated values.

## Filtering

- Search across names, relative paths, tags, and descriptions.
- Rating filter.
- Unrated-only filter.

## Repo Hygiene

- `.gitignore` for build output, local databases, IDE state, logs, and overrides.
- Repository README landing page.
- Codex project guide in `AGENTS.md`.
- Feature documentation split into implemented and planned files.
