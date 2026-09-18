using System.IO;
using System.Linq;
using Agent.Common.Data;
using Microsoft.EntityFrameworkCore;

namespace ZeroCommon.Tests;

/// <summary>
/// M0033 — the CLI definition table serves both hosts on both OSes. The migration seeds
/// Windows shells; on a non-Windows host the runtime seed adds zsh/bash/Claude once, and
/// on Windows nothing changes.
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
    public void Windows_seed_adds_only_the_claude_profile()
    {
        using var db = Open();
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);
        var names = db.CliDefinitions.OrderBy(d => d.SortOrder).Select(d => d.Name).ToList();
        Assert.Equal(new[] { "CMD", "PW5", "PW7", "Claude" }, names);
        Assert.Equal("powershell.exe", db.CliDefinitions.Single(d => d.Name == "Claude").ExePath);

        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: true);
        Assert.Equal(4, db.CliDefinitions.Count());
    }

    [Fact]
    public void Posix_seed_adds_zsh_bash_claude_once_and_keeps_windows_rows()
    {
        using var db = Open();
        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: false);
        var posix = db.CliDefinitions.AsEnumerable().Where(d => !d.ExePath.EndsWith(".exe")).OrderBy(d => d.SortOrder).ToList();
        Assert.Equal(new[] { "zsh", "bash", "Claude" }, posix.Select(d => d.Name));
        Assert.All(posix, d => Assert.True(d.IsBuiltIn));
        Assert.Equal("/bin/zsh", posix[2].ExePath);
        Assert.Contains("claude", posix[2].Arguments);
        Assert.Equal(6, db.CliDefinitions.Count());   // 3 migrated + 3 seeded

        AppDbContext.EnsureDefaultCliDefinitions(db, isWindows: false);
        Assert.Equal(6, db.CliDefinitions.Count());
    }
}
