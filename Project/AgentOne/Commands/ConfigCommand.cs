using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>`agent-one config {show|get|set|path|reset}` over ~/.agent-one/config.json.</summary>
public sealed class ConfigCommand
{
    public int Execute(string[] args)
    {
        if (args.Length == 0) return Show();

        return args[0] switch
        {
            "-h" or "--help" => Help(),
            "show" => Show(),
            "path" => Path(),
            "get" => Get(args[1..]),
            "set" => Set(args[1..]),
            "reset" => Reset(),
            _ => Unknown(args[0])
        };
    }

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static int Show()
    {
        var config = ConfigStore.Load(out var warning);
        if (warning.Length > 0) Console.Error.WriteLine("agent-one: " + warning);

        Console.WriteLine($"config: {AppPaths.ConfigPath}" + (File.Exists(AppPaths.ConfigPath) ? "" : "  (not created yet — defaults shown)"));
        foreach (var key in AgentConfig.Keys)
            Console.WriteLine($"  {key,-15} {config.Get(key)}");

        var keyPresent = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(config.ApiKeyEnv));
        Console.WriteLine($"  {"api key",-15} ${config.ApiKeyEnv} is {(keyPresent ? "set" : "NOT set")}");
        return 0;
    }

    private static int Path()
    {
        Console.WriteLine(AppPaths.ConfigPath);
        return 0;
    }

    private static int Get(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("agent-one config get: needs exactly one key");
            return 2;
        }

        var value = ConfigStore.Load().Get(args[0]);
        if (value is null)
        {
            Console.Error.WriteLine($"agent-one config get: unknown key '{args[0]}' (known: {string.Join(", ", AgentConfig.Keys)})");
            return 2;
        }

        Console.WriteLine(value);
        return 0;
    }

    private static int Set(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("agent-one config set: needs <key> <value>");
            return 2;
        }

        var config = ConfigStore.Load();
        if (!config.TrySet(args[0], args[1], out var error))
        {
            Console.Error.WriteLine("agent-one config set: " + error);
            return 2;
        }

        ConfigStore.Save(config);
        Console.WriteLine($"{args[0]} = {config.Get(args[0])}");
        return 0;
    }

    private static int Reset()
    {
        ConfigStore.Save(new AgentConfig());
        Console.WriteLine($"reset to defaults: {AppPaths.ConfigPath}");
        return 0;
    }

    private static int Unknown(string sub)
    {
        Console.Error.WriteLine($"agent-one config: unknown subcommand '{sub}'");
        PrintHelp();
        return 2;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one config show          Print the effective settings (default)
            agent-one config get <key>     Print one value
            agent-one config set <key> <v> Store one value
            agent-one config path          Print the config file path
            agent-one config reset         Restore defaults

            Keys: provider, baseUrl, model, apiKeyEnv, maxSteps, temperature,
                  timeoutSeconds, saveSessions

            The API key itself is never stored here — apiKeyEnv names the
            environment variable to read it from (default OPENAI_API_KEY).

            Ollama / LM Studio example:
              agent-one config set provider openai
              agent-one config set baseUrl http://localhost:11434/v1
              agent-one config set model qwen2.5-coder:7b
            """);
    }
}
