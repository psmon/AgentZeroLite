namespace Agent.Common.Llm;

public sealed record DownloadProgress(long BytesReceived, long TotalBytes, double BytesPerSecond);

public static class LlmModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        var partialPath = destinationPath + ".part";
        long resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        var total = resumeFrom + contentLength;

        await using var net = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            partialPath,
            resumeFrom > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1 << 16);

        var buffer = new byte[1 << 16];
        long received = resumeFrom;
        var lastReport = DateTime.UtcNow;
        long lastBytes = received;

        while (true)
        {
            var n = await net.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;

            var now = DateTime.UtcNow;
            var elapsed = (now - lastReport).TotalSeconds;
            if (elapsed >= 0.25)
            {
                var bps = (received - lastBytes) / elapsed;
                progress?.Report(new DownloadProgress(received, total, bps));
                lastReport = now;
                lastBytes = received;
            }
        }

        await file.DisposeAsync();
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        File.Move(partialPath, destinationPath);

        progress?.Report(new DownloadProgress(received, total, 0));
    }
}
