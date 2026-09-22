using System.Text;

namespace AgentOne.Services;

/// <summary>
/// Reads redirected stdin as UTF-8, once, for everybody.
///
/// <c>Console.In</c> decodes a redirected stream with the console's code page,
/// which turns piped Korean into mojibake on any Windows box running a legacy
/// ANSI page. That was fixed once in `run` and then reappeared in chat, which is
/// the argument for a single reader rather than a fix per call site.
///
/// It is shared and never disposed on purpose: several commands read from stdin
/// across the life of a process, and a reader disposed by the first of them
/// closes the stream for the rest.
/// </summary>
public static class StandardInput
{
    private static readonly Lazy<TextReader> Reader = new(() =>
        new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));

    /// <summary>The next line, or null at end of input.</summary>
    public static Task<string?> ReadLineAsync(CancellationToken ct) =>
        Reader.Value.ReadLineAsync(ct).AsTask();

    /// <summary>Everything still to come.</summary>
    public static Task<string> ReadToEndAsync(CancellationToken ct) =>
        Reader.Value.ReadToEndAsync(ct);

    /// <summary>The next line, blocking. For the places that are not async.</summary>
    public static string? ReadLine() => Reader.Value.ReadLine();
}
