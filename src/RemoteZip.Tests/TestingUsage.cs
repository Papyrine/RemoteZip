public class TestingUsage
{
    // begin-snippet: stub-zip-server
    [Test]
    public async Task StubServesRanges()
    {
        var server = new StubZipServer(SampleZipBytes());
        using var client = new HttpClient(server);

        var archive = await RemoteZipArchive.Open(client, "https://example/archive.zip");
        var text = await archive.ReadText(archive.Find("readme.md")!);

        await Assert.That(text).IsEqualTo("# Sample");
        // Requests, HeaderLog, BytesServed and MaxConcurrentRequests record the traffic.
        await Assert.That(server.Requests).Count().IsEqualTo(1);
    }
    // end-snippet

    static byte[] SampleZipBytes() =>
        Zips.Normal(("readme.md", "# Sample"));
}
