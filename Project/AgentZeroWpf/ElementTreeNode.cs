using System.Collections.ObjectModel;

namespace AgentZeroWpf;

/// <summary>UI Automation 요소 트리의 노드 (TreeView 바인딩용)</summary>
internal sealed class ElementTreeNode
{
    public string TypeTag { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string BoundsText { get; init; } = "";
    public string Label => $"{TypeTag}{DisplayName}{BoundsText}";
    public ObservableCollection<ElementTreeNode> Children { get; } = [];
}
