namespace Agent.Common.Llm.Tools;

/// <summary>
/// GBNF grammar + system prompt + tool catalog text for the Gemma-style
/// (no-native-tool-calling) backend. The grammar enforces JSON shape at the
/// sampler level so the model output is always parseable; tool name set is
/// constrained to the 5-tool surface; argument types are *permissive* (any
/// string/number kv pairs) — argument validation happens at the application
/// layer and asks the model to retry on mismatch.
///
/// References:
///   - harness/knowledge/ondevice-tool-calling-survey.md (why GBNF for Gemma)
///   - harness/logs/code-coach/2026-04-25-1620-aimode-research.md (5-tool surface)
/// </summary>
public static class AgentToolGrammar
{
    public const string SystemPrompt = """
You are an on-device agent that helps the user by calling tools to inspect and
control terminal sessions. The user's request will follow this system message.

Available tools:
  - list_terminals             returns the catalog of terminal groups and tabs (no args).
  - read_terminal              returns the last N chars of a terminal's output.
                               args: { "group": <int>, "tab": <int>, "last_n": <int> }
  - send_to_terminal           writes text + Enter to a terminal.
                               args: { "group": <int>, "tab": <int>, "text": <string> }
  - send_key                   sends one control key to a terminal.
                               args: { "group": <int>, "tab": <int>,
                                       "key":   <"cr"|"lf"|"crlf"|"esc"|"tab"|"backspace"|"del"|"ctrlc"|"ctrld"|"up"|"down"|"left"|"right"> }
  - done                       end the conversation with a final message to the user.
                               args: { "message": <string> }

Rules:
  - Reply with ONE JSON object per turn. Schema: { "tool": "<name>", "args": { ... } }.
  - The schema is enforced by a grammar; do NOT add prose, code fences, or commentary.
  - When the user's request is satisfied, call "done" with a short summary.
  - Always start by inspecting state (list_terminals, read_terminal) before acting.
""";

    /// <summary>
    /// GBNF that constrains output to: <c>{"tool": "&lt;one of 5&gt;", "args": { ... }}</c>.
    /// Argument values are constrained to JSON primitives (string / int / bool); nested
    /// objects/arrays are not allowed because the 5-tool surface doesn't need them.
    /// Use rule name <c>root</c> as the start symbol when constructing
    /// <see cref="LLama.Sampling.Grammar"/>.
    /// </summary>
    public const string Gbnf = """
root         ::= ws "{" ws "\"tool\"" ws ":" ws toolname ws "," ws "\"args\"" ws ":" ws args ws "}" ws

toolname     ::= "\"list_terminals\"" | "\"read_terminal\"" | "\"send_to_terminal\"" | "\"send_key\"" | "\"done\""

args         ::= "{" ws "}" | "{" ws kv (ws "," ws kv)* ws "}"
kv           ::= string ws ":" ws value
value        ::= string | integer | boolean

string       ::= "\"" char* "\""
char         ::= [^"\\\n\r] | "\\" ["\\bfnrt/]
integer      ::= "-"? digit+
digit        ::= [0-9]
boolean      ::= "true" | "false"

ws           ::= ([ \t\n\r])*
""";

    public const string GrammarRootRule = "root";

    /// <summary>
    /// Sentinel tool name for "session is finished". When the model emits this,
    /// the loop terminates and reports the <c>message</c> arg as the final
    /// user-facing message.
    /// </summary>
    public const string DoneToolName = "done";

    /// <summary>
    /// Tool names the loop accepts. Anything outside this set is treated as a
    /// model failure (logged + counted toward retry limit).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTools = new[]
    {
        "list_terminals",
        "read_terminal",
        "send_to_terminal",
        "send_key",
        "done",
    };
}
