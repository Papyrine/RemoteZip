static class Zips
{
    public static byte[] Normal(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                if (name.EndsWith('/'))
                {
                    continue;
                }

                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// A zip whose last entry is incompressible padding, so with a small TailLength the
    /// preceding entries sit outside the tail fetched when opening and reads have to go
    /// back to the server. Padding first would put them *inside* the tail, where
    /// <c>TailCachedRangeReader</c> serves them for free and the ranged path goes untested.
    /// </summary>
    public static byte[] Padded(int paddingLength, params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }

            var padding = archive.CreateEntry("padding.bin", CompressionLevel.NoCompression);
            using var paddingStream = padding.Open();
            paddingStream.Write(RandomBytes(paddingLength));
        }

        return stream.ToArray();
    }

    public static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    public static (HttpClient Client, StubZipServer Server) Serve(byte[] data)
    {
        var server = new StubZipServer(data);
        return (new(server), server);
    }
}
