public class NameKeyedReadTests
{
    static readonly RemoteZipOptions rangedOptions = new()
    {
        TailLength = 1024
    };

    [Test]
    public async Task ReadByNames_MissingNamesAbsent_SingleCoalescedRequest()
    {
        var data = Zips.Padded(4096, ("docs/a.txt", "alpha"), ("docs/b.txt", "beta"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var contents = await zip.Read(["docs/a.txt", "docs/b.txt", "missing.txt"]);

            await Assert.That(contents.Count).IsEqualTo(2);
            await Assert.That(Encoding.UTF8.GetString(contents["docs/a.txt"])).IsEqualTo("alpha");
            await Assert.That(Encoding.UTF8.GetString(contents["docs/b.txt"])).IsEqualTo("beta");
            await Assert.That(contents.ContainsKey("missing.txt")).IsFalse();
            await Assert.That(server.Requests).Count().IsEqualTo(2);
        }
    }

    [Test]
    public async Task ReadByNames_DuplicateNamesCollapse()
    {
        var data = Zips.Padded(4096, ("a.txt", "alpha"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var contents = await zip.Read(["a.txt", "a.txt"]);

            await Assert.That(contents.Count).IsEqualTo(1);
            await Assert.That(Encoding.UTF8.GetString(contents["a.txt"])).IsEqualTo("alpha");
        }
    }

    [Test]
    public async Task ReadTextByNames_HonorsByteOrderMark()
    {
        var data = Zips.Padded(4096, ("bom.txt", "﻿hi"), ("plain.txt", "there"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var texts = await zip.ReadText(["bom.txt", "plain.txt", "missing.txt"]);

            await Assert.That(texts.Count).IsEqualTo(2);
            await Assert.That(texts["bom.txt"]).IsEqualTo("hi");
            await Assert.That(texts["plain.txt"]).IsEqualTo("there");
        }
    }

    [Test]
    public async Task ReadTextByEntries_KeyedByEntry()
    {
        var data = Zips.Padded(4096, ("a.txt", "alpha"), ("b.txt", "beta"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var a = zip.Find("a.txt")!;
            var b = zip.Find("b.txt")!;
            var texts = await zip.ReadText([a, b]);

            await Assert.That(texts[a]).IsEqualTo("alpha");
            await Assert.That(texts[b]).IsEqualTo("beta");
        }
    }

    [Test]
    public async Task ReadByNames_EmptyInput_NoRequests()
    {
        var data = Zips.Padded(4096, ("a.txt", "alpha"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var contents = await zip.Read(Array.Empty<string>());

            await Assert.That(contents.Count).IsEqualTo(0);
            await Assert.That(server.Requests).Count().IsEqualTo(1);
        }
    }
}
