namespace RemoteZip;

public sealed class RemoteZipOptions
{
    /// <summary>
    /// Bytes fetched from the end of the file when opening. The default (128 KiB) always
    /// contains the end-of-central-directory record — its maximum legal span is 65,557
    /// bytes — and for typical archives the entire central directory, so opening costs a
    /// single request.
    /// </summary>
    public int TailLength { get; init; } = 128 * 1024;

    /// <summary>
    /// Cap applied when the whole archive has to be buffered because the server ignored
    /// the range request (plain 200 response), and to any single entry read. Exceeding it
    /// throws <see cref="RemoteZipException" />.
    /// </summary>
    public long MaxBufferLength { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Applied to every outgoing request. Blazor WebAssembly callers should use it to call
    /// <c>SetBrowserRequestCache(BrowserRequestCache.NoStore)</c> so the browser HTTP cache
    /// cannot answer one range request with the cached body of another.
    /// </summary>
    public Action<HttpRequestMessage>? ConfigureRequest { get; init; }
}
