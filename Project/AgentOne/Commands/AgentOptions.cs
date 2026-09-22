using AgentOne.Services;

namespace AgentOne.Commands;

/// <summary>
/// The flags `run` and `chat` share. Every one of them is an override of a
/// stored config value, applied on top of the loaded config — so the same flag
/// name means the same thing whether it comes from the file or the command line.
/// </summary>
public sealed class AgentOptions
{
    public AgentConfig Config { get; private set; } = new();
    public string Root { get; private set; } = Directory.GetCurrentDirectory();
    public bool Json { get; private set; }
    public bool Verbose { get; private set; }
    public bool Help { get; private set; }

    /// <summary>Non-flag arguments, in order (for `run`, the prompt words).</summary>
    public List<string> Positional { get; } = [];

    public static bool TryParse(string[] args, out AgentOptions options, out string error)
    {
        options = new AgentOptions();
        error = "";

        var config = ConfigStore.Load(out var warning);
        if (warning.Length > 0) Console.Error.WriteLine("agent-one: " + warning);

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "-h" or "--help":
                    options.Help = true;
                    break;

                case "--json":
                    options.Json = true;
                    break;

                case "-v" or "--verbose":
                    options.Verbose = true;
                    break;

                case "-r" or "--root":
                    var root = Next();
                    if (root is null) { error = "--root needs a directory"; return false; }
                    if (!Directory.Exists(root)) { error = $"--root does not exist: {root}"; return false; }
                    options.Root = Path.GetFullPath(root);
                    break;

                case "-p" or "--provider":
                    if (!Apply(config, "provider", Next(), ref error)) return false;
                    break;

                case "-m" or "--model":
                    if (!Apply(config, "model", Next(), ref error)) return false;
                    break;

                case "--base-url":
                    if (!Apply(config, "baseUrl", Next(), ref error)) return false;
                    break;

                case "--max-steps":
                    if (!Apply(config, "maxSteps", Next(), ref error)) return false;
                    break;

                case "--temperature":
                    if (!Apply(config, "temperature", Next(), ref error)) return false;
                    break;

                case "--timeout":
                    if (!Apply(config, "timeoutSeconds", Next(), ref error)) return false;
                    break;

                case "--no-session":
                    config.SaveSessions = false;
                    break;

                default:
                    if (arg.StartsWith('-') && arg.Length > 1)
                    {
                        error = $"unknown option '{arg}'";
                        return false;
                    }
                    options.Positional.Add(arg);
                    break;
            }
        }

        options.Config = config;
        return true;
    }

    private static bool Apply(AgentConfig config, string key, string? value, ref string error)
    {
        if (value is null) { error = $"--{key} needs a value"; return false; }
        if (!config.TrySet(key, value, out var reason)) { error = reason; return false; }
        return true;
    }
}
