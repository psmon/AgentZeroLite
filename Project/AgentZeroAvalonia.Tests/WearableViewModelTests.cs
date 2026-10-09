using Agent.Common.Wearable;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

/// <summary>wearable-settings.json is the whole contract with the host process; the page may only change what it shows.</summary>
public class WearableViewModelTests
{
    [Fact]
    public void Saving_keeps_hidden_fields_and_drops_folders_without_a_path()
    {
        var vm = new WearableViewModel("Debug");
        vm.Roots.Clear();
        vm.Roots.Add(new AllowedRootRow { Alias = "music", Path = @"C:\Music", Writable = false });
        vm.Roots.Add(new AllowedRootRow { Alias = "empty", Path = "  " });
        vm.HudPort = 9000;

        var stored = new WearableSettings { GuiExePath = "off", MaxReplyChars = 777, WorkspaceRoot = @"C:\old" };
        var saved = vm.ApplyTo(stored);

        Assert.Equal("off", saved.GuiExePath);
        Assert.Equal(777, saved.MaxReplyChars);
        Assert.Equal(9000, saved.HudPort);
        var root = Assert.Single(saved.AllowedRoots);
        Assert.Equal("music", root.Alias);
        Assert.Equal("", saved.WorkspaceRoot);
    }

    [Fact]
    public void Out_of_range_ports_leave_the_stored_value()
    {
        var vm = new WearableViewModel("Debug") { RemotingPort = 0, HudPort = 70000 };
        var saved = vm.ApplyTo(new WearableSettings { RemotingPort = 2552, HudPort = 8765 });
        Assert.Equal(2552, saved.RemotingPort);
        Assert.Equal(8765, saved.HudPort);
    }

    [Fact]
    public void A_new_folder_alias_is_its_name_made_unique()
    {
        Assert.Equal("music-2", WearableDiagnostics.UniqueAlias(@"C:\Users\me\Music\", ["music"]));
        Assert.Equal("music-3", WearableDiagnostics.UniqueAlias(@"D:\music", ["music", "music-2"]));
    }
}
