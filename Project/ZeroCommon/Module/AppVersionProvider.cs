namespace Agent.Common.Module;

public static class AppVersionProvider
{
    public static string GetDisplayVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var versionAttribute = (System.Reflection.AssemblyInformationalVersionAttribute?)
            Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));

        var versionText = versionAttribute?.InformationalVersion ?? "?";
        var buildMetadataSeparator = versionText.IndexOf('+');
        if (buildMetadataSeparator >= 0)
            versionText = versionText[..buildMetadataSeparator];

        return $"v{versionText}";
    }
}
