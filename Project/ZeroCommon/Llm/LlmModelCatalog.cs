namespace Agent.Common.Llm;

public sealed record LlmModelCatalogEntry(
    string Id,
    string DisplayName,
    string FileName,
    string DownloadUrl,
    long ApproxBytes);

public static class LlmModelCatalog
{
    public static readonly LlmModelCatalogEntry E4B_UD_Q4_K_XL = new(
        Id: "gemma-4-E4B-UD-Q4_K_XL",
        DisplayName: "Gemma 4 E4B  (UD-Q4_K_XL, 5.1 GB, function-calling)",
        FileName: "gemma-4-E4B-it-UD-Q4_K_XL.gguf",
        DownloadUrl: "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-UD-Q4_K_XL.gguf",
        ApproxBytes: 5_101_718_208L);

    public static readonly LlmModelCatalogEntry E2B_UD_Q4_K_XL = new(
        Id: "gemma-4-E2B-UD-Q4_K_XL",
        DisplayName: "Gemma 4 E2B  (UD-Q4_K_XL, 3.2 GB, faster / lighter)",
        FileName: "gemma-4-E2B-it-UD-Q4_K_XL.gguf",
        DownloadUrl: "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/main/gemma-4-E2B-it-UD-Q4_K_XL.gguf",
        ApproxBytes: 3_174_043_296L);

    public static readonly LlmModelCatalogEntry NemotronNano8Bv1_UD_Q4_K_XL = new(
        Id: "nemotron-nano-8B-v1-UD-Q4_K_XL",
        DisplayName: "Nemotron Nano 8B v1  (UD-Q4_K_XL, 5.0 GB, native tool-calling)",
        FileName: "Llama-3.1-Nemotron-Nano-8B-v1-UD-Q4_K_XL.gguf",
        DownloadUrl: "https://huggingface.co/unsloth/Llama-3.1-Nemotron-Nano-8B-v1-GGUF/resolve/main/Llama-3.1-Nemotron-Nano-8B-v1-UD-Q4_K_XL.gguf",
        ApproxBytes: 4_994_203_200L);

    public static readonly IReadOnlyList<LlmModelCatalogEntry> All = new[]
    {
        E4B_UD_Q4_K_XL,
        E2B_UD_Q4_K_XL,
        NemotronNano8Bv1_UD_Q4_K_XL
    };

    public static LlmModelCatalogEntry Default => E4B_UD_Q4_K_XL;

    public static LlmModelCatalogEntry FindById(string? id)
        => All.FirstOrDefault(e => e.Id == id) ?? Default;
}
