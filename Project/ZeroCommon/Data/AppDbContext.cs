using System.IO;
using Agent.Common.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agent.Common.Data;

public class AppDbContext : DbContext
{
    public DbSet<AppWindowState> AppWindowStates => Set<AppWindowState>();
    public DbSet<CliDefinition> CliDefinitions => Set<CliDefinition>();
    public DbSet<CliGroup> CliGroups => Set<CliGroup>();
    public DbSet<CliTab> CliTabs => Set<CliTab>();
    public DbSet<ClipboardEntry> ClipboardEntries => Set<ClipboardEntry>();
    public DbSet<TokenUsageRecord> TokenUsageRecords => Set<TokenUsageRecord>();
    public DbSet<TokenSourceCheckpoint> TokenSourceCheckpoints => Set<TokenSourceCheckpoint>();
    public DbSet<TokenAccountAlias> TokenAccountAliases => Set<TokenAccountAlias>();

    private static readonly string _dbDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentZeroLite");

    private static readonly string _dbPath = Path.Combine(_dbDir, "agentZeroLite.db");

    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options
            .UseSqlite($"Data Source={_dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<AppWindowState>().HasData(new AppWindowState());

        mb.Entity<CliDefinition>().HasData(
            new CliDefinition { Id = 1, Name = "CMD", ExePath = "cmd.exe", IsBuiltIn = true, SortOrder = 0 },
            new CliDefinition { Id = 2, Name = "PW5", ExePath = "powershell.exe", IsBuiltIn = true, SortOrder = 1 },
            new CliDefinition { Id = 3, Name = "PW7", ExePath = "pwsh.exe", IsBuiltIn = true, SortOrder = 2 }
        );

        mb.Entity<CliTab>()
            .HasOne(t => t.Group)
            .WithMany(g => g.Tabs)
            .HasForeignKey(t => t.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        mb.Entity<CliTab>()
            .HasOne(t => t.CliDefinition)
            .WithMany()
            .HasForeignKey(t => t.CliDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        mb.Entity<TokenUsageRecord>()
            .HasIndex(r => new { r.Vendor, r.RecordedAt });
        mb.Entity<TokenUsageRecord>()
            .HasIndex(r => new { r.Vendor, r.RawRequestId });
        mb.Entity<TokenUsageRecord>()
            .HasIndex(r => new { r.SourceFile, r.SourceLine });

        mb.Entity<TokenSourceCheckpoint>()
            .HasIndex(c => c.SourceFile)
            .IsUnique();

        mb.Entity<TokenAccountAlias>()
            .HasIndex(a => new { a.Vendor, a.AccountKey })
            .IsUnique();
    }

    public static void InitializeDatabase()
    {
        Directory.CreateDirectory(_dbDir);
        using var db = new AppDbContext();
        db.Database.Migrate();
        EnsureDefaultCliDefinitions(db);
    }

    private static void EnsureDefaultCliDefinitions(AppDbContext db)
    {
        if (!db.CliDefinitions.Any(d => d.Name == "Claude"))
        {
            var maxSort = db.CliDefinitions.Any() ? db.CliDefinitions.Max(d => d.SortOrder) : -1;
            db.CliDefinitions.Add(new CliDefinition
            {
                Name = "Claude",
                ExePath = "powershell.exe",
                Arguments = "-NoExit -Command claude",
                IsBuiltIn = true,
                SortOrder = maxSort + 1,
            });
            db.SaveChanges();
        }
    }
}
