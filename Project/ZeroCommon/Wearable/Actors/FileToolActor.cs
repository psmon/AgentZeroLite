using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Agent.Common.Llm.Tools;

namespace Agent.Common.Wearable.Actors;

/// <summary>
/// The file side of the watch's toolbelt as an actor (M0032): every request resolves
/// through the allow-list, runs, and answers the sender with the JSON envelope the model
/// will see. Disk work happens on this actor's thread, not the agent loop's, and the
/// whole surface — including <see cref="Open"/>, which hands a file to the shell, and
/// <see cref="StopMedia"/>, which takes it back — sits behind one mailbox that can be
/// watched, stopped and tested in isolation.
/// </summary>
public sealed class FileToolActor : ReceiveActor
{
    public sealed record Read(string Path, int MaxBytes);
    public sealed record Write(string Path, string Content);
    public sealed record Edit(string Path, string Old, string New, bool ReplaceAll);
    public sealed record Grep(string Pattern, string? PathFilter, int MaxResults);
    public sealed record ListFiles(string? PathFilter, int MaxEntries);
    public sealed record Find(string? Query, string? Kind, int MaxResults);
    public sealed record Open(string Path);
    public sealed record StopMedia;

    private readonly ILoggingAdapter _log = Context.GetLogger();
    private readonly AllowedRootResolver _roots;
    private readonly Func<string, Process?> _launch;
    private readonly Func<bool>? _stopFallback;
    private readonly MediaPlaybackTracker _media;

    /// <param name="launch">
    /// How a classified, resolved file is opened; returns the started process when the
    /// shell created one. Defaults to ShellExecute; tests inject a recorder so no player
    /// ever starts.
    /// </param>
    /// <param name="stopFallback">
    /// The host's system-wide media-stop key, used when no player process can be found.
    /// user32 stays outside ZeroCommon, so the host supplies it.
    /// </param>
    public FileToolActor(AllowedRootResolver roots, Func<string, Process?>? launch = null,
        Func<bool>? stopFallback = null, MediaPlaybackTracker? media = null)
    {
        _roots = roots;
        _launch = launch ?? ShellOpen;
        _stopFallback = stopFallback;
        _media = media ?? new MediaPlaybackTracker();

        Receive<Read>(m => Reply("read_file", m.Path, () => AllowedRootFileTools.ReadFile(_roots, m.Path, m.MaxBytes)));
        Receive<Write>(m => Reply("write_file", m.Path, () => AllowedRootFileTools.WriteFile(_roots, m.Path, m.Content)));
        Receive<Edit>(m => Reply("edit_file", m.Path, () => AllowedRootFileTools.Edit(_roots, m.Path, m.Old, m.New, m.ReplaceAll)));
        Receive<Grep>(m => Reply("grep", m.Pattern, () => AllowedRootFileTools.Grep(_roots, m.Pattern, m.PathFilter, m.MaxResults)));
        Receive<ListFiles>(m => Reply("list_files", m.PathFilter ?? "", () => AllowedRootFileTools.ListFiles(_roots, m.PathFilter, m.MaxEntries)));
        Receive<Find>(m => Reply("find_files", $"{m.Kind}:{m.Query}", () => AllowedRootFileTools.FindFiles(_roots, m.Query, m.Kind, m.MaxResults)));
        Receive<Open>(m => Reply("open_file", m.Path, () => OpenFile(m.Path)));
        Receive<StopMedia>(_ => Reply("stop_media", "", StopPlayback));
    }

    private void Reply(string tool, string subject, Func<string> body)
    {
        string result;
        try
        {
            result = body();
        }
        catch (Exception ex)
        {
            // A failing tool is information the model can act on, not a failed request.
            _log.Warning("{0} threw: {1}", tool, ex.Message);
            result = ToolJson.Fail(ex.Message);
        }
        _log.Debug("{0}({1}) -> {2} chars", tool, Head(subject, 60), result.Length);
        Sender.Tell(result, Self);
    }

    /// <summary>
    /// Three gates before the shell sees a path: it must resolve inside an allowed root,
    /// its extension must be on <see cref="FileOpenPolicy"/>'s allow-list, and it must
    /// exist. The envelope carries the alias form back so the model can refer to it.
    /// </summary>
    private string OpenFile(string path)
    {
        if (!_roots.TryResolve(path, out var root, out var rel, out var error)) return ToolJson.Fail(error);
        if (!FileOpenPolicy.TryClassify(rel, out var kind, out error)) return ToolJson.Fail(error);
        if (!FileToolCore.TryResolveInsideRoot(root.Path, rel, out var full, out error)) return ToolJson.Fail(error);
        if (!File.Exists(full)) return ToolJson.Fail("file not found");

        var aliasPath = AllowedRootResolver.Prefix(root.Alias, rel);
        Process? process;
        try
        {
            process = _launch(full);
        }
        catch (Exception ex)
        {
            return ToolJson.Fail($"the system could not open the file: {ex.Message}");
        }

        if (kind == FileOpenPolicy.KindMedia) _media.Record(process, aliasPath);
        else process?.Dispose();

        _log.Info("opened {0} ({1}) with the default program", aliasPath, kind);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            opened = true,
            path = aliasPath,
            kind,
            note = kind == FileOpenPolicy.KindMedia
                ? "now playing on the PC (say so; stop_media stops it)"
                : "shown on the PC",
        }, ToolJson.Options);
    }

    private string StopPlayback()
    {
        var was = _media.LastPath;
        var (stopped, how) = _media.Stop(_stopFallback);
        if (!stopped)
        {
            _log.Info("stop_media: {0}", how);
            return ToolJson.Fail(how);
        }
        _log.Info("stop_media: {0} ({1})", how, was ?? "-");
        return JsonSerializer.Serialize(new { ok = true, stopped = true, path = was, detail = how }, ToolJson.Options);
    }

    private static Process? ShellOpen(string fullPath)
        => Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });

    private static string Head(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
