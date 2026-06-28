# ArchiveImageLabler Scanner

One-shot console scanner for PowerShell.

It runs the same root scan as the homepage `Scan` button, updates the same SQLite database, then exits. It does not start the Blazor UI.

## Run

From the repository root:

```powershell
dotnet run --project .\scanner\ArchiveImageLabler.Scanner.csproj -- --env .\.env
```

The `--env` argument points at an env file:

```env
ARCHIVEIMAGELABLER_LIBRARY_PATH=D:/Images
ARCHIVEIMAGELABLER_DATA_PATH=D:/Images/.archiveimagelabler
Library__ScanParallelism=4
```

The scanner maps those values to:

```text
Library:RootPath -> ARCHIVEIMAGELABLER_LIBRARY_PATH
Library:DataPath -> ARCHIVEIMAGELABLER_DATA_PATH
```

`Library__ScanParallelism` controls how many worker tasks scan folders and fingerprint archive files in parallel. Increase it for faster scans on fast SSDs or many CPU cores; lower it if the disk is saturated.

The database is updated at:

```text
<ARCHIVEIMAGELABLER_DATA_PATH>/archiveimagelabler.db
```

## Override Paths

You can override paths from PowerShell without editing `.env`:

```powershell
dotnet run --project .\scanner\ArchiveImageLabler.Scanner.csproj -- --env .\.env `
  --Library:RootPath "D:\Images" `
  --Library:DataPath "D:\Images\.archiveimagelabler"
```

You can also run without an env file by passing all required settings:

```powershell
dotnet run --project .\scanner\ArchiveImageLabler.Scanner.csproj -- `
  --Library:RootPath "D:\Images" `
  --Library:DataPath "D:\Images\.archiveimagelabler" `
  --Library:ScanParallelism 4
```

## Progress

Before scanning, the tool counts folders and files under `Library:RootPath`.
Each progress line starts with the overall count:

```text
[12 / 340] Scanning loose media: folder/image.jpg (12 media, 3 containers, 0 errors)
```

The scanner also logs the current phase, current folder/archive, indexed media count, container count, error count, and percentage when bounded sub-work is available.

Press `Ctrl+C` to cancel.
