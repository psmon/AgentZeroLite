namespace Agent.Common.Llm;

public static class LlmModelLocator
{
    // Legacy single-file constants kept for back-compat with callers that
    // haven't been updated to use LlmModelCatalog yet. These point at the
    // default catalog entry (E4B UD-Q4_K_XL).
    public static string ModelFileName => LlmModelCatalog.Default.FileName;
    public static string DownloadUrl => LlmModelCatalog.Default.DownloadUrl;
    public static long ExpectedSizeBytes => LlmModelCatalog.Default.ApproxBytes;

    private const string DevModelsDir = @"D:\Code\AI\GemmaNet\models";

    public static string UserModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentZeroLite", "models");

    // Path resolution per catalog entry: dev-path wins if the file lives
    // there (developer workflow), otherwise the user-data path.
    public static string DevPath(LlmModelCatalogEntry entry)
        => Path.Combine(DevModelsDir, entry.FileName);

    public static string UserPathFor(LlmModelCatalogEntry entry)
        => Path.Combine(UserModelsDir, entry.FileName);

    public static string ResolveExistingOrTarget(LlmModelCatalogEntry entry)
    {
        var dev = DevPath(entry);
        return File.Exists(dev) ? dev : UserPathFor(entry);
    }

    public static bool IsAvailable(LlmModelCatalogEntry entry)
        => File.Exists(ResolveExistingOrTarget(entry));

    // Legacy zero-arg overloads — default to the first catalog entry.
    public static string UserPath => UserPathFor(LlmModelCatalog.Default);
    public static string ResolveExistingOrTarget() => ResolveExistingOrTarget(LlmModelCatalog.Default);
    public static bool IsAvailable() => IsAvailable(LlmModelCatalog.Default);
}
