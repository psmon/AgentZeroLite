using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Common;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// Per-sample remembered state for the WebDev floating window: pin
/// (always-on-top), opacity (0.5–1.0), chrome (custom titlebar shown),
/// and on-screen rect (x,y,w,h). Loaded on Detach, saved on every
/// toggle/slide/resize/move/close. Persisted as a single JSON file in
/// %LOCALAPPDATA%/AgentZeroLite/WebDev/floating-prefs.json so the
/// installer's per-user folder pattern is preserved.
/// </summary>
public sealed class WebDevFloatingPref
{
    [JsonPropertyName("topmost")] public bool Topmost { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1.0;
    [JsonPropertyName("chrome")]  public bool Chrome  { get; set; } = true;
    [JsonPropertyName("x")]       public double? X { get; set; }
    [JsonPropertyName("y")]       public double? Y { get; set; }
    [JsonPropertyName("w")]       public double? W { get; set; }
    [JsonPropertyName("h")]       public double? H { get; set; }
}

public static class WebDevFloatingPrefs
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "WebDev", "floating-prefs.json");

    public static WebDevFloatingPref Get(string sampleId)
    {
        lock (Gate)
        {
            var all = LoadUnlocked();
            return all.TryGetValue(sampleId, out var p) ? p : new WebDevFloatingPref();
        }
    }

    public static void Save(string sampleId, WebDevFloatingPref pref)
    {
        lock (Gate)
        {
            var all = LoadUnlocked();
            all[sampleId] = pref;
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(all, JsonOpts));
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[WebDev:Prefs] save failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static Dictionary<string, WebDevFloatingPref> LoadUnlocked()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<Dictionary<string, WebDevFloatingPref>>(json)
                   ?? new();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[WebDev:Prefs] load failed: {ex.GetType().Name}: {ex.Message}");
            return new();
        }
    }
}
