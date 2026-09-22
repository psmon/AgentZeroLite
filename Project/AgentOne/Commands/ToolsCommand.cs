using AgentOne.Tools;

namespace AgentOne.Commands;

/// <summary>
/// Prints the catalog the model is handed. Useful on its own, and the fastest
/// way to see whether a build actually carries the verb you just added.
/// </summary>
public sealed class ToolsCommand
{
    public int Execute(string[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        var sub = args.Length > 0 ? args[0] : "list";
        switch (sub)
        {
            case "list":
                foreach (var group in ToolCatalog.All.GroupBy(t => t.Family))
                {
                    Console.WriteLine($"[{group.Key}]");
                    foreach (var tool in group)
                        Console.WriteLine($"  {tool.Name,-12} ({string.Join(", ", tool.Args)})  {tool.Summary}");
                }
                return 0;

            case "show":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("agent-one tools show: needs a tool name");
                    return 2;
                }
                var found = ToolCatalog.Find(args[1]);
                if (found is null)
                {
                    Console.Error.WriteLine($"agent-one tools show: unknown tool '{args[1]}'");
                    return 2;
                }
                Console.WriteLine($"name:    {found.Name}");
                Console.WriteLine($"family:  {found.Family}");
                Console.WriteLine($"args:    {string.Join(", ", found.Args)}");
                Console.WriteLine($"summary: {found.Summary}");
                Console.WriteLine($"example: {found.Example}");
                return 0;

            case "prompt":
                Console.WriteLine(Agent.SystemPrompt.Build(Directory.GetCurrentDirectory()));
                return 0;

            default:
                Console.Error.WriteLine($"agent-one tools: unknown subcommand '{sub}'");
                PrintHelp();
                return 2;
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            agent-one tools list          List the verbs the agent can call (default)
            agent-one tools show <name>   Detail for one verb
            agent-one tools prompt        Print the exact system prompt the model receives
            """);
    }
}
