using System.Text;

namespace ZeroWearable;

/// <summary>
/// Tees everything this process prints into
/// <c>%LOCALAPPDATA%\AgentZeroLite\logs\wearable-host.log</c>.
///
/// <para>Until this existed the host's output went one place only: the pipe the GUI's
/// Wearable panel reads into an in-memory TextBox. That is fine while you are watching it
/// and useless for everything this host actually fails at — a watch that reboots at 3 a.m.,
/// a link that drops after an hour, an Akka association that refuses to come back. By the
/// time anyone asks "what did it say?", the answer has been scrolled away or the GUI has
/// been restarted. A file survives both.</para>
///
/// <para>It tees rather than replaces, because the pipe is load-bearing: the panel looks for
/// <c>[host/ready]</c> at the <i>start</i> of a line and for <c>/error]</c> anywhere in it.
/// So stdout keeps its exact bytes and the timestamp is added on the file side only.
/// Installing it on <see cref="Console.Out"/> also catches Akka's own logger, which writes
/// through the console — the remoting lifecycle lines are precisely what a device-reboot
/// post-mortem needs.</para>
/// </summary>
public static class HostLog
{
    /// <summary>Roll at 4 MB, keeping one previous file. Two runs' worth of INFO is well inside it.</summary>
    private const long MaxBytes = 4L * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Where the log ended up, or null when it could not be opened.</summary>
    public static string? FilePath { get; private set; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite", "logs", "wearable-host.log");

    /// <summary>
    /// Opens the file and wraps stdout/stderr. Safe to call once; a failure is reported and
    /// then ignored, because a host that cannot write a log is still a host that should run.
    /// </summary>
    public static string? Install(string? explicitPath = null)
    {
        if (_file != null) return FilePath;
        try
        {
            var path = explicitPath ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Rotate(path);

            // FileShare.ReadWrite so the file can be tailed while the host runs.
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;

            Console.SetOut(new TeeWriter(Console.Out));
            Console.SetError(new TeeWriter(Console.Error));
            return path;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[host/warn] no log file: {ex.Message}");
            return null;
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            var previous = Path.ChangeExtension(path, ".1.log");
            File.Delete(previous);
            File.Move(path, previous);
        }
        catch
        {
            // A locked or missing file is not worth failing startup over; append to it as is.
        }
    }

    /// <summary>
    /// Passes the original bytes straight through and keeps a private line buffer for the
    /// file copy, so only the file gets a timestamp.
    /// </summary>
    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly StringBuilder _pending = new();

        public TeeWriter(TextWriter inner) => _inner = inner;

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            lock (Gate) Absorb(value);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            _inner.Write(value);
            lock (Gate)
            {
                foreach (var c in value) Absorb(c);
            }
        }

        public override void Flush() => _inner.Flush();

        /// <summary>Called under <see cref="Gate"/>.</summary>
        private void Absorb(char value)
        {
            if (value == '\r') return;
            if (value != '\n')
            {
                _pending.Append(value);
                return;
            }
            var text = _pending.ToString();
            _pending.Clear();
            try
            {
                _file?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}");
            }
            catch
            {
                // Disk full, file deleted underneath us: the console copy still went out.
            }
        }
    }
}
