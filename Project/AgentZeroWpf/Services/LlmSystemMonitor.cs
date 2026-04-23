using System.Diagnostics;
using System.Globalization;

namespace AgentZeroWpf.Services;

public sealed record LlmSystemSnapshot(
    long ProcessWorkingSetMb,
    long? GpuUsedMb,
    long? GpuTotalMb);

public static class LlmSystemMonitor
{
    // Cached per-process snapshots — refreshed on demand (UI poll timer).
    public static LlmSystemSnapshot Snapshot()
    {
        var rssMb = Environment.WorkingSet / (1024 * 1024);

        long? used = null, total = null;
        try
        {
            (used, total) = QueryNvidiaSmi();
        }
        catch { /* no NVIDIA or nvidia-smi missing — leave null */ }

        return new LlmSystemSnapshot(rssMb, used, total);
    }

    private static (long? used, long? total) QueryNvidiaSmi()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p is null) return (null, null);
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(1500);

        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return (null, null);

        var parts = first.Split(',');
        if (parts.Length < 2) return (null, null);

        if (long.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var used)
         && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total))
            return (used, total);

        return (null, null);
    }
}
