using System.Diagnostics;
using System.Text.Json;
using AgentOne.Tools.Cli;

namespace AgentOne.Tools;

/// <summary>
/// Whether a file just written can be read by what will read it — asked at once,
/// for the formats that have a cheap, exact answer.
///
/// Measured (2026-11-16, music-harness): a 90-line Python compositor was written
/// with one stray quote after its last line (<c>full()"</c>). Running it said
/// "SyntaxError: unterminated string literal (detected at line 91)"; the model
/// rewrote the same text with the same quote three times, each a full file's
/// worth of generation, and the turn ended on the repeat guard. The file was
/// wrong the moment it was written, and the write had said "ok".
///
/// So a write that produces a file its own language cannot parse says so in its
/// result: JSON by parsing it here, Python and JavaScript by the interpreter
/// already on the machine (found, never installed; a missing interpreter means
/// no check, not a failure). It reads the file and changes nothing.
/// </summary>
public static class WriteCheck
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);

    /// <summary>Replaceable by a test: (executable name) → path or null.</summary>
    internal static Func<string, string?> Locate { get; set; } = ExecutableLocator.Find;

    /// <summary>A note to append to the write's result when the file does not parse, or null.</summary>
    public static string? Note(string root, string relativePath)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!File.Exists(full) || new FileInfo(full).Length is 0 or > 2_000_000) return null;

            var problem = Problem(full);
            if (problem is null) return null;

            // One character of the envelope left after the file's real end — the commonest form of
            // this mistake by far (measured: every first write of a 120-line script, each costing a
            // whole second generation of the file). When taking that one quote away makes the file
            // parse, it is taken away and said so; anything less certain is left to the model.
            var text = File.ReadAllText(full);
            var trimmed = text.TrimEnd();
            if (trimmed.EndsWith('"'))
            {
                File.WriteAllText(full, trimmed[..^1].TrimEnd() + "\n", new System.Text.UTF8Encoding(false));
                if (Problem(full) is null)
                    return "[note] The content ended with one stray quote after the file's last line — left over from the JSON " +
                           "envelope. It was removed and the file now parses; nothing else was changed. End the content argument " +
                           "where the file ends.";
                File.WriteAllText(full, text, new System.Text.UTF8Encoding(false));
            }

            return $"[check] The file was written, but it does NOT parse: {problem}\n" +
                   LastLineHint(full) +
                   "Nothing can run or read it as it is. Fix exactly that place and write the whole file again — " +
                   "writing the same text again fails the same way.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? Problem(string full) => Path.GetExtension(full).ToLowerInvariant() switch
    {
        ".json" => Json(full),
        ".py" => Interpreter("python", ["-c", "import ast,sys;ast.parse(open(sys.argv[1],encoding='utf-8').read(),sys.argv[1])", full]),
        ".js" or ".cjs" or ".mjs" => Interpreter("node", ["--check", full]),
        _ => null,
    };

    private static string? Json(string full)
    {
        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(full),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return null;
        }
        catch (JsonException ex)
        {
            return $"invalid JSON at line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}: {FirstLine(ex.Message)}";
        }
    }

    private static string? Interpreter(string name, string[] args)
    {
        var exe = Locate(name);
        if (exe is null) return null;

        try
        {
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";

            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Limit))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            if (process.ExitCode == 0) return null;

            // The last lines of a traceback are the error and where it is.
            var lines = stderr.GetAwaiter().GetResult()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // A store alias or a broken install answers with something that is not about this file.
            if (!lines.Any(l => l.Contains("Error", StringComparison.Ordinal))) return null;
            return string.Join(" / ", lines.TakeLast(4));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The commonest shape of this mistake, said outright: text after the code's real end —
    /// a quote or brace left over from the envelope the file travelled in.
    /// </summary>
    private static string LastLineHint(string full)
    {
        var last = File.ReadLines(full).LastOrDefault(l => l.Trim().Length > 0)?.TrimEnd() ?? "";
        return last.EndsWith('"') && last.Count(c => c == '"') % 2 == 1 || last.EndsWith("\"}") || last.EndsWith("}}")
            ? $"Its last line is `{(last.Length > 80 ? "…" + last[^80..] : last)}` — that looks like a character left over from the JSON " +
              "envelope (a stray quote or brace after the file's real end). The content argument must end where the file ends.\n"
            : "";
    }

    private static string FirstLine(string text)
    {
        var cut = text.IndexOf('\n');
        return (cut >= 0 ? text[..cut] : text).Trim();
    }
}
