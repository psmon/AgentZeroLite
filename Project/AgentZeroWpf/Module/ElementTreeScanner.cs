using System.Text;
using System.Windows.Automation;

namespace AgentZeroWpf.Module;

internal sealed record ElementTreeScanResult(ElementTreeNode RootNode, int NodeCount, string TreeText);

internal static class ElementTreeScanner
{
    public static ElementTreeScanResult? Scan(IntPtr hwnd, int maxDepth = 30, int maxLogLines = 50)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null)
                return null;

            // Force accessibility tree activation before walking it.
            root.FindAll(TreeScope.Children, Condition.TrueCondition);

            var count = 0;
            var rootNode = BuildTreeNode(root, TreeWalker.ControlViewWalker, 0, ref count, maxDepth);

            DumpTreeToLog(rootNode, "", 0, maxLogLines);

            var sb = new StringBuilder();
            BuildTreeText(rootNode, "", sb);
            return new ElementTreeScanResult(rootNode, count, sb.ToString());
        }
        catch (Exception ex)
        {
            AppLogger.LogError("[Main] ScanElementTree 예외", ex);
            return null;
        }
    }

    private static void BuildTreeText(ElementTreeNode node, string indent, StringBuilder sb)
    {
        sb.AppendLine($"{indent}{node.Label}");
        foreach (var child in node.Children)
            BuildTreeText(child, indent + "  ", sb);
    }

    private static void DumpTreeToLog(ElementTreeNode node, string indent, int logged, int max)
    {
        if (logged >= max)
            return;

        AppLogger.Log($"[Tree] {indent}{node.Label}  (children={node.Children.Count})");
        logged++;

        foreach (var child in node.Children)
        {
            if (logged >= max)
            {
                AppLogger.Log($"[Tree] {indent}  ... (truncated)");
                break;
            }

            DumpTreeToLog(child, indent + "  ", logged, max);
            logged += CountNodes(child);
        }
    }

    private static int CountNodes(ElementTreeNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
            count += CountNodes(child);

        return count;
    }

    private static ElementTreeNode BuildTreeNode(AutomationElement element, TreeWalker walker, int depth, ref int count, int maxDepth)
    {
        count++;

        var controlType = "?";
        var name = "";
        var boundsText = "";

        try
        {
            controlType = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            name = element.Current.Name ?? "";
            if (name.Length > 60)
                name = name[..57] + "...";

            var bounds = element.Current.BoundingRectangle;
            if (!bounds.IsEmpty)
                boundsText = $" @({(int)bounds.Left},{(int)bounds.Top}) [{(int)bounds.Width}x{(int)bounds.Height}]";
        }
        catch
        {
            // Some automation nodes throw during property access. Keep scanning.
        }

        var node = new ElementTreeNode
        {
            TypeTag = $"[{controlType}] ",
            DisplayName = string.IsNullOrEmpty(name) ? "" : $"\"{name}\" ",
            BoundsText = boundsText,
        };

        if (depth >= maxDepth)
            return node;

        try
        {
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                node.Children.Add(BuildTreeNode(child, walker, depth + 1, ref count, maxDepth));
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Some UI trees deny child traversal. Return the partial tree.
        }

        return node;
    }
}
