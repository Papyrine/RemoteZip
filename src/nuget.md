# RemoteZip

Read entries from a remote zip over HTTP range requests, without downloading the whole archive.

A zip's table of contents lives at the end of the file, and every entry is compressed independently: listing a remote archive costs one request for the file tail, and reading an entry costs one request sized to that entry. Async all the way down, so it works from Blazor WebAssembly — including against nuget.org's flat container, whose CORS policy allows range requests from the browser.

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

Servers without range support degrade transparently to a bounded full download. Zip64 is supported; every read is crc-validated.

See the [project documentation](https://github.com/Papyrine/RemoteZip) for how it works, options, and server requirements.
