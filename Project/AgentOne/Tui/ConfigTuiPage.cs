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
    private const int KeyColumn = 16;

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
                            .Select<int, ILayoutNode>(_ => BuildRows())
                            .AsLayout())
                    .Height(AgentOne.Services.AgentConfig.Keys.Length + 2))
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

    private ILayoutNode BuildRows()
    {
        var model = ViewModel.Model;
        if (model.Picking) return BuildPicker();

        var rows = Layouts.Vertical();

        for (int i = 0; i < model.Keys.Count; i++)
        {
            var key = model.Keys[i];
            var selected = i == model.Selected;
            var editing = selected && model.Editing;

            var value = editing ? model.EditBuffer + "▌" : model.Value(key);
            var marker = selected ? "›" : " ";
            var cycle = model.IsCyclable(key) && selected && !editing ? "  ←→" : "";

            var line = $"{marker} {key.PadRight(KeyColumn)}{value}{cycle}";

            var colour = editing ? Color.BrightYellow
                : selected ? Color.BrightCyan
                : Color.White;

            rows = rows.WithChild(new TextNode(line).WithForeground(colour).Height(1));
        }

        return rows;
    }

    /// <summary>
    /// The model list, windowed to the panel's height so a provider offering
    /// eighty models does not push the screen apart.
    /// </summary>
    private ILayoutNode BuildPicker()
    {
        var model = ViewModel.Model;
        var rows = Layouts.Vertical();
        var height = AgentOne.Services.AgentConfig.Keys.Length;
        var (first, count) = model.PickWindow(height);

        for (int i = first; i < first + count; i++)
        {
            var option = model.PickOptions[i];
            var selected = i == model.PickIndex;
            var inUse = option == model.Value("model");

            var marker = selected ? "›" : " ";
            var tail = inUse ? "  (current)" : "";
            var line = $"{marker} {option}{tail}";

            var colour = selected ? Color.BrightCyan
                : option == ConfigTuiModel.PickManualEntry ? Color.BrightBlack
                : Color.White;

            rows = rows.WithChild(new TextNode(line).WithForeground(colour).Height(1));
        }

        if (model.PickOptions.Count > height)
        {
            rows = rows.WithChild(
                new TextNode($"  {model.PickIndex + 1}/{model.PickOptions.Count}")
                    .WithForeground(Color.BrightBlack).Height(1));
        }

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

        if (model.Picking)
            return "↑↓ choose · Enter take it · Esc keep the current one";

        var sb = new StringBuilder("↑↓ move · Enter ");
        sb.Append(model.SelectedKey == "model" ? "list models" : "edit");
        if (model.IsCyclable(model.SelectedKey)) sb.Append(" · ←→ cycle");
        sb.Append(" · s save · r reload · d defaults · l models · t test · q quit");
        return sb.ToString();
    }
}
