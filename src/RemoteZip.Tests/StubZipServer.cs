/// <summary>
/// Serves a byte[] the way nuget.org's CDN was observed to behave: suffix and absolute
/// ranges get 206 with the requested slice; an unsatisfiable range gets 200 with the whole
/// file (not 416). Toggles simulate servers without range support and browser contexts
/// where CORS hides Content-Range.
/// </summary>
class StubZipServer(byte[] data) : HttpMessageHandler
{
    public bool SupportRanges { get; set; } = true;

    public bool ExposeContentRange { get; set; } = true;

    public List<string> Requests { get; } = [];

    public List<string> HeaderLog { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel)
    {
        HeaderLog.Add(request.Headers.ToString());
        var range = request.Headers.Range;
        if (range == null || !SupportRanges)
        {
            Requests.Add("full");
            return Task.FromResult(Full());
        }

        Requests.Add(range.ToString());
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
                return Task.FromResult(Full());
            }

            to = Math.Min(spec.To ?? long.MaxValue, data.Length - 1);
        }

        var slice = new byte[to - from + 1];
        Array.Copy(data, from, slice, 0, slice.Length);
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        if (ExposeContentRange)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
        }

        return Task.FromResult(response);
    }

    HttpResponseMessage Full() =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        };
}
