namespace RemoteZip;

/// <summary>An entry parsed from the remote archive's central directory.</summary>
public sealed class RemoteZipEntry
{
    internal RemoteZipEntry(
        string fullName,
        long length,
        long compressedLength,
        long localHeaderOffset,
        ushort method,
        ushort flags,
        uint crc,
        int nameByteCount)
    {
        FullName = fullName;
        Length = length;
        CompressedLength = compressedLength;
        LocalHeaderOffset = localHeaderOffset;
        Method = method;
        Flags = flags;
        Crc = crc;
        NameByteCount = nameByteCount;
    }

    /// <summary>The relative path of the entry within the archive, using forward slashes.</summary>
    public string FullName { get; }

    /// <summary>Uncompressed size in bytes.</summary>
    public long Length { get; }

    /// <summary>Compressed size in bytes — what a read of this entry downloads.</summary>
    public long CompressedLength { get; }

    public bool IsDirectory => FullName.EndsWith('/');

    internal long LocalHeaderOffset { get; }

    internal ushort Method { get; }

    internal ushort Flags { get; }

    internal uint Crc { get; }

    internal int NameByteCount { get; }

    public override string ToString() =>
        FullName;
}
