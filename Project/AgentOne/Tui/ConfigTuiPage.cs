using System.Text;
using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace AgentOne.Tui;

/// <summary>
/// Renders <see cref="ConfigTuiModel"/>. Pure projection — the page reads state
/// and never changes it, so what you see is exactly what the tested model says.
/// </summary>
public sealed class ConfigTuiPage : ReactivePage<ConfigTuiViewModel>
{
    /// <summary>
    /// Width of the key column: the longest key plus a gap, computed rather
    /// than typed — a constant of 16 once sat exactly on `reasoningBaseUrl`
    /// and glued the value to the name.
    /// </summary>
    private static readonly int KeyColumn =
        ConfigTuiModel.StepFields.SelectMany(f => f).Append("model").Max(k => k.Length) + 2;

    /// <summary>Rows of panel body below the step bar. Fixed so the frame never jumps between steps.</summary>
    private const int BodyRows = 6;

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle(" agent-one config ")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(
                        ViewModel.Revision
                            .Select<int, ILayoutNode>(_ => BuildBody())
                            .AsLayout())
                    .Height(BodyRows + 4))          // step bar + blank + body + borders
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => new TextNode(" " + ViewModel.Model.Hint())
                        .WithForeground(Color.BrightBlack))
                    .AsLayout()
                    .Height(1))
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => BuildStatus())
                    .AsLayout()
                    .Height(1))
            .WithChild(
                ViewModel.Revision
                    .Select<int, ILayoutNode>(_ => new TextNode(" " + KeyBar())
                        .WithForeground(Color.BrightBlack))
                    .AsLayout()
                    .Height(1));
    }

    private ILayoutNode BuildBody()
    {
        var rows = Layouts.Vertical()
            .WithChild(BuildStepBar())
            .WithChild(new TextNode("").Height(1));

        var body = ViewModel.Model.Step == ConfigStep.Model || ViewModel.Model.Picking ? BuildModelStep() : BuildFieldRows();

        foreach (var row in body) rows = rows.WithChild(row);

        // Pad to a fixed height so a short step cannot leave the previous one's
        // rows on screen — the renderer only repaints what it is given.
        for (int i = body.Count; i < BodyRows; i++)
            rows = rows.WithChild(new TextNode("").Height(1));

        return rows;
    }

    /// <summary>① Connection → ② Model → ③ Options, with the current step lit.</summary>
    private ILayoutNode BuildStepBar()
    {
        var model = ViewModel.Model;
        var bar = Layouts.Horizontal();

        // One width for every step, so the arrows between them line up whichever
        // step is bracketed.
        var width = ConfigTuiModel.StepTitles.Max(t => t.Length) + 5;

        for (int i = 0; i < ConfigTuiModel.StepTitles.Length; i++)
        {
            var current = i == (int)model.Step;
            var name = $"{i + 1}. {ConfigTuiModel.StepTitles[i]}";

            bar = bar.WithChild(
                new TextNode(current ? $"[{name}]" : $" {name} ")
                    .WithForeground(current ? Color.BrightCyan : Color.BrightBlack)
                    .Width(width));

            if (i < ConfigTuiModel.StepTitles.Length - 1)
                bar = bar.WithChild(new TextNode("→ ").WithForeground(Color.BrightBlack).Width(2));
        }

        return bar;
    }

    private List<LayoutNode> BuildFieldRows()
    {
        var model = ViewModel.Model;
        var rows = new List<LayoutNode>();

        for (int i = 0; i < model.Fields.Count; i++)
        {
            var key = model.Fields[i];
            var selected = i == model.Selected;
            var editing = selected && model.Editing;

            // A key being typed is echoed as dots: shoulder-surfing a settings
            // screen should not be enough to take it.
            var value = editing
                ? (ConfigTuiModel.CredentialFields.ContainsKey(key) ? new string('•', model.EditBuffer.Length) : model.EditBuffer) + "▌"
                : model.Value(key);

            // Empty means "inherit" on the Reasoning step — say so instead of showing a blank.
            if (!editing && value.Length == 0)
                value = key switch
                {
                    "reasoningBaseUrl" => "(same as the connection)",
                    ConfigTuiModel.ReasoningModelField => "(none — Enter to pick, e to type)",
                    _ => value
                };

            var marker = selected ? "›" : " ";
            var tail = model.IsCyclable(key) && selected && !editing ? "  ←→" : "";

            if (key == ConfigTuiModel.JevApiKeyField && !editing && !model.SmartKeyPresent)
                tail = "  (not set)";

            if (key == "apiKeyEnv" && !editing)
                tail = model.ApiKeyPresent ? "  (set)" : "  (not set)";

            var colour = editing ? Color.BrightYellow
                : selected ? Color.BrightCyan
                : Color.White;

            rows.Add(new TextNode($"{marker} {key.PadRight(KeyColumn)}{value}{tail}").WithForeground(colour).Height(1));
        }

        return rows;
    }

    private List<LayoutNode> BuildModelStep()
    {
        var model = ViewModel.Model;
        var rows = new List<LayoutNode>();

        if (model.Editing)
        {
            rows.Add(new TextNode($"› {model.PickKey.PadRight(KeyColumn)}{model.EditBuffer}▌")
                .WithForeground(Color.BrightYellow).Height(1));
            return rows;
        }

        if (model.Busy)
        {
            rows.Add(new TextNode("  asking the endpoint…").WithForeground(Color.BrightBlack).Height(1));
            return rows;
        }

        if (!model.Picking)
        {
            rows.Add(new TextNode($"  {model.PickKey.PadRight(KeyColumn)}{model.Value(model.PickKey)}").WithForeground(Color.White).Height(1));
            rows.Add(new TextNode("").Height(1));
            rows.Add(new TextNode("  no list from the endpoint — see the line below")
                .WithForeground(Color.BrightRed).Height(1));
            return rows;
        }

        var (first, count) = model.PickWindow(BodyRows - 1);

        for (int i = first; i < first + count; i++)
        {
            var option = model.PickOptions[i];
            var selected = i == model.PickIndex;
            var inUse = option == model.Value(model.PickKey);

            var colour = selected ? Color.BrightCyan
                : option == ConfigTuiModel.PickManualEntry ? Color.BrightBlack
                : Color.White;

            rows.Add(new TextNode($"{(selected ? "›" : " ")} {option}{(inUse ? "  (current)" : "")}")
                .WithForeground(colour).Height(1));
        }

        if (model.PickOptions.Count > count)
            rows.Add(new TextNode($"  {model.PickIndex + 1}/{model.PickOptions.Count}")
                .WithForeground(Color.BrightBlack).Height(1));

        return rows;
    }

    private ILayoutNode BuildStatus()
    {
        var status = ViewModel.Model.Status;
        var colour = status.StartsWith('✗') ? Color.BrightRed
            : status.StartsWith('✓') ? Color.BrightGreen
            : Color.BrightBlack;

        var dirty = ViewModel.Model.Dirty ? "  ●unsaved" : "";
        return new TextNode(" " + status + dirty).WithForeground(colour);
    }

    private string KeyBar()
    {
        var model = ViewModel.Model;

        if (model.Editing)
            return "Enter accept · Esc cancel · Backspace delete";

        // Kept under 80 columns: a key bar that wraps or truncates teaches the
        // wrong keys. The rest of the bindings live in the hint line above.
        var sb = new StringBuilder();

        if (model.Step == ConfigStep.Model || model.Picking)
        {
            if (model.Picking) sb.Append("↑↓ pick · Enter take · ");
            if (model.Picking && model.Step == ConfigStep.Reasoning) sb.Append("Esc close · ");
            sb.Append("e type · ");
        }
        else if (model.SelectedKey == ConfigTuiModel.ReasoningModelField)
        {
            sb.Append("↑↓ move · Enter list · e type · ");
        }
        else
        {
            sb.Append("↑↓ move · Enter edit · ");
            if (model.IsCyclable(model.SelectedKey)) sb.Append("←→ cycle · ");
        }

        if (model.Step == ConfigStep.Smart) sb.Append("h check · ");
        if (model.Step != ConfigStep.Connection) sb.Append("b back · ");
        if (model.Step != ConfigStep.Smart) sb.Append("Tab next · ");

        sb.Append("s save · t test · q quit");
        return sb.ToString();
    }
}
