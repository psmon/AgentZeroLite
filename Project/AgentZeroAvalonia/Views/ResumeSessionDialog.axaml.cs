using Avalonia.Controls;
using Avalonia.Input;
using Agent.Common.Agents;

namespace AgentZeroAvalonia.Views;

/// <summary>One row of <see cref="ResumeSessionDialog"/>.</summary>
public sealed class ResumeSessionItem
{
    public ResumeSessionItem(AgentSessionInfo session, DateTime nowUtc, bool isTabLast = false)
    {
        Session = session;
        IsTabLast = isTabLast;
        Headline = session.Headline;
        // The first prompt under the title — unless the title already is that prompt.
        Detail = !string.IsNullOrWhiteSpace(session.Title) && !string.IsNullOrWhiteSpace(session.Preview)
                 && session.Preview != session.Title ? session.Preview! : "";
        When = Ago(nowUtc - session.LastActivityUtc, session.LastActivityUtc);
        Meta = $"{Size(session.SizeBytes)} · {(session.Id.Length > 8 ? session.Id[..8] : session.Id)}";
    }

    public AgentSessionInfo Session { get; }

    /// <summary>The conversation the tab being started last ran (<see cref="TabSessionMemory"/>).</summary>
    public bool IsTabLast { get; }
    public string Headline { get; }
    public string Detail { get; }
    public bool HasDetail => Detail.Length > 0;
    public string When { get; }
    public string Meta { get; }

    internal static string Ago(TimeSpan age, DateTime utc)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(7)) return $"{(int)age.TotalDays} d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    internal static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";
}

/// <summary>What the person chose: a session to reopen (null = start a new one), and whether to stop asking.</summary>
public sealed record ResumeChoice(string? SessionId, bool DontAskAgain);

/// <summary>
/// Asked before a Claude / Codex tab's process starts: the sessions the agent already
/// recorded for the workspace folder, newest first and pre-selected, or a new session.
/// "Don't ask again" belongs to the new-session answer — it turns the question off for
/// that agent (Settings → Terminal turns it back on), so ticking it disables Resume rather
/// than meaning "always resume something".
///
/// <para>When the tab remembers the conversation it last ran and that one is still in the list,
/// it is offered above the list with its own default button, and marked in the list: with several
/// agent tabs in one workspace the newest entry is often another tab's, and the list alone gave
/// no way to tell which one was this tab's.</para>
/// </summary>
public partial class ResumeSessionDialog : Window
{
    private bool _answered;

    public ResumeSessionDialog() => InitializeComponent();

    /// <param name="toolName">Which agent (and account) the sessions belong to — "Claude (claude-sam)".</param>
    /// <param name="askScope">What "don't ask again" turns off; the setting is per tool, not per account,
    /// so a wrapper's dialog names the tool. Defaults to <paramref name="toolName"/>.</param>
    /// <param name="tabLast">The tab's own last conversation, one of <paramref name="sessions"/>, or null.</param>
    public ResumeSessionDialog(string toolName, string where, IReadOnlyList<AgentSessionInfo> sessions,
        string? askScope = null, AgentSessionInfo? tabLast = null) : this()
    {
        Title = $"Resume {toolName} session";
        HeadingText.Text = $"Resume a previous {toolName} session?";
        WhereText.Text = $"{where} · {sessions.Count} session(s) recorded for this folder, newest first";
        DontAskBox.Content = $"Don't ask again for {askScope ?? toolName} — always start a new session";

        var now = DateTime.UtcNow;
        var items = sessions.Select(s => new ResumeSessionItem(s, now, ReferenceEquals(s, tabLast))).ToList();
        SessionList.ItemsSource = items;
        var mine = items.FindIndex(i => i.IsTabLast);
        SessionList.SelectedIndex = Math.Max(0, mine);
        SessionList.DoubleTapped += (_, _) => Resume();

        if (mine >= 0)
        {
            var item = items[mine];
            TabLastPanel.IsVisible = true;
            TabLastHeadline.Text = item.Headline;
            TabLastMeta.Text = $"{item.When} · {item.Meta}" + (item.HasDetail ? $" · {item.Detail}" : "");
            // The tab's own session is the default answer; "Resume selected" stays for the list.
            ResumeButton.Classes.Remove("accent");
            ResumeButton.IsDefault = false;
            ContinueButton.IsDefault = true;
            ContinueButton.Click += (_, _) => Finish(new ResumeChoice(item.Session.Id, false));
            Opened += (_, _) => SessionList.ScrollIntoView(mine);
        }

        ResumeButton.Click += (_, _) => Resume();
        NewButton.Click += (_, _) => Finish(new ResumeChoice(null, DontAskBox.IsChecked == true));
        DontAskBox.IsCheckedChanged += (_, _) =>
        {
            var enabled = DontAskBox.IsChecked != true;
            ResumeButton.IsEnabled = enabled;
            ContinueButton.IsEnabled = enabled;
            if (mine >= 0) ContinueButton.IsDefault = enabled;
            else ResumeButton.IsDefault = enabled;
            NewButton.IsDefault = !enabled;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(new ResumeChoice(null, false));
        };
        // The close box is "not now": a new session, the question stays on.
        Closing += (_, _) =>
        {
            if (_answered) return;
            _answered = true;
            Result = new ResumeChoice(null, false);
        };
        Opened += (_, _) => SessionList.Focus();
    }

    public ResumeChoice Result { get; private set; } = new(null, false);

    private void Resume()
    {
        if (DontAskBox.IsChecked == true) return;
        if (SessionList.SelectedItem is not ResumeSessionItem item) return;
        Finish(new ResumeChoice(item.Session.Id, false));
    }

    private void Finish(ResumeChoice choice)
    {
        if (_answered) return;
        _answered = true;
        Result = choice;
        Close(choice);
    }
}
