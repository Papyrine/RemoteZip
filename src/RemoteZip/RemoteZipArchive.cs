namespace RemoteZip;

/// <summary>
/// Reads a zip archive over HTTP without downloading the whole file. Opening costs one
/// range request for the file tail (end-of-central-directory record plus, typically, the
/// whole central directory); each read costs one more request sized to the entry, with
/// adjacent entries in a batched read coalescing into a single request. Servers that
/// ignore range requests degrade transparently to a bounded full download.
/// </summary>
public sealed class RemoteZipArchive
{
    // Local-header extra fields may differ from the central directory's, so entry reads
    // over-fetch by this much to almost always cover header + name + extra in one request.
    // A larger local extra field costs one exact follow-up request instead of failing.
    const int extraFieldSlack = 512;

    // Entries in a batched read whose byte ranges are closer than this are fetched in one
    // request; the gap bytes are discarded. One round trip usually costs more than 8 KiB.
    const int coalesceGap = 8 * 1024;

    readonly IRangeReader reader;
    readonly List<RemoteZipEntry> entries;
    readonly long centralDirectoryOffset;
    readonly long maxBufferLength;
    readonly int maxConcurrency;

    RemoteZipArchive(
        IRangeReader reader,
        List<RemoteZipEntry> entries,
        long fileLength,
        long centralDirectoryOffset,
        bool downloadedWholeFile,
        RemoteZipOptions options)
    {
        this.reader = reader;
        this.entries = entries;
        FileLength = fileLength;
        this.centralDirectoryOffset = centralDirectoryOffset;
        DownloadedWholeFile = downloadedWholeFile;
        maxBufferLength = options.MaxBufferLength;
        maxConcurrency = Math.Max(1, options.MaxConcurrency);
    }

    public IReadOnlyList<RemoteZipEntry> Entries => entries;

    /// <summary>Total length of the remote file in bytes.</summary>
    public long FileLength { get; }

    /// <summary>
    /// True when the whole archive ended up buffered in memory — either the server ignored
    /// the range request, or the file fit inside the initial tail request. Reads are then
    /// served locally without further requests.
    /// </summary>
    public bool DownloadedWholeFile { get; }

    public static Task<RemoteZipArchive> Open(HttpClient http, string url, RemoteZipOptions? options = null, Cancel cancel = default) =>
        Open(http, new Uri(url), options, cancel);

    public static async Task<RemoteZipArchive> Open(HttpClient http, Uri uri, RemoteZipOptions? options = null, Cancel cancel = default)
    {
        options ??= new();
        var tailLength = Math.Max(options.TailLength, ZipFormat.EndOfCentralDirectoryLength);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new(null, tailLength);
        options.ConfigureRequest?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        using var content = await response.Content.ReadAsStreamAsync(cancel);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            // No range support: buffer the whole archive, bounded.
            var whole = await ReadBounded(content, contentLength, options.MaxBufferLength, cancel);
            return await Build(null, whole, 0, whole.Length, options, cancel);
        }

        var tail = await ReadBounded(content, contentLength, tailLength, cancel);

        // Content-Range gives the tail's absolute position directly, but CORS hides the
        // header from browser callers (it is rarely in Access-Control-Expose-Headers, and
        // notably not on nuget.org), so it is optional input, not a requirement.
        long? tailStart = null;
        long? knownLength = null;
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is {Unit: "bytes"})
        {
            tailStart = contentRange.From;
            knownLength = contentRange.Length;
        }

        var httpReader = new HttpRangeReader(http, uri, options.ConfigureRequest);
        return await Build(httpReader, tail, tailStart, knownLength, options, cancel);
    }

    static async Task<RemoteZipArchive> Build(
        HttpRangeReader? httpReader,
        byte[] tail,
        long? tailStart,
        long? knownLength,
        RemoteZipOptions options,
        Cancel cancel)
    {
        var eocdIndex = ZipFormat.FindEndOfCentralDirectory(tail);
        if (eocdIndex < 0)
        {
            throw new RemoteZipException(
                "No end-of-central-directory record found. Either the file is not a zip archive, or its comment exceeds the configured TailLength.");
        }

        long entryCount = ZipFormat.UInt16(tail, eocdIndex + 10);
        long directorySize = ZipFormat.UInt32(tail, eocdIndex + 12);
        long directoryOffset = ZipFormat.UInt32(tail, eocdIndex + 16);

        // Absolute position of a structure whose index within the tail is known, used to
        // derive the tail's own absolute position when Content-Range was unavailable. For
        // a well-formed archive the central directory runs right up to the record that
        // follows it, and the signature check below validates the assumption.
        long anchorAbsolute;
        int anchorIndex;

        if (entryCount == 0xFFFF || directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
        {
            var locatorIndex = eocdIndex - ZipFormat.Zip64LocatorLength;
            if (locatorIndex < 0 ||
                ZipFormat.UInt32(tail, locatorIndex) != ZipFormat.Zip64LocatorSignature)
            {
                throw new RemoteZipException("zip64 markers present but the zip64 locator record is missing.");
            }

            var zip64RecordAbsolute = (long) ZipFormat.UInt64(tail, locatorIndex + 8);
            var recordIndex = ZipFormat.FindZip64Record(tail, locatorIndex);
            if (recordIndex < 0)
            {
                throw new RemoteZipException("zip64 end-of-central-directory record not found within the tail. Increase TailLength.");
            }

            var recordLength = 12 + (long) ZipFormat.UInt64(tail, recordIndex + 4);
            if (recordIndex + recordLength != locatorIndex)
            {
                throw new RemoteZipException("Unexpected data between the zip64 end-of-central-directory record and its locator.");
            }

            entryCount = (long) ZipFormat.UInt64(tail, recordIndex + 32);
            directorySize = (long) ZipFormat.UInt64(tail, recordIndex + 40);
            directoryOffset = (long) ZipFormat.UInt64(tail, recordIndex + 48);
            anchorAbsolute = zip64RecordAbsolute;
            anchorIndex = recordIndex;
        }
        else
        {
            anchorAbsolute = directoryOffset + directorySize;
            anchorIndex = eocdIndex;
        }

        var bufferStart = tailStart ?? anchorAbsolute - anchorIndex;
        var fileLength = knownLength ?? bufferStart + tail.Length;
        if (bufferStart < 0 ||
            directoryOffset + directorySize > fileLength ||
            directorySize > int.MaxValue)
        {
            throw new RemoteZipException("Corrupt end-of-central-directory record.");
        }

        ReadOnlyMemory<byte> directory;
        if (directoryOffset >= bufferStart && directoryOffset + directorySize <= bufferStart + tail.Length)
        {
            directory = tail.AsMemory((int) (directoryOffset - bufferStart), (int) directorySize);
        }
        else
        {
            if (httpReader == null)
            {
                throw new RemoteZipException("Corrupt central directory position.");
            }

            directory = await httpReader.Read(directoryOffset, directorySize, cancel);
        }

        if (directorySize >= 4 &&
            ZipFormat.UInt32(directory.Span, 0) != ZipFormat.CentralDirectorySignature)
        {
            throw new RemoteZipException(
                "Central directory is not at the position the end-of-central-directory record claims. Archives with prepended data are not supported.");
        }

        var entries = ParseCentralDirectory(directory.Span, entryCount);

        var wholeFile = bufferStart == 0;
        IRangeReader reader;
        if (wholeFile)
        {
            reader = new ArrayRangeReader(tail);
        }
        else
        {
            var http = httpReader ?? throw new RemoteZipException("Corrupt central directory position.");

            // The tail is in hand already, so entries stored inside it cost no request.
            reader = new TailCachedRangeReader(http, tail, bufferStart);
        }

        return new(reader, entries, fileLength, directoryOffset, wholeFile, options);
    }

    static List<RemoteZipEntry> ParseCentralDirectory(ReadOnlySpan<byte> directory, long entryCount)
    {
        var entries = new List<RemoteZipEntry>((int) Math.Min(entryCount, 100_000));
        var position = 0;
        var end = directory.Length;
        while (entries.Count < entryCount)
        {
            if (position + 46 > end ||
                ZipFormat.UInt32(directory, position) != ZipFormat.CentralDirectorySignature)
            {
                throw new RemoteZipException("Corrupt central directory.");
            }

            var flags = ZipFormat.UInt16(directory, position + 8);
            var method = ZipFormat.UInt16(directory, position + 10);
            var crc = ZipFormat.UInt32(directory, position + 16);
            long compressedLength = ZipFormat.UInt32(directory, position + 20);
            long length = ZipFormat.UInt32(directory, position + 24);
            int nameLength = ZipFormat.UInt16(directory, position + 28);
            int extraLength = ZipFormat.UInt16(directory, position + 30);
            int commentLength = ZipFormat.UInt16(directory, position + 32);
            long localHeaderOffset = ZipFormat.UInt32(directory, position + 42);

            if (position + 46 + nameLength + extraLength + commentLength > end)
            {
                throw new RemoteZipException("Corrupt central directory.");
            }

            var fullName = Encoding.UTF8.GetString(directory.Slice(position + 46, nameLength));

            // The zip64 extended-information extra field overrides any 32/16-bit value
            // that was stored as its all-ones marker, in this fixed field order.
            if (length == 0xFFFFFFFF ||
                compressedLength == 0xFFFFFFFF ||
                localHeaderOffset == 0xFFFFFFFF)
            {
                var extraPosition = position + 46 + nameLength;
                var extraEnd = extraPosition + extraLength;
                while (extraPosition + 4 <= extraEnd)
                {
                    var id = ZipFormat.UInt16(directory, extraPosition);
                    var size = ZipFormat.UInt16(directory, extraPosition + 2);
                    if (id == 0x0001)
                    {
                        var field = extraPosition + 4;
                        if (length == 0xFFFFFFFF)
                        {
                            length = (long) ZipFormat.UInt64(directory, field);
                            field += 8;
                        }

                        if (compressedLength == 0xFFFFFFFF)
                        {
                            compressedLength = (long) ZipFormat.UInt64(directory, field);
                            field += 8;
                        }

                        if (localHeaderOffset == 0xFFFFFFFF)
                        {
                            localHeaderOffset = (long) ZipFormat.UInt64(directory, field);
                        }

                        break;
                    }

                    extraPosition += 4 + size;
                }
            }

            entries.Add(new(fullName, length, compressedLength, localHeaderOffset, method, flags, crc, nameLength));
            position += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    public RemoteZipEntry? Find(string fullName)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.FullName, fullName, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Downloads and decompresses a single entry, verifying its crc.</summary>
    public async Task<byte[]> Read(RemoteZipEntry entry, Cancel cancel = default)
    {
        Validate(entry);
        var start = entry.LocalHeaderOffset;
        var end = Math.Min(UpperBound(entry), centralDirectoryOffset);
        var buffer = await reader.Read(start, end - start, cancel);
        return await Extract(entry, buffer, start, cancel);
    }

    /// <summary>
    /// Downloads and decompresses multiple entries, fetching entries that sit close
    /// together in the archive in a single request and overlapping the requests that remain.
    /// </summary>
    public async Task<IReadOnlyDictionary<RemoteZipEntry, byte[]>> Read(IReadOnlyCollection<RemoteZipEntry> batch, Cancel cancel = default)
    {
        foreach (var entry in batch)
        {
            Validate(entry);
        }

        var ordered = batch.Distinct().OrderBy(_ => _.LocalHeaderOffset).ToList();
        var clusters = BuildClusters(ordered);

        // Clusters are independent requests, so they are issued together — a serial loop
        // pays a full round trip for every one of them. The gate keeps a large batch from
        // flooding the server; browsers cap concurrent requests per origin regardless.
        using var gate = new SemaphoreSlim(maxConcurrency);
        var fetched = await Task.WhenAll(clusters.Select(_ => ReadCluster(_, gate, cancel)));

        var results = new Dictionary<RemoteZipEntry, byte[]>(ordered.Count);
        foreach (var cluster in fetched)
        {
            foreach (var (entry, content) in cluster)
            {
                results[entry] = content;
            }
        }

        return results;
    }

    /// <summary>
    /// Groups entries whose byte ranges are close enough that one request covering the gap
    /// between them beats a second round trip. Pure arithmetic over the ordered entries, so
    /// the whole request plan is known before any of it is issued.
    /// </summary>
    List<Cluster> BuildClusters(List<RemoteZipEntry> ordered)
    {
        var clusters = new List<Cluster>();
        var index = 0;
        while (index < ordered.Count)
        {
            var start = ordered[index].LocalHeaderOffset;
            var end = UpperBound(ordered[index]);
            var last = index;
            while (last + 1 < ordered.Count &&
                   ordered[last + 1].LocalHeaderOffset <= end + coalesceGap &&
                   Math.Max(end, UpperBound(ordered[last + 1])) - start <= maxBufferLength)
            {
                last++;
                end = Math.Max(end, UpperBound(ordered[last]));
            }

            clusters.Add(new(start, Math.Min(end, centralDirectoryOffset), ordered.GetRange(index, last - index + 1)));
            index = last + 1;
        }

        return clusters;
    }

    async Task<List<KeyValuePair<RemoteZipEntry, byte[]>>> ReadCluster(Cluster cluster, SemaphoreSlim gate, Cancel cancel)
    {
        ReadOnlyMemory<byte> buffer;
        await gate.WaitAsync(cancel);
        try
        {
            buffer = await reader.Read(cluster.Start, cluster.End - cluster.Start, cancel);
        }
        finally
        {
            gate.Release();
        }

        // Decompression runs outside the gate so it overlaps other clusters' downloads.
        var extracted = new List<KeyValuePair<RemoteZipEntry, byte[]>>(cluster.Entries.Count);
        foreach (var entry in cluster.Entries)
        {
            extracted.Add(new(entry, await Extract(entry, buffer, cluster.Start, cancel)));
        }

        return extracted;
    }

    sealed record Cluster(long Start, long End, List<RemoteZipEntry> Entries);

    /// <summary>Downloads an entry and decodes it as text, honoring a byte-order mark.</summary>
    public async Task<string> ReadText(RemoteZipEntry entry, Cancel cancel = default)
    {
        var bytes = await Read(entry, cancel);
        using var streamReader = new StreamReader(new MemoryStream(bytes));
        return await streamReader.ReadToEndAsync(cancel);
    }

    static long UpperBound(RemoteZipEntry entry) =>
        entry.LocalHeaderOffset + ZipFormat.LocalHeaderLength + entry.NameByteCount + extraFieldSlack + entry.CompressedLength;

    void Validate(RemoteZipEntry entry)
    {
        if ((entry.Flags & 1) != 0)
        {
            throw new RemoteZipException($"'{entry.FullName}' is encrypted. Encrypted entries are not supported.");
        }

        if (entry.Method != 0 &&
            entry.Method != 8)
        {
            throw new RemoteZipException($"'{entry.FullName}' uses compression method {entry.Method}. Only stored (0) and deflate (8) are supported.");
        }

        if (entry.Length > maxBufferLength ||
            entry.CompressedLength > maxBufferLength)
        {
            throw new RemoteZipException($"'{entry.FullName}' exceeds MaxBufferLength ({maxBufferLength} bytes).");
        }

        if (entry.LocalHeaderOffset + ZipFormat.LocalHeaderLength > centralDirectoryOffset)
        {
            throw new RemoteZipException($"'{entry.FullName}' has a local header position that overlaps the central directory.");
        }
    }

    async Task<byte[]> Extract(RemoteZipEntry entry, ReadOnlyMemory<byte> buffer, long bufferOffset, Cancel cancel)
    {
        var headerIndex = (int) (entry.LocalHeaderOffset - bufferOffset);
        if (headerIndex + ZipFormat.LocalHeaderLength > buffer.Length ||
            ZipFormat.UInt32(buffer.Span, headerIndex) != ZipFormat.LocalHeaderSignature)
        {
            throw new RemoteZipException($"'{entry.FullName}' has no local header at the position the central directory claims.");
        }

        int nameLength = ZipFormat.UInt16(buffer.Span, headerIndex + 26);
        int extraLength = ZipFormat.UInt16(buffer.Span, headerIndex + 28);
        var dataStart = entry.LocalHeaderOffset + ZipFormat.LocalHeaderLength + nameLength + extraLength;

        if (dataStart + entry.CompressedLength <= bufferOffset + buffer.Length)
        {
            // Already fetched — slice rather than copy. In a batched read the same buffer
            // spans a whole cluster, so every entry in it is served without a copy.
            return Inflate(entry, buffer.Slice((int) (dataStart - bufferOffset), (int) entry.CompressedLength));
        }

        // The local extra field was larger than the over-fetch slack: one exact request.
        var compressed = await reader.Read(dataStart, entry.CompressedLength, cancel);
        return Inflate(entry, compressed);
    }

    static byte[] Inflate(RemoteZipEntry entry, ReadOnlyMemory<byte> compressed)
    {
        byte[] result;
        if (entry.Method == 0)
        {
            if (compressed.Length != entry.Length)
            {
                throw new RemoteZipException($"'{entry.FullName}' is stored but its compressed and uncompressed lengths differ.");
            }

            result = compressed.ToArray();
        }
        else
        {
            result = new byte[entry.Length];
            using var deflate = new DeflateStream(AsStream(compressed), CompressionMode.Decompress);
            var position = 0;
            while (position < result.Length)
            {
                var read = deflate.Read(result, position, result.Length - position);
                if (read == 0)
                {
                    throw new RemoteZipException($"'{entry.FullName}' ended before its declared uncompressed length.");
                }

                position += read;
            }

            if (deflate.ReadByte() != -1)
            {
                throw new RemoteZipException($"'{entry.FullName}' contains more data than its declared uncompressed length.");
            }
        }

        if (Crc32.HashToUInt32(result) != entry.Crc)
        {
            throw new RemoteZipException($"'{entry.FullName}' failed crc validation.");
        }

        return result;
    }

    /// <summary>
    /// Presents a buffer to <see cref="DeflateStream" /> without copying it. Every buffer
    /// here is backed by an array this library allocated, so the segment is always available.
    /// </summary>
    static Stream AsStream(ReadOnlyMemory<byte> memory)
    {
        if (MemoryMarshal.TryGetArray(memory, out var segment) &&
            segment.Array != null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, false);
        }

        return new MemoryStream(memory.ToArray(), false);
    }

    /// <summary>
    /// Reads a response body, refusing to buffer more than <paramref name="maxLength" />.
    /// Both response shapes handled here carry a Content-Length, so the buffer is normally
    /// allocated once at its final size; a response without one falls back to growing.
    /// </summary>
    static async Task<byte[]> ReadBounded(Stream stream, long? contentLength, long maxLength, Cancel cancel)
    {
        if (contentLength > maxLength)
        {
            throw new RemoteZipException($"Response exceeds the configured maximum of {maxLength} bytes.");
        }

        if (contentLength == null)
        {
            return await ReadGrowing(stream, maxLength, cancel);
        }

        var result = new byte[contentLength.Value];
        var position = 0;
        while (position < result.Length)
        {
            var read = await stream.ReadAsync(result.AsMemory(position), cancel);
            if (read == 0)
            {
                // Body was shorter than advertised; hand back what arrived, as growing would.
                Array.Resize(ref result, position);
                break;
            }

            position += read;
        }

        return result;
    }

    static async Task<byte[]> ReadGrowing(Stream stream, long maxLength, Cancel cancel)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > maxLength)
            {
                throw new RemoteZipException($"Response exceeds the configured maximum of {maxLength} bytes.");
            }
        }

        return memory.ToArray();
    }
}
