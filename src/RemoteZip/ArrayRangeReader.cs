/// <summary>Serves ranges from an in-memory copy of the whole archive.</summary>
sealed class ArrayRangeReader(byte[] data) : IRangeReader
{
    public Task<byte[]> Read(long offset, long length, Cancel cancel)
    {
        var result = new byte[length];
        Array.Copy(data, offset, result, 0, (int) length);
        return Task.FromResult(result);
    }
}