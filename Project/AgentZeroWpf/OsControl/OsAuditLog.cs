using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentZeroWpf.OsControl;

/// <summary>
/// Append-only JSONL audit trail for every OS-control invocation, regardless
/// of whether the call came from the CLI, the LLM toolbelt, or an internal
/// E2E script. Each call lands as one line under
/// <c>tmp/os-cli/audit/{yyyy-MM-dd}.jsonl</c>. Per-day file rotation keeps
/// each file small enough to grep without truncation.
///
/// Mission M0014 design: prompt-injection-aware harness identity requires
/// a forensic record of every Win32 side-effect attempted, so a tampered or
/// hijacked LLM session leaves traces operator can inspect after the fact.
/// </summary>
internal static class OsAuditLog
{
    private static readonly object Lock = new();

    public enum Caller
    {
        Cli,
        Llm,
        E2e,
    }

    public static void Record(Caller caller, string verb, object? args = null, bool ok = true, string? error = null)
    {
        try
        {
            var ts = DateTimeOffset.Now;
            var dayFile = Path.Combine(OsControlPaths.AuditDir(), $"{ts:yyyy-MM-dd}.jsonl");

            var entry = new
            {
                ts = ts.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
                caller = caller.ToString().ToLowerInvariant(),
                verb,
                args,
                ok,
                error,
            };
            var line = JsonSerializer.Serialize(entry);

            lock (Lock)
            {
                File.AppendAllText(dayFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Audit failures must never break the actual operation. The
            // operator's primary defense is the file existing at all; a
            // single skipped entry under disk pressure is preferable to
            // a crash inside an LLM tool turn.
        }
    }
}
