using System.IO;
using System.Linq;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ZeroCommon.Tests;

/// <summary>
/// The CLI definition table serves both hosts on both OSes. The migration seeds Windows
/// shells (M0033); the runtime seed adds the POSIX shells once on a non-Windows host and
/// the agent CLI profiles (Claude, Codex) on either — each checked on its own, so a
/// database that predates a new built-in still gains it.
/// </summary>
[Trait("Category", "Persistence")]
public sealed class CliDefinitionSeedingTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "aztest-seed-" + Guid.NewGuid().ToString("n") + ".db");

    private AppDbContext Open()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var db = new AppDbContext(options);
        db.Database.Migrate();
        return db;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Windows_seed_adds_the_agent_profiles_on_powershell_5()
    {
        using var db = Open();
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);
        var names = db.CliDefinitions.OrderBy(d => d.SortOrder).Select(d => d.Name).ToList();
        Assert.Equal(new[] { "CMD", "PW5", "PW7", "Claude", "Codex" }, names);

        foreach (var name in new[] { "Claude", "Codex" })
        {
            var row = db.CliDefinitions.Single(d => d.Name == name);
            // PowerShell 5 is the base: every Windows install has powershell.exe, so the
            // definition works before anyone installs PowerShell 7.
            Assert.Equal("powershell.exe", row.ExePath);
            Assert.Equal("-NoExit -Command " + name.ToLowerInvariant(), row.Arguments);
            Assert.True(row.IsBuiltIn);
        }

        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);
        Assert.Equal(5, db.CliDefinitions.Count());
    }

    [Fact]
    public void An_existing_database_that_already_has_claude_still_gains_codex()
    {
        // The regression this guards: seeding used to stop at "is there a Claude row?",
        // so every installation in the field would have skipped a newly added built-in.
        using var db = Open();
        db.CliDefinitions.Add(new CliDefinition
        {
            Name = "Claude", ExePath = "powershell.exe", Arguments = "-NoExit -Command claude",
            IsBuiltIn = true, SortOrder = 3,
        });
        db.SaveChanges();

        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);

        Assert.Single(db.CliDefinitions.Where(d => d.Name == "Claude"));
        var codex = db.CliDefinitions.Single(d => d.Name == "Codex");
        Assert.Equal("powershell.exe", codex.ExePath);
        Assert.True(codex.SortOrder > 3);   // appended, never renumbering what is there
    }

    [Fact]
    public void Posix_seed_adds_the_shells_and_both_agents_once_and_keeps_windows_rows()
    {
        using var db = Open();
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: false);
        var posix = db.CliDefinitions.AsEnumerable().Where(d => !d.ExePath.EndsWith(".exe")).OrderBy(d => d.SortOrder).ToList();
        Assert.Equal(new[] { "zsh", "bash", "Claude", "Codex" }, posix.Select(d => d.Name));
        Assert.All(posix, d => Assert.True(d.IsBuiltIn));
        foreach (var name in new[] { "Claude", "Codex" })
        {
            var row = posix.Single(d => d.Name == name);
            Assert.Equal("/bin/zsh", row.ExePath);
            // `exec zsh -l` keeps the tab alive after the agent quits.
            Assert.Contains(name.ToLowerInvariant() + "; exec zsh -l", row.Arguments!);
        }
        Assert.Equal(7, db.CliDefinitions.Count());   // 3 migrated + 4 seeded

        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: false);
        Assert.Equal(7, db.CliDefinitions.Count());
    }

    [Fact]
    public void A_windows_agent_row_does_not_satisfy_the_posix_seed()
    {
        // An .exe definition is hidden on a mac, so a name match alone would leave the
        // host with an agent entry it cannot run and no runnable one.
        using var db = Open();
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: false);

        var claude = db.CliDefinitions.Where(d => d.Name == "Claude").ToList();
        Assert.Equal(2, claude.Count);
        Assert.Contains(claude, d => d.ExePath == "powershell.exe");
        Assert.Contains(claude, d => d.ExePath == "/bin/zsh");
    }
}
