namespace AgentOne.Services;

/// <param name="Value">The key, or null when there is none to be found.</param>
/// <param name="Source">Where it came from, for messages: "credentials.json", "$VAR", or "none".</param>
public readonly record struct ResolvedApiKey(string? Value, string Source)
{
    public bool Found => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>
/// One place that decides where the API key comes from, so every error message
/// can name the same two places in the same order.
/// </summary>
public static class ApiKey
{
    /// <summary>
    /// The stored key wins over the environment variable: it is the one the
    /// operator typed into this tool, so it should not be silently overridden by
    /// a variable some shell profile happens to export.
    /// </summary>
    public static ResolvedApiKey Resolve(AgentConfig config)
    {
        // A config derived for the reasoning model asks for its own slot first;
        // an empty one means "the same key as the everyday model", which is what
        // one gateway serving two model sizes needs.
        if (config.KeySlot == CredentialStore.Slot.Reasoning)
        {
            var own = CredentialStore.Load(CredentialStore.Slot.Reasoning);
            if (!string.IsNullOrWhiteSpace(own)) return new ResolvedApiKey(own, "credentials.json (reasoning)");
        }

        var stored = CredentialStore.Load();
        if (!string.IsNullOrWhiteSpace(stored)) return new ResolvedApiKey(stored, "credentials.json");

        var fromEnv = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return new ResolvedApiKey(fromEnv.Trim(), "$" + config.ApiKeyEnv);

        return new ResolvedApiKey(null, "none");
    }

    /// <summary>Where a missing key could be put, phrased for an error line.</summary>
    public static string WhereToPutIt(AgentConfig config) =>
        $"set one on the Connection step of `agent-one tui`, or `agent-one auth set`, or export ${config.ApiKeyEnv}";

    /// <summary>
    /// True when a string is a plausible environment-variable NAME. Anything else
    /// in the apiKeyEnv field is almost certainly the key itself, pasted into the
    /// wrong box — a mistake worth catching at the point of entry, because the
    /// symptom otherwise is an unexplained 401.
    /// </summary>
    public static bool LooksLikeVariableName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        if (!char.IsLetter(value[0]) && value[0] != '_') return false;

        foreach (var c in value)
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;

        return true;
    }
}
