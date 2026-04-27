namespace Agent.Common.Module;

public static class AppVersionProvider
{
    /// <summary>
    /// Lookup order:
    /// <list type="number">
    /// <item><description><c>version.txt</c> sitting next to the entry exe — the
    /// truth source the build pipeline updates and the installer labels with.
    /// Wins because it survives "loaded from a different assembly" edge cases
    /// (e.g. a helper DLL whose own InformationalVersion is the unset
    /// default 1.0.0).</description></item>
    /// <item><description>Entry assembly's <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
    /// — what the .csproj injects from the same version.txt at compile time.</description></item>
    /// <item><description>Executing assembly's attribute — last-resort fallback.</description></item>
    /// </list>
    /// </summary>
    public static string GetDisplayVersion()
    {
        // 1. Runtime read of version.txt next to the running exe.
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (System.IO.File.Exists(path))
            {
                var text = System.IO.File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(text))
                    return $"v{text}";
            }
        }
        catch { /* fall through to assembly attribute */ }

        // 2. Entry assembly attribute (csproj-injected from version.txt).
        // 3. Executing assembly attribute (when EntryAssembly is null, e.g. tests).
        var assembly = System.Reflection.Assembly.GetEntryAssembly()
                       ?? System.Reflection.Assembly.GetExecutingAssembly();
        var versionAttribute = (System.Reflection.AssemblyInformationalVersionAttribute?)
            Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));

        var versionText = versionAttribute?.InformationalVersion ?? "?";
        var buildMetadataSeparator = versionText.IndexOf('+');
        if (buildMetadataSeparator >= 0)
            versionText = versionText[..buildMetadataSeparator];

        return $"v{versionText}";
    }
}
