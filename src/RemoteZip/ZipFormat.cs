/// <summary>
/// Little-endian readers and record layouts for the zip format (PKWARE APPNOTE 4.3.x).
/// All offsets here mirror the spec; the central directory — not local headers — is the
/// source of truth for sizes and crc, so data-descriptor archives (flag bit 3) need no
/// special handling.
/// </summary>
static class ZipFormat
{
    public const uint LocalHeaderSignature = 0x04034b50;
    public const uint CentralDirectorySignature = 0x02014b50;
    public const uint EndOfCentralDirectorySignature = 0x06054b50;
    public const uint Zip64LocatorSignature = 0x07064b50;
    public const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;

    public const int LocalHeaderLength = 30;
    public const int EndOfCentralDirectoryLength = 22;
    public const int Zip64LocatorLength = 20;

    public static ushort UInt16(ReadOnlySpan<byte> buffer, int offset) =>
        (ushort) (buffer[offset] | buffer[offset + 1] << 8);

    public static uint UInt32(ReadOnlySpan<byte> buffer, int offset) =>
        (uint) (buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);

    public static ulong UInt64(ReadOnlySpan<byte> buffer, int offset) =>
        UInt32(buffer, offset) | (ulong) UInt32(buffer, offset + 4) << 32;

    /// <summary>
    /// Locates the end-of-central-directory record in a buffer whose last byte is the last
    /// byte of the file. Scans backward to tolerate an archive comment; a candidate only
    /// counts when its comment-length field reaches exactly to the end of the buffer, which
    /// rules out the signature bytes appearing inside comment or entry data.
    /// </summary>
    public static int FindEndOfCentralDirectory(ReadOnlySpan<byte> tail)
    {
        for (var index = tail.Length - EndOfCentralDirectoryLength; index >= 0; index--)
        {
            if (UInt32(tail, index) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = UInt16(tail, index + 20);
            if (index + EndOfCentralDirectoryLength + commentLength == tail.Length)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Scans backward from <paramref name="before" /> for the zip64 end-of-central-directory
    /// record signature.
    /// </summary>
    public static int FindZip64Record(ReadOnlySpan<byte> tail, int before)
    {
        for (var index = before - 4; index >= 0; index--)
        {
            if (UInt32(tail, index) == Zip64EndOfCentralDirectorySignature)
            {
                return index;
            }
        }

        return -1;
    }
}
