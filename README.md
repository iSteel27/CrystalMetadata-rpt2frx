# CrystalMetadata

Extracts **design metadata** (never data) from SAP Crystal Reports `.rpt` files into one AI-friendly JSON document per
report, so the report can be regenerated in FastReport, MigraDoc/PDFsharp, QuestPDF, HTML or any future engine.

```
.NET 10 client / console  ──named pipe──►  .NET Framework 4.8 (x64) extractor process(es)  ──►  SAP Crystal Reports runtime
   CrystalMetadata.Console                     CrystalMetadata.PipeServer.exe                        (Engine API + in-process RAS)
   CrystalMetadata.Client                      CrystalMetadata.Extractor
                     └──────────── CrystalMetadata.Core (netstandard2.0: models, JSON contract, protocol) ────────────┘
```

The split is forced by the SDK: `CrystalDecisions.*` assemblies only load in .NET Framework. The .NET 10 side has no
Crystal dependency at all; it spawns and supervises the extractor processes, talks to them over named pipes, runs batches,
and hands `ReportMetadata` objects to the conversion pipeline (`IReportConverter`).

## Prerequisites (extraction machine)

| Requirement | Notes |
|---|---|
| Windows x64 | Named pipes + Crystal runtime are Windows-only. |
| **SAP Crystal Reports runtime engine for .NET Framework, 64-bit** | Free MSI from SAP ("CRRuntime_64bit_13_0_xx.msi"). Installs the `CrystalDecisions.*` assemblies into the GAC. There is no NuGet package. The extractor is built `x64`; a 32-bit runtime will not load (exit code 3). |
| .NET Framework 4.8 | Ships with Windows 10/11. |
| .NET 10 SDK | For the client/console and to build. |
| Visual Studio 2022 (build only) | Any edition; the .NET Framework 4.8 targeting pack must be installed. |

The build references the runtime assemblies by strong name + GAC path. If a different service pack is installed, override
the version: `dotnet build -p:CrystalAssemblyVersion=13.0.4000.0` (check `C:\Windows\assembly\GAC_MSIL\CrystalDecisions.Shared`).

## Build & test

```bash
dotnet build CrystalMetadata.sln -c Release
```

```bash
dotnet test CrystalMetadata.sln -c Release
```

`CrystalMetadata.Extractor.Tests` (real `.rpt` files, real runtime) only runs when `CR_RUNTIME_AVAILABLE=1` is set; it
reads `samples/` (override with `CR_SAMPLE_DIR`). Everything else runs anywhere.

End-to-end smoke test (build → schema → batch over `samples/` through the pipe → validate → summary):

```bash
powershell -ExecutionPolicy Bypass -File scripts/smoke.ps1
```

## Quick start (no arguments needed)

Build the solution, set **`CrystalMetadata.Console`** as the startup project and press **F5** (or **Ctrl+F5** to keep the
window open). A guided menu appears: extract one report, extract a folder, check your setup, create the schema, help.
It asks for the paths (you can drag a file or folder into the window) and shows the equivalent command line.

Or from any terminal, with no command word: `CrystalMetadata.Console "C:\Reports\Sales.rpt"` (writes `Sales.metadata.json`
next to it) or `CrystalMetadata.Console "C:\Reports"` (whole folder tree, output in `C:\Reports\metadata`).

## Installer (one file for users)

```bash
powershell -ExecutionPolicy Bypass -File installer/build-installer.ps1 -Version 0.1.0
```

Publishes the app self-contained (users need no .NET install), adds the extractor under `server\`, verifies the package
(including the `.exe.config` the extractor cannot start without, and that no SAP files are packaged), runs the packaged
program's own `doctor`, and compiles `installer/CrystalMetadata.iss` with Inno Setup 6 (`winget install JRSoftware.InnoSetup`)
into `installer/output/CrystalMetadataSetup-<version>.exe`. Use `-SkipCompile` to only assemble and verify `installer/staging`.

The installer is per-user (no administrator rights), shows a **system-check page** before installing (Windows 64-bit,
.NET Framework 4.8, SAP Crystal Reports runtime 64-bit) with the reason and the fix for anything missing plus buttons to the
download pages, and offers a right-click "Convert with CrystalMetadata" item for `.rpt` files. The SAP runtime is
**never bundled** (SAP's licence); missing items do not block installation because converting an existing
`.metadata.json` works without it. After installing, Start menu > "Check my setup" (`doctor`) repeats the checks.
Sign the installer before distributing it or Windows shows an "unknown publisher" warning.
Logs are written to `%LOCALAPPDATA%\CrystalMetadata\logs`.

## Convert to a FastReport (open source) template

```bash
CrystalMetadata.Console convert --in "C:\Reports\Sales.rpt" --preview
CrystalMetadata.Console convert --in Sales.metadata.json --out Sales.frx --picture logo.png --preview
```

(or menu option 7). `CrystalMetadata.FastReport` turns the metadata into a `.frx` for **FastReport Open Source (MIT)**:
page size and margins, bands (title, page header/footer, group header/footer, data band, summary), text/field/line/box
objects at their exact positions, fonts, borders, fills, number and date formats, groups with sort order, special fields
(page N of M, print date), row counters, and running sums (Crystal's `numbervar` accumulate/print/reset trio becomes a
native FastReport *Total*). Simple formulas (`If/Then/Else`, `and/or/not`, `isnull`, `chr`, `Sum/Count/...`, `x/1000` on a
total) are translated; anything else is listed as a warning with the original formula, never guessed.

`--font-scale 0.87` multiplies all font sizes: Crystal's own PDF export draws Arial about 14% narrower than FastReport at the
same nominal size (measured against a real Crystal export), so 0.87 makes the result look like the Crystal output. The default
is 1.0 (exact point sizes from the .rpt); layout is unaffected either way.

`--preview` renders the template with the open source engine using **synthetic sample rows** (no real data is read) to
`<name>.preview.pdf` and `<name>.preview.png` so you can check the layout. Known limits: pictures are placeholders unless you
pass `--picture` (the SDK does not expose image bytes); charts, cross-tabs and subreports are skipped with a warning;
multi-table joins must be done in the data source; the open source PDF plug-in rasterises pages (text is not selectable).

## Usage

The console spawns the extractor processes itself (default `min(4, cores/2)` workers) and stops them on exit.

```bash
dotnet src/CrystalMetadata.Console/bin/Release/net10.0/CrystalMetadata.Console.dll extract --in "C:\Reports\SalesRegister.rpt" --out SalesRegister.metadata.json --hints
```

```bash
dotnet src/CrystalMetadata.Console/bin/Release/net10.0/CrystalMetadata.Console.dll batch --in "C:\Reports" --recursive --out out --workers 4 --hints
```

`batch` writes `out/<relative>/<name>.metadata.json` plus `out/_manifest.json` (per report: success, error code,
duration, attempts, counts, input SHA-256). Re-running skips reports whose output exists and whose input hash is unchanged;
`--force` re-extracts everything. Exit codes: 0 ok, 1 failures, 2 usage, 3 server unavailable.

Other commands: `ping` / `info` (server health JSON), `schema --out metadata.schema.json` (JSON Schema of the output).

Switches: `--generated-sql` (ask RAS for the SQL Crystal would generate; off by default because it may attempt a
connection), `--no-raw` (drop the `raw` extension bags), `--no-unused-fields`, `--timeout MS`, `--pipe NAME`,
`--server-exe PATH`, `--attach` (use an already running server, never spawn), `--log-level Debug`.

### Direct mode (no pipe)

Handy for diagnosing a single report or a runtime problem:

```bash
src/CrystalMetadata.PipeServer/bin/Release/net48/CrystalMetadata.PipeServer.exe --extract "C:\Reports\SalesRegister.rpt" --out SalesRegister.json
```

### Running the extractor as a Windows service

```
sc create CrystalMetadata binPath= "C:\CrystalMetadata\server\CrystalMetadata.PipeServer.exe --pipe CrystalMetadataPipe" start= auto
```

(or NSSM). Clients then use `--attach --pipe CrystalMetadataPipe`. A service-hosted server is not recycled by the
client; it exits with code 10 after `MaxReportsPerProcess` reports and must be configured to restart automatically
(`sc failure CrystalMetadata reset= 0 actions= restart/1000`). Grant clients running under other accounts access with
`Server:AllowedSids` and set `Client:CurrentUserOnly=false`.

### Using the client library

```csharp
services.AddCrystalMetadataClient(configuration);          // binds the "Client" section
var client = provider.GetRequiredService<ICrystalMetadataClient>();
var result = await client.ExtractAsync(@"C:\Reports\SalesRegister.rpt");
if (result.Success) { LayoutHints.Apply(result.Metadata!); /* hand result.Metadata to IReportConverter */ }
```

## Output

One `ReportMetadata` document per report — see [docs/metadata-schema.md](docs/metadata-schema.md) for every property, its
SDK origin and when it is `null`. Highlights:

- `reportInfo` (file, title, saved-data flag, **record selection formula** verbatim, file version)
- `pageSetup` (orientation, paper, margins, printable area — twips)
- `dataSource` (connections without passwords, tables with kind Table/StoredProcedure/Command + `commandText`, fields with `useCount`, joins)
- `parameters` (kind, discrete/range, defaults, `isLinkedFromParentReport` + `linkedSourceHint` for `Pm-…` parameters)
- `formulas` (text **verbatim**, syntax), `sqlExpressions`, `customFunctions`, `summaryFields`, `runningTotals`
- `groups` (condition field, direction, options), `sortFields`
- `areas` and `sections` in print order, each object with twips + points, format, border, font, kind-specific data
  (field data source & number/date format, text paragraphs with embedded `{PageNumber}`-style fields, lines, boxes,
  pictures, subreport placeholders), and **conditional-format formulas** from RAS
- `subreports[]` with placement, **parent→child links**, and the full metadata of the subreport
- `derived` (optional, client-computed): Details fields paired with their header text → column list, band summary
- `diagnostics[]` (code, path, message) and `stats`

Units: positions/sizes in twips (1440/inch, 20/point); `*Points` values are provided next to every twips value.
Colours are `#RRGGBB`. Enums are the SDK's enum names. `null` = "Crystal does not expose this".

## Behaviour you should know about

- **No data is ever read.** The extractor never calls `Refresh`, `SetDatabaseLogon`, `VerifyDatabase`, export or print,
  and never connects to a database. Reports load fine without the source database being reachable.
- **Nested subreports do not exist in Crystal.** `subreports[].report.subreports` is always empty; the code is recursive anyway.
- **Process hygiene.** Crystal's runtime leaks and enforces a 75-document print-job limit, so each server processes one
  report at a time on an STA thread and exits with code 10 after `MaxReportsPerProcess` (default 200) reports or after a
  timeout; the client restarts it and retries the affected report once. Scale with more processes (`--workers`), never threads.
- **Load failures are classified**, never fatal: `FILE_NOT_FOUND`, `ACCESS_DENIED`, `LOAD_FAILED` (corrupt / not a report),
  `JRC_REPORT` (saved by the Java runtime), `DB_DRIVER_MISSING`, `PRINT_JOB_LIMIT`, `TIMEOUT`, `SERVER_CRASHED`,
  `SERVER_UNAVAILABLE`, `PROTOCOL_ERROR`, `INTERNAL`. `success=true` with diagnostics means a partial extraction — normal for
  real-world files; check `stats.warningCount`.
- **`app.config` matters.** Every Crystal-hosting exe carries `useLegacyV2RuntimeActivationPolicy="true"`; without it the
  runtime fails to load mixed-mode assemblies.
- **Temp files.** Reports are opened with `OpenReportByTempCopy` (Crystal makes its own copy in `%TEMP%`; the source is not
  locked). The server writes output files itself only when started with `--allow-output-path` and the request sets `outputPath`.
- **`Pm-` convention.** Crystal names parameters fed by the parent report `Pm-Table.Field`, `Pm-@Formula` or `Pm-?Param`.
  Those appear in the subreport's `parameters[]` with `isLinkedFromParentReport=true`, and in the main report's
  `parameters[]` with `reportName` set to the subreport. `subreports[].links[]` is the authoritative mapping.

## Configuration

Server (`src/CrystalMetadata.PipeServer/appsettings.json`, env prefix `CRYSTALMETA_`, e.g. `CRYSTALMETA_Server__PipeName`):
`Server:PipeName`, `MaxReportsPerProcess`, `RequestTimeoutMs`, `MaxFrameBytes`, `AllowOutputPath`, `AllowedSids[]`,
`LogDirectory`; `Extraction:IncludeUnusedFields|IncludeRawBags|IncludeGeneratedSql`; `Logging:MinimumLevel`.
Command line overrides: `--pipe`, `--max-reports`, `--timeout`, `--once`, `--allow-output-path`, `--log-dir`, `--log-level`.

Client/console (`src/CrystalMetadata.Console/appsettings.json`): `Client:PipeName`, `ServerExePath`, `WorkerCount`,
`AttachToExistingServer`, `ConnectTimeoutMs`, `RequestTimeoutMs`, `ServerStartTimeoutMs`, `MaxReportsPerProcess`,
`CurrentUserOnly`, `ServerExtraArgs`; `Batch:OutputDirectory`, `Batch:Hints`; `Logging:MinimumLevel|LogDirectory`.

Logs: Serilog, console + rolling compact-JSON files under `logs/` next to each executable (`pipeserver-*.json`,
`console-*.json`), with `RequestId` and `ReportPath` scopes.

## Pipe protocol

Pipe `\\.\pipe\CrystalMetadataPipe` (workers: `CrystalMetadataPipe.0`, `.1`, …), byte mode, one request per connection,
frames = `int32 little-endian length` + UTF-8 JSON, max 256 MB. Server ACL: current user (+ `AllowedSids`).

Request (all members optional except `reportPath` for `extract`; `{ "reportPath": "…" }` alone is valid):

```json
{ "protocolVersion": 1, "requestId": "guid", "command": "extract | ping | info | shutdown",
  "reportPath": "C:\\Reports\\SalesRegister.rpt",
  "options": { "includeUnusedFields": true, "includeRawBags": true, "includeGeneratedSql": false, "timeoutMs": 120000, "outputPath": null } }
```

Response:

```json
{ "protocolVersion": 1, "requestId": "guid", "success": true, "error": null,
  "metadata": { "...ReportMetadata..." }, "diagnostics": [], "outputPath": null,
  "server": { "processId": 1234, "bitness": "x64", "crystalRuntimeVersion": "13.0.2000.0 / RAS 13.0.2000.0", "reportsProcessed": 12, "uptimeSeconds": 80, "recycleAfter": 200, "recyclePending": false },
  "durationMs": 1830 }
```

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Server exits with code 3, "Crystal Reports runtime assembly not found" | Install the **64-bit** runtime MSI; check `C:\Windows\assembly\GAC_64\CrystalDecisions.ReportAppServer.ClientDoc`. |
| `BadImageFormatException` | 32-bit runtime with the x64 build (or vice versa). |
| "Unsupported Operation. A document processed by the JRC engine…" (`JRC_REPORT`) | Report saved by the Java runtime; open and re-save it in Crystal Reports Designer. |
| "Failed to load database information" (`DB_DRIVER_MISSING`) | The report references a driver (e.g. `crdb_oracle.dll`) not installed with the runtime; the rest of the metadata is still extracted. |
| `TIMEOUT` on a report | Raise `--timeout`; the worker is recycled automatically. Very large reports with saved data take longer to open. |
| `ACCESS_DENIED` connecting to the pipe (attach mode) | Server runs under another account: add the client's SID to `Server:AllowedSids` and set `Client:CurrentUserOnly=false`. |
| Growing memory in a long-running service | Expected; lower `MaxReportsPerProcess` so it recycles more often. |
