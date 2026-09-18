namespace Agent.Common.Platform;

/// <summary>
/// Where this app keeps its data, in one place (M0033). Today every store builds
/// <c>%LOCALAPPDATA%\AgentZeroLite</c> by hand; they keep doing so (the WPF host is
/// not touched by the Avalonia work), and the new host reads the same folder through
/// this class so both GUIs share one database and one settings set on Windows.
///
/// <para>Phase 1 keeps <c>LocalApplicationData</c> on every OS — on macOS that is
/// <c>~/.local/share/AgentZeroLite</c>, which is unusual for a Mac app but harmless.
/// Moving to <c>~/Library/Application Support</c> is a one-line change here once the
/// existing stores route through it too.</para>
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AgentZeroLite";

    private static string? _override;

    /// <summary>The data root; created on first use.</summary>
    public static string DataRoot
    {
        get
        {
            var root = _override ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>A file directly under the data root.</summary>
    public static string File(string name) => Path.Combine(DataRoot, name);

    /// <summary>A sub-folder under the data root, created on first use.</summary>
    public static string Dir(string name)
    {
        var dir = Path.Combine(DataRoot, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LogsDir => Dir("logs");
    public static string ModelsDir => Dir("models");

    /// <summary>Tests point the root at a scratch folder; disposing restores it.</summary>
    public static IDisposable OverrideRoot(string root)
    {
        var previous = _override;
        _override = root;
        return new Restore(() => _override = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
