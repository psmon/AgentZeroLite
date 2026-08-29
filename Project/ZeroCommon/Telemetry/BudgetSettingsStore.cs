using System;
using System.IO;
using System.Text.Json;

namespace Agent.Common.Telemetry;

/// <summary>
/// JSON persistence for <see cref="BudgetSettings"/> at
/// <c>%LocalAppData%\AgentZeroLite\budget-settings.json</c>. Mirrors the
/// <c>LlmSettingsStore</c> pattern (static Load/Save, defaults on missing or
/// corrupt file) — no EF migration, no credentials, so no encryption layer.
/// </summary>
public static class BudgetSettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "budget-settings.json");

    /// <summary>Load from the default path (or an explicit one, for tests). Never throws.</summary>
    public static BudgetSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new BudgetSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BudgetSettings>(json) ?? new BudgetSettings();
        }
        catch
        {
            return new BudgetSettings();
        }
    }

    /// <summary>Persist to the default path (or an explicit one, for tests).</summary>
    public static void Save(BudgetSettings settings, string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
