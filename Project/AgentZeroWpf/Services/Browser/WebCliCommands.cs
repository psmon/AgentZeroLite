using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentZeroWpf.Services.Browser;

/// <summary>
/// <c>AgentZeroLite.exe -cli web &lt;verb&gt;</c> — the Browser page from another process (M0032).
/// Unlike the <c>os</c> group these round-trip to the live GUI (the tabs are there), so
/// they follow the terminal-command shape: WM_COPYDATA in, JSON out of a memory-mapped
/// file, printed verbatim. The wearable host is the main caller; a person can use it to
/// drive the page from a shell.
///
/// <para>Two things differ from the older commands because the GUI answers <i>asynchronously</i>
/// (a navigation takes seconds): the handler clears the response map before it starts, and
/// each request carries a <c>req</c> id that the reply must echo — so a poll can never
/// pick up the previous command's page.</para>
/// </summary>
internal static class WebCliCommands
{
    public const string MmfName = "AgentZeroLite_Web_Response";
    public const int MmfSize = 256 * 1024;

    /// <summary>Page loads take longer than the CLI's 5 s default; applied unless the caller set --timeout.</summary>
    private const int DefaultTimeoutMs = 45_000;

    public static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }
        var verb = args[0].ToLowerInvariant();
        if (verb is "help" or "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        var payload = new JsonObject
        {
            ["command"] = "web",
            ["verb"] = verb,
            ["req"] = Guid.NewGuid().ToString("n"),
        };

        var positional = new List<string>();
        int tab = 0, max = 5, maxChars = 0;
        string mode = "summary", find = "";
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--tab" when i + 1 < args.Length:
                    int.TryParse(args[++i], out tab); break;
                case "--max" when i + 1 < args.Length:
                    int.TryParse(args[++i], out max); break;
                case "--max-chars" when i + 1 < args.Length:
                    int.TryParse(args[++i], out maxChars); break;
                case "--mode" when i + 1 < args.Length:
                    mode = args[++i]; break;
                case "--find" when i + 1 < args.Length:
                    find = args[++i]; break;
                default:
                    positional.Add(args[i]); break;
            }
        }

        switch (verb)
        {
            case "open":
                if (positional.Count == 0)
                {
                    Console.Error.WriteLine("Usage: web open <url> [--tab N]");
                    return 1;
                }
                payload["url"] = positional[0];
                payload["tab"] = tab;
                break;
            case "search":
                if (positional.Count == 0)
                {
                    Console.Error.WriteLine("Usage: web search <query...> [--max N]");
                    return 1;
                }
                payload["query"] = string.Join(" ", positional);
                payload["max"] = max;
                break;
            case "read":
                payload["tab"] = tab;
                payload["mode"] = mode;
                payload["find"] = find;
                payload["max_chars"] = maxChars;
                break;
            case "tabs":
                break;
            default:
                Console.Error.WriteLine($"Unknown web verb: {verb}");
                PrintUsage();
                return 1;
        }

        if (!CliHandler.TimeoutExplicit)
            CliHandler.TimeoutMs = Math.Max(CliHandler.TimeoutMs, DefaultTimeoutMs);

        var window = CliHandler.FindAgentZero();
        if (window == IntPtr.Zero) return 1;
        if (!CliHandler.SendWpfCommand(window, payload.ToJsonString())) return 1;

        var req = payload["req"]!.GetValue<string>();
        var json = CliHandler.TryReadMmf(MmfName, MmfSize, text => text.Contains(req, StringComparison.Ordinal));
        if (json is null) return CliHandler.NoWait ? 0 : 1;

        Console.WriteLine(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean() ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: AgentZeroLite.exe -cli web <verb> [options]");
        Console.WriteLine();
        Console.WriteLine("Drives the GUI's Browser page (M0032). The GUI must be running.");
        Console.WriteLine();
        Console.WriteLine("Verbs:");
        Console.WriteLine("  open <url> [--tab N]                 Open a URL (tab 0 = new tab); prints tab id + page summary");
        Console.WriteLine("  search <query...> [--max N]          Search the web; prints {title,url,snippet} results");
        Console.WriteLine("  read [--tab N] [--mode summary|links|find] [--find <kw>] [--max-chars N]");
        Console.WriteLine("                                       Read an open tab (tab 0 = the active one)");
        Console.WriteLine("  tabs                                 List open tabs");
        Console.WriteLine();
        Console.WriteLine("Output is one JSON object; exit code 0 when ok is true.");
        Console.WriteLine("Default --timeout for this group is 45000 ms (page loads are slow).");
    }
}
