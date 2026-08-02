/// <summary>Serves ranges from an in-memory copy of the whole archive.</summary>
sealed class ArrayRangeReader(byte[] data) : IRangeReader
{
    public Task<ReadOnlyMemory<byte>> Read(long offset, long length, Cancel cancel) =>
        Task.FromResult<ReadOnlyMemory<byte>>(data.AsMemory((int) offset, (int) length));
}
