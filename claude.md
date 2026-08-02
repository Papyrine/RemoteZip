# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & test

```pwsh
dotnet build src --configuration Release
dotnet run --project src/RemoteZip.Tests --configuration Release --no-build
```

Release builds run the SponsorCheck bundler, which fails with `SC102` when no GitHub token is configured (see Conventions). Swap in `--configuration Debug` when that is the case — the compile and the tests are the same either way.

Filter to a single test (TUnit uses `--treenode-filter`, not `--filter`):

```pwsh
dotnet run --project src/RemoteZip.Tests --configuration Release --no-build -- --treenode-filter '/*/*/RemoteZipArchiveTests/RangedRead_RoundTrips'
```

`LiveNugetTests` hits api.nuget.org for real; its assertions are deliberately resilient because the CDN occasionally answers a valid range request with 200 + full body (cold edge cache). Don't tighten them to exact request counts.

## What this library is

Two public types, `RemoteZipArchive` and `StubZipServer` (a test double for consumers, shipped in the same package — it depends on nothing beyond `HttpMessageHandler` and trims away when unreferenced, so a separate package would cost more than it saves): reads a zip over HTTP range requests without downloading the whole file. Batched reads come in entry-keyed and name-keyed forms (`Read`/`ReadText` over `IReadOnlyCollection` of entries or names); name-keyed results omit missing names rather than erroring, so callers probe optional files without existence checks. Open = one suffix request for the tail → parse end-of-central-directory (EOCD) → parse central directory (fetched separately only if bigger than the tail). Read = one range request per entry (`local header + name + extra-slack + compressed data`), decompress, crc-check — or no request at all when the entry falls inside the retained tail. Batched reads coalesce entries whose ranges are within 8 KiB and issue the clusters that remain concurrently, bounded by `MaxConcurrency`.

Design constraints that are not obvious from the code alone:

- **Everything must be async.** The primary consumer is Blazor WASM, where sync-over-async deadlocks. This is why the zip parsing is hand-rolled instead of feeding `ZipArchive` a seekable stream adapter.
- **`Content-Range` cannot be relied on.** CORS hides it from browser callers unless the server exposes it (nuget.org doesn't). Absolute offsets are derived from the EOCD instead: for a well-formed zip, `EOCD position == cdOffset + cdSize`, which anchors the tail buffer's absolute position; the central-directory signature check validates the assumption. When `Content-Range` *is* present it is preferred. Same trick via the zip64 record for zip64 archives.
- **A 200 instead of a 206 is not an error.** nuget.org's CDN was observed serving a full body for a valid mid-file range (cold edge). `HttpRangeReader` skips to the requested window on 200; `Open` treats 200 as the full-download fallback (bounded by `MaxBufferLength`).
- **Local headers lie.** The central directory is authoritative for sizes/crc (also covers data-descriptor archives); local extra fields can differ in length from central ones, hence the 512-byte over-fetch slack and the exact re-fetch fallback in `Extract`.
- **Round trips are the only cost that matters.** Everything else — copies, allocations, parsing — is microseconds against a request measured in milliseconds, so optimise for requests avoided, not bytes moved. Two mechanisms do that: `TailCachedRangeReader` keeps the tail downloaded at open and serves any read falling inside it for free (an entry stored just before the central directory costs nothing), and batched reads plan every cluster up front in `BuildClusters` so the requests can overlap instead of running serially. The tail costs `TailLength` bytes of retained memory for the archive's lifetime, which is the deliberate trade.
- **Fetched bytes are sliced, never copied.** `IRangeReader` returns `ReadOnlyMemory<byte>` so `ArrayRangeReader` can hand back a window onto the archive it already holds, and `Extract` can slice an entry's compressed payload out of the fetched buffer — which in a batched read covers a whole coalesced cluster. `ZipFormat` takes `ReadOnlySpan<byte>` for the same reason. This is about allocation pressure under the WASM GC, not throughput: network latency dominates everything here. The public `Read` deliberately still returns `byte[]` — callers want an array, and `Inflate` allocates the result fresh regardless. `DeflateStream` needs a `Stream`, so `AsStream` recovers the backing array via `MemoryMarshal.TryGetArray` rather than copying into a new `MemoryStream`.

## Test infrastructure

- `StubZipServer` (shipped public in `src/RemoteZip`, so the tests dogfood what consumers get) mimics nuget.org's observed behavior (206 slices, suffix ranges, 200-with-full-body for unsatisfiable ranges). Toggles: `SupportRanges` (range-less servers), `ExposeContentRange` (browser CORS simulation), `Delay` (needed before `MaxConcurrentRequests` can observe overlap — without it each response completes before the next request is issued). Batched reads hit the handler concurrently, so its logs are lock-guarded and their *order* is not meaningful; assert on counts.
- `Zips.Padded` + `TailLength = 1024` is the pattern for forcing the ranged path; small zips otherwise fit entirely inside the default 128 KiB tail (`DownloadedWholeFile == true`) and reads cost zero requests.
- **Padding goes last in a test zip, not first.** Since `TailCachedRangeReader` serves anything inside the tail for free, an entry written near the end of the file is read without a request. A fixture that pads first pushes its interesting entries to the end and silently stops testing the ranged path — it will pass, with a lower request count than the test asserts. `Zips.Padded` and the `ZipBuilder` fixtures all append `padding.bin` for this reason; `EntryInsideTail_ReadsWithoutAnotherRequest` is the one test that deliberately inverts it.
- `ZipBuilder` hand-writes zip bytes for what `ZipArchive` can't produce: archive comments, oversized local extras, encrypted/unsupported-method flags, wrong crcs, zip64 records.

## Conventions

- `ProjectDefaults` (nuget) supplies packing metadata, signing (`src/key.snk`), and on every build **overwrites** the root `.editorconfig` and `src/Shared.sln.DotSettings` from the package — don't hand-edit those.
- CPM (`src/Directory.Packages.props`). SponsorCheck metadata lives on its `PackageVersion` line; the bundler runs on Release builds and needs a GitHub token (`GitHubToken` env var, or user-secrets `SponsorCheck:GitHubToken` under the `UserSecretsId` in `RemoteZip.csproj`).
- `readme.md` / `nuget.md` are processed by MarkdownSnippets on test-project builds; `snippet:`/`<!-- snippet: -->` blocks are regenerated from `Usage.cs` — edit the source, not the expanded block.
- Single-target `net10.0`, `IsAotCompatible`. No Polyfill package — spans, `Memory<T>` and the rest of the modern BCL are in-box, so use them freely. `Cancel` / `CancelSource` are global aliases for `CancellationToken` / `CancellationTokenSource`, generated by `ProjectDefaults` along with the other global usings; per-project ones are `<Using Include="..." />` items in `RemoteZip.csproj`.
