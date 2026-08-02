# <img src="/src/icon.png" height="30px"> RemoteZip

[![Build status](https://img.shields.io/appveyor/build/SimonCropp/RemoteZip)](https://ci.appveyor.com/project/SimonCropp/RemoteZip)
[![NuGet Status](https://img.shields.io/nuget/v/RemoteZip.svg)](https://www.nuget.org/packages/RemoteZip/)

Read entries from a remote zip over HTTP range requests, without downloading the whole archive.

A zip's table of contents (the central directory) lives at the end of the file, and every entry is compressed independently. So listing a remote archive costs one request for the file tail, and reading an entry costs one request sized to that entry — a few KB fetched from a multi-MB archive. Useful for any zip-shaped format: nupkg, vsix, jar, apk, docx, epub.

Async all the way down, so it works from Blazor WebAssembly — the original motivation was inspecting NuGet packages client-side in the browser against nuget.org's flat container, which supports both range requests and the CORS preflight for them.

**See [Milestones](../../milestones?state=closed) for release notes.**


## NuGet package

https://nuget.org/packages/RemoteZip/


## Usage

<!-- snippet: usage -->
<a id='snippet-usage'></a>
```cs
public static async Task PrintRemoteZip(HttpClient http, string url)
{
    var zip = await RemoteZipArchive.Open(http, url);

    foreach (var entry in zip.Entries)
    {
        Console.WriteLine($"{entry.FullName} ({entry.Length} bytes)");
    }

    var readme = zip.Find("readme.md");
    if (readme != null)
    {
        Console.WriteLine(await zip.ReadText(readme));
    }
}
```
<sup><a href='/src/RemoteZip.Tests/Usage.cs#L3-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Reading multiple entries

Entries that sit close together in the archive coalesce into a single request:

<!-- snippet: batch-read -->
<a id='snippet-batch-read'></a>
```cs
public static async Task<string?> ReadNuspec(HttpClient http, string packageUrl)
{
    var zip = await RemoteZipArchive.Open(http, packageUrl);

    // Entries close together in the archive are fetched in one request.
    var wanted = zip.Entries
        .Where(_ => _.FullName.EndsWith(".nuspec") || _.FullName.StartsWith("build/"))
        .ToList();
    var contents = await zip.Read(wanted);

    var nuspec = wanted.FirstOrDefault(_ => _.FullName.EndsWith(".nuspec"));
    return nuspec == null ? null : Encoding.UTF8.GetString(contents[nuspec]);
}
```
<sup><a href='/src/RemoteZip.Tests/Usage.cs#L21-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-batch-read' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## How it works

Opening sends one suffix range request (`Range: bytes=-131072`) and parses the end-of-central-directory record from it. For typical archives the whole central directory is inside that tail, so enumeration costs exactly one request; an oversized central directory costs one more. Each `Read` then fetches `local header + entry data` in one range request, over-fetching by 512 bytes to cover local extra fields; a batched `Read` merges entries whose ranges are within 8 KB of each other.

Browsers add a wrinkle: CORS hides `Content-Range` unless the server explicitly exposes it (nuget.org does not), so the file's total length cannot be read from the response. RemoteZip instead derives every absolute offset from the end-of-central-directory record itself and validates the result against the central directory signature — no `Content-Range`, `HEAD` request, or `Content-Length` needed.

Zip64 archives are supported. Entries must be stored or deflated (the only methods that appear in practice); every read is crc-validated.

## Fallback behavior

Servers that ignore range requests (plain `200` responses) degrade transparently: the archive is buffered in full — bounded by `MaxBufferLength` — and reads are served from memory. The same happens when the file is smaller than the tail request. `DownloadedWholeFile` reports which mode an archive ended up in. A server answering a *valid* range request with `200` mid-session (observed on nuget.org's CDN for cold edge caches) is also handled per request.


## Options

| Option | Default | Purpose |
|---|---|---|
| `TailLength` | 128 KiB | Size of the opening suffix request. The default always contains the end-of-central-directory record and, for typical archives, the whole central directory. |
| `MaxBufferLength` | 1 GiB | Cap on full-download fallback and on any single entry read. |
| `ConfigureRequest` | — | Applied to every outgoing request. |


### Blazor WebAssembly

The browser HTTP cache can answer one range request with the cached body of another. Opt ranged requests out of the cache:

```csharp
var options = new RemoteZipOptions
{
    ConfigureRequest = request => request.SetBrowserRequestCache(BrowserRequestCache.NoStore)
};
```


## Server requirements

The server must support `Range` requests (`206 Partial Content`). For browser use it must also allow the `Range` header in its CORS policy (`Access-Control-Allow-Headers: range`). Exposing `Content-Range` is *not* required. nuget.org's flat container satisfies all of this, including from `localhost` origins.


## Limitations

- Encrypted entries and compression methods other than stored/deflate throw.
- Archives with data prepended to the zip (self-extracting layouts) are rejected.
- Entry names are decoded as UTF-8 regardless of the cp437 flag; non-ASCII names in ancient archives may decode differently than intended.
