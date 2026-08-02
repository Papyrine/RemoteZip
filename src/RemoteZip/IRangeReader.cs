/// <summary>
/// Serves a byte range of the archive. Returns memory rather than an array so a reader that
/// already holds the bytes can hand back a slice instead of a copy.
/// </summary>
interface IRangeReader
{
    Task<ReadOnlyMemory<byte>> Read(long offset, long length, Cancel cancel);
}
