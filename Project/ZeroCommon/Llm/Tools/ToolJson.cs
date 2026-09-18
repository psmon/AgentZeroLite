using System.Text.Encodings.Web;
using System.Text.Json;

namespace Agent.Common.Llm.Tools;

/// <summary>
/// Serialization defaults for tool-result envelopes that carry human text (file contents,
/// web pages). <see cref="JsonSerializer"/>'s default encoder turns every non-ASCII
/// character into <c>\uXXXX</c>, which quadruples the tokens a Korean page costs the model
/// for no safety gain — the envelope is fed to a model, never to a browser.
/// </summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Fail(string error) => JsonSerializer.Serialize(new { ok = false, error }, Options);
}
