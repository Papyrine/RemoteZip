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
    /// A zip whose first entry is incompressible padding, so with a small TailLength the
    /// remaining entries sit outside the tail and reads exercise the ranged path.
    /// </summary>
    public static byte[] Padded(int paddingLength, params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var padding = archive.CreateEntry("padding.bin", CompressionLevel.NoCompression);
            using (var paddingStream = padding.Open())
            {
                paddingStream.Write(RandomBytes(paddingLength));
            }

            foreach (var (name, content) in files)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entryStream.Write(bytes, 0, bytes.Length);
            }
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
