/// <summary>
/// Serves reads from the tail buffer already downloaded when the archive was opened, falling
/// back to HTTP for anything outside it. The tail covers the end of the file, so entries
/// stored just before the central directory are read without another request.
/// </summary>
sealed class TailCachedRangeReader(IRangeReader inner, byte[] tail, long tailStart) : IRangeReader
{
    public Task<ReadOnlyMemory<byte>> Read(long offset, long length, Cancel cancel)
    {
        if (offset >= tailStart &&
            offset + length <= tailStart + tail.Length)
        {
            return Task.FromResult<ReadOnlyMemory<byte>>(tail.AsMemory((int) (offset - tailStart), (int) length));
        }

        return inner.Read(offset, length, cancel);
    }
}
