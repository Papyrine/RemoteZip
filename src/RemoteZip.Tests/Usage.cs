public class Usage
{
    // begin-snippet: usage
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
    // end-snippet

    // begin-snippet: batch-read
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
    // end-snippet

    [Test]
    public async Task Runs()
    {
        var data = Zips.Normal(("readme.md", "# Sample"), ("lib/app.dll", "not really a dll"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            await PrintRemoteZip(client, "https://example/archive.zip");
        }
    }

    [Test]
    public async Task BatchRuns()
    {
        var data = Zips.Normal(
            ("thepackage.nuspec", "<package />"),
            ("build/thepackage.targets", "<Project />"),
            ("lib/app.dll", "not really a dll"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var nuspec = await ReadNuspec(client, "https://example/package.nupkg");
            await Assert.That(nuspec).IsEqualTo("<package />");
        }
    }
}
