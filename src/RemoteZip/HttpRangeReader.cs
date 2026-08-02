/// <summary>
/// Serves ranges via HTTP. A server may answer a valid range request with 200 and the full
/// body (observed on nuget.org's CDN for edge-cache misses), so that case degrades to
/// skipping to the requested window rather than failing.
/// </summary>
sealed class HttpRangeReader(HttpClient http, Uri uri, Action<HttpRequestMessage>? configureRequest) :
    IRangeReader
{
    public async Task<byte[]> Read(long offset, long length, Cancel cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new(offset, offset + length - 1);
        configureRequest?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            await Skip(stream, offset, cancel);
        }

        return await ReadExactly(stream, length, cancel);
    }

    static async Task Skip(Stream stream, long count, Cancel cancel)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer, 0, (int) Math.Min(buffer.Length, count), cancel);
            if (read == 0)
            {
                throw new RemoteZipException("Response ended before the requested range started.");
            }

            count -= read;
        }
    }

    static async Task<byte[]> ReadExactly(Stream stream, long length, Cancel cancel)
    {
        var result = new byte[length];
        var position = 0;
        while (position < result.Length)
        {
            var read = await stream.ReadAsync(result, position, result.Length - position, cancel);
            if (read == 0)
            {
                throw new RemoteZipException("Response ended before the requested range was fully served.");
            }

            position += read;
        }

        return result;
    }
}