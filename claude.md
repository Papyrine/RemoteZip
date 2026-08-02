# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

```pwsh
dotnet build src --configuration Release
dotnet run --project src/RemoteZip.Tests --configuration Release --no-build
```

Filter to a single test (TUnit uses `--treenode-filter`, not `--filter`):

```pwsh
dotnet run --project src/RemoteZip.Tests --configuration Release --no-build -- --treenode-filter '/*/*/RemoteZipArchiveTests/RangedRead_RoundTrips'
```

`LiveNugetTests` hits api.nuget.org for real; its assertions are deliberately resilient because the CDN occasionally answers a valid range request with 200 + full body (cold edge cache). Don't tighten them to exact request counts.

## What this library is

One public type, `RemoteZipArchive`: reads a zip over HTTP range requests without downloading the whole file. Open = one suffix request for the tail → parse end-of-central-directory (EOCD) → parse central directory (fetched separately only if bigger than the tail). Read = one range request per entry (`local header + name + extra-slack + compressed data`), decompress, crc-check. Batched reads coalesce entries whose ranges are within 8 KiB.

Design constraints that are not obvious from the code alone:

- **Everything must be async.** The primary consumer is Blazor WASM, where sync-over-async deadlocks. This is why the zip parsing is hand-rolled instead of feeding `ZipArchive` a seekable stream adapter.
- **`Content-Range` cannot be relied on.** CORS hides it from browser callers unless the server exposes it (nuget.org doesn't). Absolute offsets are derived from the EOCD instead: for a well-formed zip, `EOCD position == cdOffset + cdSize`, which anchors the tail buffer's absolute position; the central-directory signature check validates the assumption. When `Content-Range` *is* present it is preferred. Same trick via the zip64 record for zip64 archives.
- **A 200 instead of a 206 is not an error.** nuget.org's CDN was observed serving a full body for a valid mid-file range (cold edge). `HttpRangeReader` skips to the requested window on 200; `Open` treats 200 as the full-download fallback (bounded by `MaxBufferLength`).
- **Local headers lie.** The central directory is authoritative for sizes/crc (also covers data-descriptor archives); local extra fields can differ in length from central ones, hence the 512-byte over-fetch slack and the exact re-fetch fallback in `Extract`.

## Test infrastructure

- `StubZipServer` mimics nuget.org's observed behavior (206 slices, suffix ranges, 200-with-full-body for unsatisfiable ranges). Toggles: `SupportRanges` (range-less servers), `ExposeContentRange` (browser CORS simulation).
- `Zips.Padded` + `TailLength = 1024` is the pattern for forcing the ranged path; small zips otherwise fit entirely inside the default 128 KiB tail (`DownloadedWholeFile == true`) and reads cost zero requests.
- `ZipBuilder` hand-writes zip bytes for what `ZipArchive` can't produce: archive comments, oversized local extras, encrypted/unsupported-method flags, wrong crcs, zip64 records.

## Conventions

- `ProjectDefaults` (nuget) supplies packing metadata, signing (`src/key.snk`), and on every build **overwrites** the root `.editorconfig` and `src/Shared.sln.DotSettings` from the package — don't hand-edit those.
- CPM (`src/Directory.Packages.props`). SponsorCheck metadata lives on its `PackageVersion` line; the bundler runs on Release builds and needs a GitHub token (`GitHubToken` env var, or user-secrets `SponsorCheck:GitHubToken` under the `UserSecretsId` in `RemoteZip.csproj`).
- `readme.md` / `nuget.md` are processed by MarkdownSnippets on test-project builds; `snippet:`/`<!-- snippet: -->` blocks are regenerated from `Usage.cs` — edit the source, not the expanded block.
- Multi-targets `netstandard2.0;net8.0;net10.0` with Polyfill; keep new code compatible with all three (no `System.Memory`-only APIs without a Polyfill equivalent).
