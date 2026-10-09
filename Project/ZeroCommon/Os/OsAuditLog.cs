using System.Text.Json;

namespace Agent.Common.Os;

/// <summary>
/// One JSON line per OS action an agent took — who asked, what, with which arguments, and
/// whether it worked — under <c>%LOCALAPPDATA%\AgentZeroLite\logs\os-audit\yyyy-MM-dd.jsonl</c>.
/// The same idea as the WPF host's <c>OsAuditLog</c> (M0014): a click the person did not
/// make must be findable afterwards. Never throws: an audit that fails does not stop the
/// action it was recording.
/// </summary>
public sealed class OsAuditLog
{
    private readonly object _gate = new();
    private readonly string _dir;
    private readonly string _caller;

    public OsAuditLog(string caller, string? directory = null)
    {
        _caller = caller;
        _dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentZeroLite", "logs", "os-audit");
    }

    public string Directory => _dir;

    public void Record(string verb, object? args, bool ok, string? error = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now.ToString("O"),
                caller = _caller,
                verb,
                args,
                ok,
                error,
            });
            lock (_gate)
            {
                System.IO.Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"), line + Environment.NewLine);
            }
        }
        catch
        {
            // the audit is a record, not a gate
        }
    }
}
