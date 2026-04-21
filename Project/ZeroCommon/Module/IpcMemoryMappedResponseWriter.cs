using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace Agent.Common.Module;

[SupportedOSPlatform("windows")]
public static class IpcMemoryMappedResponseWriter
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, MemoryMappedFile> Maps = new(StringComparer.Ordinal);

    public static void WriteJson(string mapName, int capacity, string json, string errorContext)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(json);
            if (data.Length > capacity - 4)
                throw new InvalidOperationException($"Response size {data.Length} exceeds MMF capacity {capacity - 4} for {mapName}.");

            lock (Sync)
            {
                if (!Maps.TryGetValue(mapName, out var mmf))
                {
                    mmf = MemoryMappedFile.CreateOrOpen(mapName, capacity);
                    Maps[mapName] = mmf;
                }

                using var accessor = mmf.CreateViewAccessor(0, capacity);
                accessor.Write(0, 0);
                accessor.WriteArray(4, data, 0, data.Length);
                accessor.Write(0, data.Length);
                accessor.Flush();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"{errorContext}: {ex.Message}");
        }
    }
}
