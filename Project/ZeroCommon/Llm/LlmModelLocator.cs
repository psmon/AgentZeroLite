namespace Agent.Common.Llm;

public static class LlmModelLocator
{
    public const string ModelFileName = "gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    public const string DownloadUrl =
        "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-UD-Q4_K_XL.gguf";

    // Approximate expected file size (bytes). Used for sanity checks and progress
    // reporting when the HTTP server doesn't return Content-Length on resume.
    public const long ExpectedSizeBytes = 5_101_718_208L;

    private static readonly string DevPath =
        Path.Combine(@"D:\Code\AI\GemmaNet\models", ModelFileName);

    public static string UserModelsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentZeroLite", "models");

    public static string UserPath => Path.Combine(UserModelsDir, ModelFileName);

    // Developer-priority resolution: if a local dev copy exists, prefer it so
    // engineers don't re-download into %LOCALAPPDATA%.
    public static string ResolveExistingOrTarget()
        => File.Exists(DevPath) ? DevPath : UserPath;

    public static bool IsAvailable() => File.Exists(ResolveExistingOrTarget());
}
