using System.Text;

namespace AgentOne.Tui;

/// <summary>What the shell must do after a keystroke the model already absorbed.</summary>
public enum ChatEffect
{
    None,
    /// <summary>The input line is ready; take it with <see cref="ChatTuiModel.TakeInput"/>.</summary>
    Submit,
    ToggleMode,
    ScrollUp,
    ScrollDown,
    /// <summary>Back to the live end of the transcript (Ctrl+End).</summary>
    ScrollToBottom,
    /// <summary>Print the session's status block (F2).</summary>
    ShowStatus,
    Quit
}

/// <summary>
/// The chat screen's own state — the line being typed and what the bars say —
/// with no Termina in it, so the key map is unit-tested like the config
/// screen's. The conversation itself lives in <see cref="Agent.ChatSession"/>;
/// this only knows enough about it to label the header.
/// </summary>
public sealed class ChatTuiModel
{
    private readonly StringBuilder _input = new();

    public ChatTuiModel(bool smart, bool smartAvailable)
    {
        Smart = smart;
        SmartAvailable = smartAvailable;
        Status = smartAvailable
            ? "Enter sends · Shift+Tab basic/smart · F2 status · wheel/PageUp/PageDown scroll · Esc clears, Ctrl+D quits"
            : "Enter sends · F2 status · wheel/PageUp/PageDown scroll · Esc clears, Ctrl+D quits  (no TypeSafe key — smart unavailable)";
    }

    public string Input => _input.ToString();
    public int Cursor { get; private set; }

    /// <summary>A turn is running; Enter is refused until it finishes or pauses.</summary>
    public bool Busy { get; private set; }

    /// <summary>A turn is waiting for the person to answer.</summary>
    public bool AwaitingPerson { get; private set; }

    public bool Smart { get; private set; }
    public bool SmartAvailable { get; }
    public string Status { get; private set; }
    public int Turns { get; private set; }

    /// <summary>Armed by Ctrl+D or Esc-on-empty; a second one quits.</summary>
    public bool QuitArmed { get; private set; }

    /// <summary>True while the reader is away from the live end of the transcript.</summary>
    public bool ScrolledUp { get; private set; }

    /// <summary>How many lines above the live end the view is.</summary>
    public int ScrollOffset { get; private set; }

    /// <summary>The page reports where the transcript is. True when it moved.</summary>
    public bool SetScrolled(bool scrolledUp, int offset)
    {
        if (scrolledUp == ScrolledUp && offset == ScrollOffset) return false;
        ScrolledUp = scrolledUp;
        ScrollOffset = offset;
        return true;
    }

    public void SetBusy(bool busy, string? status = null)
    {
        Busy = busy;
        if (status is not null) Status = status;
    }

    public void SetAwaitingPerson(bool awaiting) => AwaitingPerson = awaiting;

    public void SetSmart(bool smart) => Smart = smart;

    public void SetStatus(string status) => Status = status;

    public void CountTurn() => Turns++;

    /// <summary>Hands the line over and clears it.</summary>
    public string TakeInput()
    {
        var text = _input.ToString().Trim();
        _input.Clear();
        Cursor = 0;
        return text;
    }

    public ChatEffect HandleKey(ConsoleKeyInfo key)
    {
        // Any key other than a second quit disarms the first.
        if (key.Key is not (ConsoleKey.D or ConsoleKey.Escape)) QuitArmed = false;

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                if (Busy)
                {
                    Status = "still working — wait for it to finish or pause";
                    return ChatEffect.None;
                }
                if (_input.ToString().Trim().Length == 0 && !AwaitingPerson) return ChatEffect.None;
                return ChatEffect.Submit;

            case ConsoleKey.Tab when key.Modifiers.HasFlag(ConsoleModifiers.Shift):
                return ChatEffect.ToggleMode;

            case ConsoleKey.Tab:
                return ChatEffect.None;

            case ConsoleKey.PageUp:
                return ChatEffect.ScrollUp;

            case ConsoleKey.PageDown:
                return ChatEffect.ScrollDown;

            case ConsoleKey.End when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return ChatEffect.ScrollToBottom;

            case ConsoleKey.F2:
                return ChatEffect.ShowStatus;

            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return ArmQuit();

            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                return ChatEffect.Quit;

            case ConsoleKey.Escape:
                if (_input.Length > 0)
                {
                    _input.Clear();
                    Cursor = 0;
                    return ChatEffect.None;
                }
                return ArmQuit();

            case ConsoleKey.Backspace when Cursor > 0:
                _input.Remove(Cursor - 1, 1);
                Cursor--;
                return ChatEffect.None;

            case ConsoleKey.Delete when Cursor < _input.Length:
                _input.Remove(Cursor, 1);
                return ChatEffect.None;

            case ConsoleKey.LeftArrow when Cursor > 0:
                Cursor--;
                return ChatEffect.None;

            case ConsoleKey.RightArrow when Cursor < _input.Length:
                Cursor++;
                return ChatEffect.None;

            case ConsoleKey.Home:
                Cursor = 0;
                return ChatEffect.None;

            case ConsoleKey.End:
                Cursor = _input.Length;
                return ChatEffect.None;
        }

        if (char.IsControl(key.KeyChar) || key.KeyChar == '\0') return ChatEffect.None;

        _input.Insert(Cursor, key.KeyChar);
        Cursor++;
        return ChatEffect.None;
    }

    private ChatEffect ArmQuit()
    {
        if (QuitArmed) return ChatEffect.Quit;
        QuitArmed = true;
        Status = "press again to quit";
        return ChatEffect.None;
    }
}
