interface IRangeReader
{
    Task<byte[]> Read(long offset, long length, Cancel cancel);
}