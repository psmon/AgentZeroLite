using Akka;
using Akka.Streams;
using Akka.Streams.Dsl;
using Akka.Streams.Stage;
using System.Text;

namespace Agent.Common.Voice.Streams;

/// <summary>
/// Accumulates an LLM token stream into TTS-sized text chunks. Built as a
/// custom <see cref="GraphStage{TShape}"/> for the same reason
/// <see cref="VoiceSegmenterStage"/> is one — Akka.NET 1.5's FlowOperations
/// doesn't expose <c>StatefulSelectMany</c>, and the stage logic is tight
/// enough that owning the inlet/outlet handlers is cleaner than wrapping
/// a Scan/SelectMany combo.
///
/// Chunking rules:
///   - Emit on sentence terminator (<c>.</c>, <c>?</c>, <c>!</c>) when the
///     accumulated buffer exceeds <see cref="MinChunkChars"/>. This keeps
///     "Yes." or "No." from triggering a tiny TTS request.
///   - Emit on a hard newline (<c>\n</c>) regardless of length — paragraph
///     breaks read better as separate utterances.
///   - Emit when the buffer exceeds <see cref="MaxChunkChars"/> at the next
///     whitespace boundary, so a runaway sentence (rare) doesn't stall TTS.
///   - On upstream completion: flush any remaining text as one final chunk.
///
/// Token boundaries (incoming string sizes) are unconstrained — the LLM may
/// emit single characters, partial words, or whole sentences per token. The
/// stage handles all by appending into a <see cref="StringBuilder"/> first,
/// then scanning for boundaries.
/// </summary>
public sealed class SentenceChunkerStage : GraphStage<FlowShape<string, string>>
{
    public const int DefaultMinChunkChars = 24;
    public const int DefaultMaxChunkChars = 240;

    public Inlet<string> In { get; } = new("SentenceChunker.in");
    public Outlet<string> Out { get; } = new("SentenceChunker.out");
    public override FlowShape<string, string> Shape { get; }

    public int MinChunkChars { get; }
    public int MaxChunkChars { get; }

    public SentenceChunkerStage(int minChunkChars = DefaultMinChunkChars, int maxChunkChars = DefaultMaxChunkChars)
    {
        if (minChunkChars < 1) throw new ArgumentOutOfRangeException(nameof(minChunkChars));
        if (maxChunkChars <= minChunkChars) throw new ArgumentOutOfRangeException(nameof(maxChunkChars));
        MinChunkChars = minChunkChars;
        MaxChunkChars = maxChunkChars;
        Shape = new FlowShape<string, string>(In, Out);
    }

    protected override GraphStageLogic CreateLogic(Attributes inheritedAttributes) => new Logic(this);

    private sealed class Logic : InAndOutGraphStageLogic
    {
        private readonly SentenceChunkerStage _stage;
        private readonly StringBuilder _buffer = new();
        private readonly Queue<string> _ready = new();

        public Logic(SentenceChunkerStage stage) : base(stage.Shape)
        {
            _stage = stage;
            SetHandler(stage.In, this);
            SetHandler(stage.Out, this);
        }

        public override void OnPush()
        {
            var token = Grab(_stage.In);
            if (!string.IsNullOrEmpty(token))
            {
                _buffer.Append(token);
                ScanAndQueueChunks();
            }
            DispatchOrPullMore();
        }

        public override void OnUpstreamFinish()
        {
            // Flush remaining buffer — even if it doesn't end at a sentence
            // boundary, the LLM is done; this is the last chunk to speak.
            var tail = _buffer.ToString().Trim();
            _buffer.Clear();
            if (tail.Length > 0) _ready.Enqueue(tail);
            // If a downstream pull is pending and we have a chunk, push.
            if (IsAvailable(_stage.Out) && _ready.Count > 0)
                Push(_stage.Out, _ready.Dequeue());
            // Otherwise hand off — completion will be observed when the queue
            // drains: we override OnPull to push then complete.
            if (_ready.Count == 0)
                CompleteStage();
        }

        public override void OnPull()
        {
            if (_ready.Count > 0)
            {
                Push(_stage.Out, _ready.Dequeue());
                if (_ready.Count == 0 && IsClosed(_stage.In))
                    CompleteStage();
                return;
            }
            if (IsClosed(_stage.In))
            {
                CompleteStage();
                return;
            }
            Pull(_stage.In);
        }

        private void DispatchOrPullMore()
        {
            if (_ready.Count > 0 && IsAvailable(_stage.Out))
            {
                Push(_stage.Out, _ready.Dequeue());
                return;
            }
            Pull(_stage.In);
        }

        private void ScanAndQueueChunks()
        {
            while (true)
            {
                var idx = FindChunkEnd(_buffer);
                if (idx < 0) return;
                var chunk = _buffer.ToString(0, idx).Trim();
                _buffer.Remove(0, idx);
                if (chunk.Length > 0) _ready.Enqueue(chunk);
            }
        }

        /// <summary>
        /// Return the index (exclusive) at which the next chunk should be cut,
        /// or -1 if no good boundary is in the current buffer. Scans:
        ///   1. First newline → cut after it (regardless of length).
        ///   2. First sentence terminator with following whitespace/end →
        ///      cut after the terminator IF buffer length ≥ MinChunkChars.
        ///   3. If buffer ≥ MaxChunkChars, fall back to last whitespace.
        /// </summary>
        private int FindChunkEnd(StringBuilder b)
        {
            var len = b.Length;
            for (int i = 0; i < len; i++)
            {
                if (b[i] == '\n') return i + 1;
            }
            if (len >= _stage.MinChunkChars)
            {
                for (int i = _stage.MinChunkChars - 1; i < len; i++)
                {
                    var c = b[i];
                    if (c == '.' || c == '?' || c == '!')
                    {
                        // Need either end-of-buffer or whitespace right after
                        // to avoid cutting "1.5" or "Mr.":
                        if (i + 1 >= len || char.IsWhiteSpace(b[i + 1]))
                            return i + 1;
                    }
                }
            }
            if (len >= _stage.MaxChunkChars)
            {
                for (int i = len - 1; i > 0; i--)
                {
                    if (char.IsWhiteSpace(b[i])) return i + 1;
                }
                return len; // no whitespace found — cut at the boundary anyway
            }
            return -1;
        }
    }
}

/// <summary>Convenience facade for callsites.</summary>
public static class SentenceChunkerFlow
{
    public static Flow<string, string, NotUsed> Create(int minChunkChars = SentenceChunkerStage.DefaultMinChunkChars,
                                                        int maxChunkChars = SentenceChunkerStage.DefaultMaxChunkChars)
        => Flow.FromGraph(new SentenceChunkerStage(minChunkChars, maxChunkChars));
}
