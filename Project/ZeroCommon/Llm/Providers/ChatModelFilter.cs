using System.Text.RegularExpressions;

namespace Agent.Common.Llm.Providers;

/// <summary>
/// Narrows a server's <c>/v1/models</c> list to the models AI mode can actually talk to.
/// The endpoint says nothing about what a model does, and OpenAI's answer mixes chat models
/// with embeddings, speech, images, moderation and dated snapshots — over a hundred names, most
/// of which fail when picked (AI mode speaks <c>/v1/chat/completions</c>). So the list is cut by
/// name: what is plainly not a chat model goes, and a dated snapshot goes when its alias is listed.
/// A model filtered out here can still be typed by hand.
/// </summary>
public static partial class ChatModelFilter
{
    // Families that are not chat-completion models, matched as a word inside the id.
    [GeneratedRegex(@"(^|[-_.:/])(embed\w*|embedding\w*|rerank\w*|whisper|tts|speech|transcribe|audio|realtime|dall-e|image|moderation|sora|davinci|babbage|deep-research|computer-use)([-_.:/]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotChat();

    // OpenAI-only names: the legacy completions model (gpt-3.5-turbo-instruct — "instruct" alone
    // would also drop every local "llama-…-instruct" chat model) and the models served only by the
    // Responses API, which AI mode does not speak.
    [GeneratedRegex(@"^gpt-[\w.-]+-instruct(-|$)|^(o\d[\w.]*|gpt-[\w.-]+)-pro(-|$)|^(gpt-[\w.-]+-)?codex", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResponsesOnly();

    // "-2024-08-06" or a four-digit "-0613" / "-1106" at the end: a pinned snapshot.
    [GeneratedRegex(@"-(\d{4}-\d{2}-\d{2}|\d{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex DatedSuffix();

    /// <summary>True when the name is plausibly a chat model.</summary>
    public static bool IsChatModel(string id) =>
        !string.IsNullOrWhiteSpace(id) && !NotChat().IsMatch(id) && !ResponsesOnly().IsMatch(id);

    /// <summary>The chat models, sorted, with a dated snapshot dropped when its alias is also listed.</summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> ids)
    {
        var chat = ids.Where(IsChatModel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = new HashSet<string>(chat, StringComparer.OrdinalIgnoreCase);
        return chat
            .Where(id => DatedSuffix().Match(id) is not { Success: true } m || !names.Contains(id[..m.Index]))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
