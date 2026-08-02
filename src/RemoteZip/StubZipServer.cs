namespace RemoteZip;

/// <summary>
/// An <see cref="HttpMessageHandler" /> for testing consumers, serving a byte[] the way
/// nuget.org's CDN was observed to behave: suffix and absolute ranges get 206 with the
/// requested slice; an unsatisfiable range gets 200 with the whole file (not 416). Toggles
/// simulate servers without range support and browser contexts where CORS hides
/// Content-Range. Batched reads issue requests concurrently, so the logs are lock-guarded
/// and their order is not meaningful — assert on counts, not sequence.
/// </summary>
public class StubZipServer(byte[] data) : HttpMessageHandler
{
    readonly Lock padlock = new();
    int inFlight;

    /// <summary>False simulates a server without range support: every response is a 200 with the full body.</summary>
    public bool SupportRanges { get; set; } = true;

    /// <summary>False omits Content-Range from 206 responses, matching what browser CORS lets a caller see on nuget.org.</summary>
    public bool ExposeContentRange { get; set; } = true;

    /// <summary>
    /// Held open before responding. A non-zero delay is what makes overlapping requests
    /// observable via <see cref="MaxConcurrentRequests" />; without it each response
    /// completes before the next request is issued.
    /// </summary>
    public TimeSpan Delay { get; set; }

    /// <summary>One entry per request: the Range header served, or "full".</summary>
    public List<string> Requests { get; } = [];

    /// <summary>The full request headers of every request, for asserting on configured extras.</summary>
    public List<string> HeaderLog { get; } = [];

    /// <summary>High-water mark of requests in flight at the same time.</summary>
    public int MaxConcurrentRequests { get; private set; }

    /// <summary>Total body bytes served, for asserting a consumer's fetch efficiency.</summary>
    public long BytesServed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel)
    {
        lock (padlock)
        {
            inFlight++;
            MaxConcurrentRequests = Math.Max(MaxConcurrentRequests, inFlight);
            HeaderLog.Add(request.Headers.ToString());
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancel);
            }

            return Respond(request);
        }
        finally
        {
            lock (padlock)
            {
                inFlight--;
            }
        }
    }

    HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var range = request.Headers.Range;
        if (range == null || !SupportRanges)
        {
            Log("full");
            return Full();
        }

        Log(range.ToString());
        var spec = range.Ranges.Single();
        long from;
        long to;
        if (spec.From == null)
        {
            var suffix = Math.Min(spec.To!.Value, data.Length);
            from = data.Length - suffix;
            to = data.Length - 1;
        }
        else
        {
            from = spec.From.Value;
            if (from >= data.Length)
            {
                return Full();
            }

            to = Math.Min(spec.To ?? long.MaxValue, data.Length - 1);
        }

        var slice = new byte[to - from + 1];
        Array.Copy(data, from, slice, 0, slice.Length);
        Count(slice.Length);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        if (ExposeContentRange)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
        }

        return response;
    }

    void Log(string request)
    {
        lock (padlock)
        {
            Requests.Add(request);
        }
    }

    void Count(long bytes)
    {
        lock (padlock)
        {
            BytesServed += bytes;
        }
    }

    HttpResponseMessage Full()
    {
        Count(data.Length);
        return new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        };
    }
}
