public class RemoteZipArchiveTests
{
    // Small enough that padded test zips keep their entries outside the tail, exercising
    // the ranged path; big enough to hold the central directory of every test zip.
    static readonly RemoteZipOptions rangedOptions = new()
    {
        TailLength = 1024
    };

    [Test]
    public async Task Entries_SnapshotAndSingleRequest()
    {
        var data = Zips.Normal(
            ("readme.md", "# hi"),
            ("docs/", ""),
            ("docs/a.txt", "aaa"),
            ("lib/net10.0/app.dll", "not really a dll"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            await Assert.That(server.Requests).Count().IsEqualTo(1);
            await Verify(zip.Entries.Select(_ => new
            {
                _.FullName,
                _.Length,
                _.IsDirectory
            }));
        }
    }

    [Test]
    public async Task SmallFile_WholeArchiveInTail_ReadsCostNothing()
    {
        var data = Zips.Normal(("a.txt", "alpha"), ("b.txt", "beta"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            await Assert.That(zip.DownloadedWholeFile).IsTrue();
            var content = await zip.ReadText(zip.Find("b.txt")!);
            await Assert.That(content).IsEqualTo("beta");
            await Assert.That(server.Requests).Count().IsEqualTo(1);
        }
    }

    [Test]
    public async Task RangedRead_RoundTrips()
    {
        var data = Zips.Padded(4096, ("docs/a.txt", "aaa"), ("docs/b.txt", "some content that deflates"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            await Assert.That(zip.DownloadedWholeFile).IsFalse();
            await Assert.That(zip.FileLength).IsEqualTo(data.Length);

            var content = await zip.ReadText(zip.Find("docs/b.txt")!);
            await Assert.That(content).IsEqualTo("some content that deflates");
            await Assert.That(server.Requests).Count().IsEqualTo(2);
        }
    }

    [Test]
    public async Task StoredEntry_RoundTrips()
    {
        var payload = Zips.RandomBytes(2000);
        var data = new ZipBuilder()
            .Add("padding.bin", Zips.RandomBytes(4096), method: 0)
            .Add("stored.bin", payload, method: 0)
            .Build();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var content = await zip.Read(zip.Find("stored.bin")!);
            await Assert.That(content.SequenceEqual(payload)).IsTrue();
        }
    }

    [Test]
    public async Task ReadText_StripsByteOrderMark()
    {
        var data = Zips.Normal(("bom.txt", "﻿hi"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            var content = await zip.ReadText(zip.Find("bom.txt")!);
            await Assert.That(content).IsEqualTo("hi");
        }
    }

    [Test]
    public async Task Find_Missing_ReturnsNull()
    {
        var data = Zips.Normal(("a.txt", "alpha"));
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            await Assert.That(zip.Find("missing.txt")).IsNull();
            await Assert.That(zip.Find("A.TXT")).IsNull();
        }
    }

    [Test]
    public async Task ServerWithoutRangeSupport_FallsBackToFullDownload()
    {
        var data = Zips.Padded(4096, ("docs/a.txt", "aaa"));
        var (client, server) = Zips.Serve(data);
        server.SupportRanges = false;
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            await Assert.That(zip.DownloadedWholeFile).IsTrue();
            await Assert.That(zip.FileLength).IsEqualTo(data.Length);
            var content = await zip.ReadText(zip.Find("docs/a.txt")!);
            await Assert.That(content).IsEqualTo("aaa");
            await Assert.That(server.Requests).Count().IsEqualTo(1);
        }
    }

    [Test]
    public async Task HiddenContentRange_DerivesOffsetsFromDirectory()
    {
        // Browser CORS hides Content-Range (nuget.org does not expose it), so absolute
        // positions must be derived from the end-of-central-directory record alone.
        var data = Zips.Padded(4096, ("docs/a.txt", "aaa"));
        var (client, server) = Zips.Serve(data);
        server.ExposeContentRange = false;
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            await Assert.That(zip.DownloadedWholeFile).IsFalse();
            await Assert.That(zip.FileLength).IsEqualTo(data.Length);
            var content = await zip.ReadText(zip.Find("docs/a.txt")!);
            await Assert.That(content).IsEqualTo("aaa");
        }
    }

    [Test]
    public async Task ArchiveComment_StillFindsDirectory()
    {
        var data = new ZipBuilder
            {
                Comment = "packaged by tooling — comments shift the end-of-central-directory record"
            }
            .Add("a.txt", "alpha")
            .Build();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            var content = await zip.ReadText(zip.Find("a.txt")!);
            await Assert.That(content).IsEqualTo("alpha");
        }
    }

    [Test]
    public async Task DirectoryBiggerThanTail_FetchedSeparately()
    {
        var files = Enumerable.Range(0, 40)
            .Select(_ => ($"folder/file{_:00}.txt", $"content {_}"))
            .ToArray();
        var data = Zips.Padded(4096, files);
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var options = new RemoteZipOptions
            {
                TailLength = 100
            };
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", options);
            await Assert.That(zip.Entries).Count().IsEqualTo(41);
            await Assert.That(server.Requests).Count().IsEqualTo(2);
            var content = await zip.ReadText(zip.Find("folder/file07.txt")!);
            await Assert.That(content).IsEqualTo("content 7");
        }
    }

    [Test]
    public async Task LocalExtraField_WithinSlack_SingleRequestPerRead()
    {
        var data = new ZipBuilder()
            .Add("padding.bin", Zips.RandomBytes(4096), method: 0)
            .Add("target.txt", "payload", localExtraLength: 100)
            .Build();
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var content = await zip.ReadText(zip.Find("target.txt")!);
            await Assert.That(content).IsEqualTo("payload");
            await Assert.That(server.Requests).Count().IsEqualTo(2);
        }
    }

    [Test]
    public async Task LocalExtraField_BeyondSlack_CostsOneExtraRequest()
    {
        var data = new ZipBuilder()
            .Add("padding.bin", Zips.RandomBytes(4096), method: 0)
            .Add("target.txt", "payload", localExtraLength: 600)
            .Build();
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var content = await zip.ReadText(zip.Find("target.txt")!);
            await Assert.That(content).IsEqualTo("payload");
            await Assert.That(server.Requests).Count().IsEqualTo(3);
        }
    }

    [Test]
    public async Task Batch_AdjacentEntries_CoalesceIntoOneRequest()
    {
        var data = Zips.Padded(4096, ("a.txt", "alpha"), ("b.txt", "beta"), ("c.txt", "gamma"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var entries = new[]
            {
                zip.Find("a.txt")!,
                zip.Find("b.txt")!,
                zip.Find("c.txt")!
            };
            var contents = await zip.Read(entries);
            await Assert.That(Encoding.UTF8.GetString(contents[entries[0]])).IsEqualTo("alpha");
            await Assert.That(Encoding.UTF8.GetString(contents[entries[1]])).IsEqualTo("beta");
            await Assert.That(Encoding.UTF8.GetString(contents[entries[2]])).IsEqualTo("gamma");
            await Assert.That(server.Requests).Count().IsEqualTo(2);
        }
    }

    [Test]
    public async Task Batch_DistantEntries_SplitIntoClusters()
    {
        var data = new ZipBuilder()
            .Add("a.txt", "alpha")
            .Add("padding.bin", Zips.RandomBytes(300_000), method: 0)
            .Add("b.txt", "beta")
            .Build();
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            var a = zip.Find("a.txt")!;
            var b = zip.Find("b.txt")!;
            var contents = await zip.Read([a, b]);
            await Assert.That(Encoding.UTF8.GetString(contents[a])).IsEqualTo("alpha");
            await Assert.That(Encoding.UTF8.GetString(contents[b])).IsEqualTo("beta");
            await Assert.That(server.Requests).Count().IsEqualTo(3);
        }
    }

    [Test]
    public async Task Zip64_Reads()
    {
        var data = new ZipBuilder
            {
                Zip64 = true
            }
            .Add("padding.bin", Zips.RandomBytes(4096), method: 0)
            .Add("a.txt", "alpha")
            .Build();
        var (client, server) = Zips.Serve(data);
        server.ExposeContentRange = false;
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", rangedOptions);
            await Assert.That(zip.DownloadedWholeFile).IsFalse();
            await Assert.That(zip.FileLength).IsEqualTo(data.Length);
            var content = await zip.ReadText(zip.Find("a.txt")!);
            await Assert.That(content).IsEqualTo("alpha");
        }
    }

    [Test]
    public async Task EncryptedEntry_Throws()
    {
        var data = new ZipBuilder()
            .Add("secret.txt", "payload", flags: 1)
            .Build();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            var entry = zip.Find("secret.txt")!;
            var exception = await Assert.ThrowsAsync<RemoteZipException>(() => zip.Read(entry));
            await Assert.That(exception!.Message).Contains("encrypted");
        }
    }

    [Test]
    public async Task UnsupportedCompressionMethod_Throws()
    {
        var data = new ZipBuilder()
            .Add("weird.bin", "payload", method: 99)
            .Build();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            var entry = zip.Find("weird.bin")!;
            var exception = await Assert.ThrowsAsync<RemoteZipException>(() => zip.Read(entry));
            await Assert.That(exception!.Message).Contains("method 99");
        }
    }

    [Test]
    public async Task CrcMismatch_Throws()
    {
        var data = new ZipBuilder()
            .Add("corrupt.txt", "payload", crcOverride: 0xDEADBEEF)
            .Build();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            var entry = zip.Find("corrupt.txt")!;
            var exception = await Assert.ThrowsAsync<RemoteZipException>(() => zip.Read(entry));
            await Assert.That(exception!.Message).Contains("crc");
        }
    }

    [Test]
    public async Task FullDownloadFallback_RespectsMaxBufferLength()
    {
        var data = Zips.Padded(10_000, ("a.txt", "alpha"));
        var (client, server) = Zips.Serve(data);
        server.SupportRanges = false;
        using (client)
        {
            var options = new RemoteZipOptions
            {
                MaxBufferLength = 1000
            };
            await Assert.ThrowsAsync<RemoteZipException>(() => RemoteZipArchive.Open(client, "https://example/archive.zip", options));
        }
    }

    [Test]
    public async Task EmptyArchive()
    {
        var data = Zips.Normal();
        var (client, _) = Zips.Serve(data);
        using (client)
        {
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip");
            await Assert.That(zip.Entries).Count().IsEqualTo(0);
        }
    }

    [Test]
    public async Task NotAZip_Throws()
    {
        var (client, _) = Zips.Serve(Zips.RandomBytes(10_000));
        using (client)
        {
            var exception = await Assert.ThrowsAsync<RemoteZipException>(() => RemoteZipArchive.Open(client, "https://example/archive.zip"));
            await Assert.That(exception!.Message).Contains("end-of-central-directory");
        }
    }

    [Test]
    public async Task ConfigureRequest_AppliedToEveryRequest()
    {
        var data = Zips.Padded(4096, ("docs/a.txt", "aaa"));
        var (client, server) = Zips.Serve(data);
        using (client)
        {
            var options = new RemoteZipOptions
            {
                TailLength = 1024,
                ConfigureRequest = _ => _.Headers.Add("x-probe", "1")
            };
            var zip = await RemoteZipArchive.Open(client, "https://example/archive.zip", options);
            await zip.Read(zip.Find("docs/a.txt")!);
            await Assert.That(server.HeaderLog.All(_ => _.Contains("x-probe"))).IsTrue();
            await Assert.That(server.HeaderLog).Count().IsEqualTo(2);
        }
    }
}
