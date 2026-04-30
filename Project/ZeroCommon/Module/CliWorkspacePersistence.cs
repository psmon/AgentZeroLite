using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agent.Common.Module;

public sealed record WindowStateSnapshot(
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsMaximized,
    int LastActiveGroupIndex,
    int LastActiveTabIndex,
    bool IsBotDocked);

public sealed record CliDefinitionSnapshot(
    int Id,
    string Name,
    string ExePath,
    string? Arguments);

public sealed record CliTabSnapshot(
    string Title,
    int CliDefinitionId,
    string ExePath,
    string? Arguments);

public sealed record CliGroupSnapshot(
    string DirectoryPath,
    string DisplayName,
    IReadOnlyList<CliTabSnapshot> Tabs);

public static class CliWorkspacePersistence
{
    public static WindowStateSnapshot? LoadWindowState()
    {
        using var db = new AppDbContext();
        var state = db.AppWindowStates.Find(1);
        return state is null
            ? null
            : new WindowStateSnapshot(
                state.Left,
                state.Top,
                state.Width,
                state.Height,
                state.IsMaximized,
                state.LastActiveGroupIndex,
                state.LastActiveTabIndex,
                state.IsBotDocked);
    }

    public static void SaveWindowState(
        double left,
        double top,
        double width,
        double height,
        bool isMaximized,
        int lastActiveGroupIndex,
        int lastActiveTabIndex,
        bool isBotDocked)
    {
        using var db = new AppDbContext();
        var state = db.AppWindowStates.Find(1) ?? new AppWindowState();
        state.Left = left;
        state.Top = top;
        state.Width = width;
        state.Height = height;
        state.IsMaximized = isMaximized;
        state.LastActiveGroupIndex = lastActiveGroupIndex;
        state.LastActiveTabIndex = lastActiveTabIndex;
        state.IsBotDocked = isBotDocked;

        if (db.AppWindowStates.Any(row => row.Id == 1))
            db.AppWindowStates.Update(state);
        else
            db.AppWindowStates.Add(state);

        db.SaveChanges();
    }

    public static void SaveCliGroups(IReadOnlyList<ICliGroupInfo> groups)
    {
        using var db = new AppDbContext();
        db.CliTabs.RemoveRange(db.CliTabs);
        db.CliGroups.RemoveRange(db.CliGroups);
        db.SaveChanges();

        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var dbGroup = new CliGroup
            {
                DirectoryPath = group.DirectoryPath,
                DisplayName = group.DisplayName,
                SortOrder = groupIndex,
            };

            db.CliGroups.Add(dbGroup);
            db.SaveChanges();

            for (int tabIndex = 0; tabIndex < group.TabsView.Count; tabIndex++)
            {
                var tab = group.TabsView[tabIndex];
                db.CliTabs.Add(new CliTab
                {
                    GroupId = dbGroup.Id,
                    Title = tab.Title,
                    CliDefinitionId = tab.CliDefinitionId > 0 ? tab.CliDefinitionId : 1,
                    SortOrder = tabIndex,
                });
            }
        }

        db.SaveChanges();
    }

    public static IReadOnlyList<CliGroupSnapshot> LoadCliGroups()
    {
        using var db = new AppDbContext();
        return db.CliGroups
            .AsNoTracking()
            .Include(group => group.Tabs)
            .ThenInclude(tab => tab.CliDefinition)
            .OrderBy(group => group.SortOrder)
            .Select(group => new CliGroupSnapshot(
                group.DirectoryPath,
                group.DisplayName,
                group.Tabs
                    .OrderBy(tab => tab.SortOrder)
                    .Select(tab => new CliTabSnapshot(
                        tab.Title,
                        tab.CliDefinitionId,
                        tab.CliDefinition.ExePath,
                        tab.CliDefinition.Arguments))
                    .ToList()))
            .ToList();
    }

    public static IReadOnlyList<CliDefinitionSnapshot> LoadCliDefinitions()
    {
        using var db = new AppDbContext();
        return db.CliDefinitions
            .AsNoTracking()
            .OrderBy(definition => definition.SortOrder)
            .Select(definition => new CliDefinitionSnapshot(
                definition.Id,
                definition.Name,
                definition.ExePath,
                definition.Arguments))
            .ToList();
    }
}
