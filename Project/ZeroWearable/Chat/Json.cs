using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ZeroWearable.Chat;

/// <summary>
/// Tiny JSON writer for the device protocol. Deliberately not
/// <c>JsonSerializer.Serialize(object)</c>: that needs reflection or a source
/// generator, and the payloads here are five fields wide.
///
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> matters - the default
/// encoder turns every Korean character into \uXXXX, tripling the payload and
/// pushing a reply chunk past the frame budget for no reason.
/// </summary>
public static class Json
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Splits text into chunks of at most <paramref name="maxBytes"/> UTF-8 bytes,
    /// never cutting a character (let alone a surrogate pair) in half.</summary>
    public static List<string> ChunkUtf8(string text, int maxBytes)
    {
        var chunks = new List<string>();
        if (text.Length == 0) return chunks;

        var start = 0;
        var bytes = 0;
        for (var i = 0; i < text.Length;)
        {
            var runeLength = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var runeBytes = Encoding.UTF8.GetByteCount(text.AsSpan(i, runeLength));

            if (bytes + runeBytes > maxBytes && i > start)
            {
                chunks.Add(text[start..i]);
                start = i;
                bytes = 0;
                continue;
            }
            bytes += runeBytes;
            i += runeLength;
        }
        if (start < text.Length) chunks.Add(text[start..]);
        return chunks;
    }
}
