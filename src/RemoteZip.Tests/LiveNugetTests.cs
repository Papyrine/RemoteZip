/// <summary>
/// Hits nuget.org's flat container for real. Assertions stay resilient to CDN mood — a
/// cold edge has been observed answering a valid range request with 200 and the full
/// body — so they verify correctness always, and efficiency only loosely.
/// </summary>
public class LiveNugetTests
{
    // Immutable published package: content can never change, so entry names are stable.
    const string url = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

    [Test]
    public async Task NewtonsoftJson_EntriesAndNuspec()
    {
        var counter = new CountingHandler();
        using var client = new HttpClient(counter);
        var zip = await RemoteZipArchive.Open(client, url);

        await Assert.That(zip.FileLength).IsEqualTo(2_441_966);

        var nuspec = zip.Find("Newtonsoft.Json.nuspec");
        await Assert.That(nuspec).IsNotNull();
        var text = await zip.ReadText(nuspec!);
        await Assert.That(text).Contains("<id>Newtonsoft.Json</id>");

        Console.WriteLine($"requests: {counter.Requests}, bytes: {counter.BytesReceived}, whole file: {zip.DownloadedWholeFile}");
        if (!zip.DownloadedWholeFile)
        {
            await Assert.That(counter.BytesReceived).IsLessThan(300_000);
        }

        await Verify(zip.Entries.Select(_ => _.FullName));
    }
}

class CountingHandler() : DelegatingHandler(new HttpClientHandler())
{
    public int Requests { get; private set; }

    public long BytesReceived { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel)
    {
        Requests++;
        var response = await base.SendAsync(request, cancel);
        BytesReceived += response.Content.Headers.ContentLength ?? 0;
        return response;
    }
}
