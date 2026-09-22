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
        if (ViewModel.Model.Editing)
            return "Enter accept · Esc cancel · Backspace delete";

        var sb = new StringBuilder("↑↓ move · Enter edit");
        if (ViewModel.Model.IsCyclable(ViewModel.Model.SelectedKey)) sb.Append(" · ←→ cycle");
        sb.Append(" · s save · r reload · d defaults · t test · q quit");
        return sb.ToString();
    }
}
