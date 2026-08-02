/// <summary>
/// Hand-writes zip bytes for cases <see cref="System.IO.Compression.ZipArchive" /> cannot
/// produce: archive comments, oversized local extra fields, encrypted/unsupported-method
/// flags, wrong crcs, and zip64 records.
/// </summary>
class ZipBuilder
{
    sealed record BuilderEntry(string Name, byte[] Content, ushort Method, int LocalExtraLength, ushort Flags, uint? CrcOverride);

    readonly List<BuilderEntry> entries = [];

    public string Comment { get; set; } = "";

    public bool Zip64 { get; set; }

    public ZipBuilder Add(string name, string content, ushort method = 8, int localExtraLength = 0, ushort flags = 0, uint? crcOverride = null)
    {
        entries.Add(new(name, Encoding.UTF8.GetBytes(content), method, localExtraLength, flags, crcOverride));
        return this;
    }

    public ZipBuilder Add(string name, byte[] content, ushort method = 8, int localExtraLength = 0, ushort flags = 0, uint? crcOverride = null)
    {
        entries.Add(new(name, content, method, localExtraLength, flags, crcOverride));
        return this;
    }

    public byte[] Build()
    {
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        var records = new List<(BuilderEntry Entry, long Offset, byte[] Data, uint Crc, byte[] Name)>();

        foreach (var entry in entries)
        {
            var crc = entry.CrcOverride ?? Crc32.HashToUInt32(entry.Content);
            var data = entry.Method == 8 ? Deflate(entry.Content) : entry.Content;
            var name = Encoding.UTF8.GetBytes(entry.Name);
            records.Add((entry, stream.Position, data, crc, name));

            writer.Write(0x04034b50u);
            writer.Write((ushort) 20);
            writer.Write(entry.Flags);
            writer.Write(entry.Method);
            writer.Write((ushort) 0);
            writer.Write((ushort) 0);
            writer.Write(crc);
            writer.Write((uint) data.Length);
            writer.Write((uint) entry.Content.Length);
            writer.Write((ushort) name.Length);
            writer.Write((ushort) entry.LocalExtraLength);
            writer.Write(name);
            writer.Write(new byte[entry.LocalExtraLength]);
            writer.Write(data);
        }

        var directoryOffset = stream.Position;
        foreach (var (entry, offset, data, crc, name) in records)
        {
            writer.Write(0x02014b50u);
            writer.Write((ushort) 20);
            writer.Write((ushort) 20);
            writer.Write(entry.Flags);
            writer.Write(entry.Method);
            writer.Write((ushort) 0);
            writer.Write((ushort) 0);
            writer.Write(crc);
            writer.Write((uint) data.Length);
            writer.Write((uint) entry.Content.Length);
            writer.Write((ushort) name.Length);
            writer.Write((ushort) 0);
            writer.Write((ushort) 0);
            writer.Write((ushort) 0);
            writer.Write((ushort) 0);
            writer.Write(0u);
            writer.Write((uint) offset);
            writer.Write(name);
        }

        var directorySize = stream.Position - directoryOffset;

        if (Zip64)
        {
            var recordOffset = stream.Position;
            writer.Write(0x06064b50u);
            writer.Write(44UL);
            writer.Write((ushort) 45);
            writer.Write((ushort) 45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ulong) entries.Count);
            writer.Write((ulong) entries.Count);
            writer.Write((ulong) directorySize);
            writer.Write((ulong) directoryOffset);

            writer.Write(0x07064b50u);
            writer.Write(0u);
            writer.Write((ulong) recordOffset);
            writer.Write(1u);
        }

        var comment = Encoding.UTF8.GetBytes(Comment);
        writer.Write(0x06054b50u);
        writer.Write((ushort) 0);
        writer.Write((ushort) 0);
        if (Zip64)
        {
            writer.Write((ushort) 0xFFFF);
            writer.Write((ushort) 0xFFFF);
            writer.Write(0xFFFFFFFFu);
            writer.Write(0xFFFFFFFFu);
        }
        else
        {
            writer.Write((ushort) entries.Count);
            writer.Write((ushort) entries.Count);
            writer.Write((uint) directorySize);
            writer.Write((uint) directoryOffset);
        }

        writer.Write((ushort) comment.Length);
        writer.Write(comment);
        writer.Flush();
        return stream.ToArray();
    }

    static byte[] Deflate(byte[] input)
    {
        var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
        {
            deflate.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }
}
