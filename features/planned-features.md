# Planned Features

## Browsing and Indexing

- Incremental rescans based on file timestamps and sizes.
- Persisted last selected source and source-page history.
- Sorting controls for name, date, rating, size, and source type.
- Pagination or virtualization for very large folders and archives.
- Generated thumbnail cache.

## Metadata

- Dedicated tag table with autocomplete and tag counts.
- Bulk metadata editing.
- LabelAffe-style image labeling mode for tagging and naming individual images after the archive-only workflow is stable.
- Favorite/pinned images.
- Saved filter presets.
- Import/export metadata as JSON.
- Duplicate detection by file hash or perceptual hash.
- Read metadata from EXIF, IPTC, and XMP.

## Search and Filtering

- Advanced filters by source type, extension, path, tag, rating, and missing state.
- Natural-language search once local LLM support exists.
- Similar-image grouping.
- Recently viewed and recently edited views.

## Ollama and Local LLM

- Add an Ollama service to `docker-compose.yml`.
- Add an AI service boundary instead of calling Ollama directly from UI components.
- Generate suggested tags from filenames, paths, and existing metadata.
- Generate short image descriptions where vision models are available.
- Rank and reorder images by prompt.
- Store AI suggestions separately from user-confirmed metadata.
- Keep normal browsing functional when Ollama is unavailable.

## Operations

- Health endpoint for Docker checks.
- App version display.
- Safer startup checks for missing `/library`.
- Compose override example for different local library folders.
- Aspire AppHost for debugging/development orchestration.

## UI Polish

- Better mobile layout.
- Drag-resizable source and preview panels.
- More compact density mode.
- Toast notifications for scan and save operations.
