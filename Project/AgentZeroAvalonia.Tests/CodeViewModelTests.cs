using Agent.Common.Workspace;
using AgentZeroAvalonia.Layout;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>The code view's model: the workspace tree, opening files, staying inside the workspace.</summary>
public sealed class CodeViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "az-code-" + Guid.NewGuid().ToString("N"));

    public CodeViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App {}");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Two_loads_of_one_root_fill_the_tree_once()
    {
        // Measured: opening the view from the CLI started two loads of the same root and the
        // tree showed every top-level entry twice.
        var vm = new CodeViewModel();
        await Task.WhenAll(vm.SetRootAsync(_root, "ws"), vm.SetRootAsync(_root, "ws"));
        Assert.Equal(new[] { "src", "README.md" }, vm.Nodes.Select(n => n.Name));   // node_modules hidden
    }

    [Fact]
    public async Task A_folder_loads_its_children_when_expanded()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        var src = vm.Nodes.Single(n => n.Name == "src");
        Assert.Equal("…", Assert.Single(src.Children).Name);   // placeholder until expanded

        await src.LoadAsync();
        Assert.Equal("app.cs", Assert.Single(src.Children).Name);
    }

    [Fact]
    public async Task Opening_reads_inside_the_workspace_only()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");

        Assert.Null(await vm.OpenAsync("src/app.cs"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);
        Assert.Equal("class App {}", vm.Text);
        Assert.Equal(FileViewKind.Text, vm.Kind);
        Assert.True(vm.IsTextView);

        Assert.Equal("the file is outside the workspace", await vm.OpenAsync("../elsewhere.txt"));
        Assert.Equal("no such file", await vm.OpenAsync("missing.txt"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);   // a refused open leaves the shown file alone
    }

    [Fact]
    public async Task Markdown_opens_rendered_and_switches_to_its_source()
    {
        File.WriteAllText(Path.Combine(_root, "docs.md"), "# Title\n\n```mermaid\ngraph LR\n A-->B\n```\n\n[app](src/app.cs)\n");
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        await vm.OpenAsync("docs.md");

        Assert.True(vm.IsMarkdown);
        Assert.True(vm.IsPreview);          // rendered by default
        Assert.False(vm.IsTextView);
        Assert.Contains("<pre class=\"mermaid\">", vm.Html);

        vm.TogglePreviewCommand.Execute(null);
        Assert.False(vm.IsPreview);
        Assert.True(vm.IsTextView);         // the source, highlighted

        // A relative link opens the workspace file here; a web link is handed back to launch.
        Assert.Null(await vm.FollowLinkAsync("src/app.cs"));
        Assert.Equal("src/app.cs", vm.CurrentTitle);
        Assert.Equal(new Uri("https://example.com/x"), await vm.FollowLinkAsync("https://example.com/x"));
        Assert.False(vm.IsMarkdown);        // a .cs file is never previewed
    }

    [Fact]
    public async Task Expanding_again_picks_up_new_files_and_keeps_the_nodes_below()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        var src = vm.Nodes.Single(n => n.Name == "src");
        Directory.CreateDirectory(Path.Combine(_root, "src", "lib"));
        await src.LoadAsync();
        var lib = src.Children.Single(n => n.Name == "lib");
        File.WriteAllText(Path.Combine(_root, "src", "lib", "a.cs"), "");
        await lib.LoadAsync();
        lib.IsExpanded = true;

        // Created after the folder was read: no ⟳, just the next expand (or a select) of src.
        File.WriteAllText(Path.Combine(_root, "src", "new.cs"), "");
        File.Delete(Path.Combine(_root, "src", "app.cs"));
        var result = await src.LoadAsync();

        Assert.Equal((1, 1), (result.Added, result.Removed));
        Assert.Equal(new[] { "lib", "new.cs" }, src.Children.Select(n => n.Name));
        Assert.Same(lib, src.Children[0]);                       // the same node, not a rebuilt one
        Assert.True(lib.IsExpanded);
        Assert.Equal("a.cs", Assert.Single(lib.Children).Name);  // its children are still there
    }

    [Fact]
    public async Task Refresh_merges_in_place_and_keeps_expansion_and_the_open_file()
    {
        var vm = new CodeViewModel();
        await vm.SetRootAsync(_root, "ws");
        var src = vm.Nodes.Single(n => n.Name == "src");
        await src.LoadAsync();
        src.IsExpanded = true;
        await vm.OpenAsync("src/app.cs");
        var changes = 0;
        vm.TextChanged += () => changes++;

        File.WriteAllText(Path.Combine(_root, "src", "more.cs"), "");
        File.WriteAllText(Path.Combine(_root, "NOTES.txt"), "");
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Same(src, vm.Nodes.Single(n => n.Name == "src"));
        Assert.True(src.IsExpanded);
        Assert.Equal(new[] { "app.cs", "more.cs" }, src.Children.Select(n => n.Name));   // expanded folder re-read
        Assert.Contains(vm.Nodes, n => n.Name == "NOTES.txt");
        Assert.Equal("src/app.cs", vm.CurrentTitle);
        Assert.Equal(0, changes);                                  // unchanged file: not re-shown

        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App { int x; }");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, changes);
        Assert.Equal("class App { int x; }", vm.Text);
    }

    [Fact]
    public void The_toggle_chord_is_bound()
    {
        var binding = Assert.Single(HotkeyTable.Build(null, isMac: false), b => b.Name == HotkeyTable.CodeToggle);
        Assert.True(binding.Ctrl && binding.Alt && binding.Key == "E");
    }
}
