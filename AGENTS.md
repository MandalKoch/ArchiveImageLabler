# Codex Project Guide

## Project
FileZipPreview is a .NET 10 Blazor Web App under `src/` that runs in Docker. It scans a read-only mounted image library at `/library`, indexes raw images plus zip, archive folder, and nested zip images, streams previews from their source, and stores user metadata in SQLite at `/app/data/filezippreview.db`. Debugging is orchestrated through the official Aspire AppHost project under `apphost/`.

## Architecture Defaults
- Docker Compose is the runtime path.
- Keep `docker-compose.yml` in the repository root.
- Keep the Dockerfile beside the Blazor app at `src/Dockerfile`; Compose should build with root context and that Dockerfile path.
- Aspire AppHost is the preferred debugging/development orchestration path.
- The AppHost must be generated/maintained from the official `aspire-apphost` template, not handwritten from scratch.
- The mounted library must stay read-only.
- SQLite and Data Protection keys must stay under the configured `Library:DataPath`, mounted from `IMAGEVAULT_DATA_PATH`.
- Do not bulk-extract the user library. Stream raw files and zip entries on demand.
- Preserve user metadata across rescans by keeping stable asset keys stable.
- Nested zip scanning defaults to depth `3` unless the user explicitly changes it.
- `IsIgnored` is the canonical flag for skipped zip and nested zip sources.

## Important Commands
```powershell
dotnet restore
dotnet build --no-restore
dotnet publish -c Release --no-restore
dotnet run --project .\apphost\FileZipPreview.AppHost.csproj
docker compose config
docker compose up --build
```

## Development Notes
- Rider is the primary IDE for this project.
- Copy `.env.example` to `.env` and set `IMAGEVAULT_LIBRARY_PATH` plus `IMAGEVAULT_DATA_PATH` before Docker runs.
- Docker Compose binds the app to `127.0.0.1` by default.
- Docker Desktop must be running for `docker compose build` or `docker compose up`.
- For debugging, prefer adding/running an Aspire AppHost that launches the Blazor app and any future services such as Ollama.
- Rider run/debug profiles live in `.run/` and should remain committed.
- Avoid committing `App_Data/`, `bin/`, `obj/`, local databases, or Docker override files.

## Verification
Before handing work back, prefer:
```powershell
dotnet build --no-restore
dotnet publish -c Release --no-restore
docker compose config
```

If Docker Desktop is running, also verify:
```powershell
docker compose up --build
```
