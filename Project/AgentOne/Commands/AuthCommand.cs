using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// The API key, kept out of config.json and out of shell history: `auth set`
/// reads the key from stdin or prompts for it, rather than taking it as an
/// argument where it would be recorded by the shell and visible in `ps`.
/// </summary>
public sealed class AuthCommand
{
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var sub = args.Length > 0 ? args[0] : "show";

        return sub switch
        {
            "-h" or "--help" => Help(),
            "show" => Show(),
            "set" => await SetAsync(ct),
            "clear" => Clear(),
            "import" => Import(),
            _ => Unknown(sub)
        };
    }

    private static int Show()
    {
        var config = ConfigStore.Load();
        var resolved = ApiKey.Resolve(config);

        Console.WriteLine($"key      {CredentialStore.Mask(resolved.Value)}");
        Console.WriteLine($"source   {resolved.Source}");
        Console.WriteLine($"stored   {CredentialStore.Path}" +
                          (File.Exists(CredentialStore.Path) ? "" : "  (no file)"));
        Console.WriteLine($"fallback ${config.ApiKeyEnv}" +
                          (Environment.GetEnvironmentVariable(config.ApiKeyEnv) is { Length: > 0 } ? "  (set)" : "  (not set)"));

        return resolved.Found ? 0 : 1;
    }

    private static async Task<int> SetAsync(CancellationToken ct)
    {
        string? key;

        if (Console.IsInputRedirected)
        {
            key = (await Console.In.ReadToEndAsync(ct)).Trim();
        }
        else
        {
            Console.Error.Write("API key: ");
            key = ReadHidden();
            Console.Error.WriteLine();
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("agent-one auth set: no key given");
            return 2;
        }

        CredentialStore.Save(key);
        Console.WriteLine($"stored {CredentialStore.Mask(key)} → {CredentialStore.Path}");
        return 0;
    }

    private static int Clear()
    {
        CredentialStore.Clear();
        Console.WriteLine($"removed {CredentialStore.Path}");
        return 0;
    }

    /// <summary>
    /// Rescues a key that was pasted into the apiKeyEnv field — a mistake the
    /// old UI invited — by moving it into the credential store and putting a
    /// real variable name back.
    /// </summary>
    private static int Import()
    {
        var config = ConfigStore.Load();

        if (ApiKey.LooksLikeVariableName(config.ApiKeyEnv))
        {
            Console.WriteLine($"apiKeyEnv is a variable name (${config.ApiKeyEnv}) — nothing to import.");
            return 0;
        }

        var pasted = config.ApiKeyEnv;
        CredentialStore.Save(pasted);

        config.ApiKeyEnv = new AgentConfig().ApiKeyEnv;      // back to the default name
        ConfigStore.Save(config);

        Console.WriteLine($"moved {CredentialStore.Mask(pasted)} out of config.json → {CredentialStore.Path}");
        Console.WriteLine($"apiKeyEnv reset to ${config.ApiKeyEnv}");
        Console.WriteLine("check it with: agent-one models");
        return 0;
    }

    /// <summary>Reads a line without echoing it, so the key never appears on screen.</summary>
    private static string ReadHidden()
    {
        var chars = new List<char>();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) return "";

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.RemoveAt(chars.Count - 1);
                continue;
            }

            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }

        return new string([.. chars]);
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"agent-one auth: unknown subcommand '{sub}'");
        PrintHelp();
        return 2;
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one auth show      Where the key comes from, masked (default)
            agent-one auth set       Store a key — typed without echo, or piped on stdin
            agent-one auth clear     Forget the stored key
            agent-one auth import    Move a key that was pasted into apiKeyEnv into the store

            The key is never written to config.json. It lives in
            ~/.agent-one/credentials.json, and `auth set` takes it from stdin or a
            hidden prompt so it does not land in your shell history.

            Lookup order: the stored key first, then the environment variable named
            by `apiKeyEnv` (default OPENAI_API_KEY).

            Examples:
              agent-one auth set                      # prompts, nothing echoed
              cat key.txt | agent-one auth set        # from a file
              agent-one auth show
            """);
    }
}
