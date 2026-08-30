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
    public DbSet<TokenRemainingObservation> TokenRemainingObservations => Set<TokenRemainingObservation>();
    public DbSet<SessionHeartbeat> SessionHeartbeats => Set<SessionHeartbeat>();
    public DbSet<Mp3Track> Mp3Tracks => Set<Mp3Track>();
    public DbSet<Mp3MoodCard> Mp3MoodCards => Set<Mp3MoodCard>();
    public DbSet<DiffComment> DiffComments => Set<DiffComment>();
    public DbSet<OrchestrationRun> OrchestrationRuns => Set<OrchestrationRun>();
    public DbSet<OrchestrationTask> OrchestrationTasks => Set<OrchestrationTask>();
    public DbSet<OrchestrationDispatch> OrchestrationDispatches => Set<OrchestrationDispatch>();
    public DbSet<Automation> Automations => Set<Automation>();
    public DbSet<YouTubePlaylistItem> YouTubePlaylistItems => Set<YouTubePlaylistItem>();

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

        // Latest-per-(account, model) lookup is the hot path for the
        // token-remaining widget. The DESC ordering on ObservedAtUtc
        // lets SQLite serve the query with a single index range scan.
        mb.Entity<TokenRemainingObservation>()
            .HasIndex(o => new { o.AccountKey, o.Model, o.ObservedAtUtc })
            .IsDescending(false, false, true);

        // SessionHeartbeat — UPSERT key + active-window scan.
        mb.Entity<SessionHeartbeat>()
            .HasIndex(h => new { h.AccountKey, h.SessionId })
            .IsUnique();
        mb.Entity<SessionHeartbeat>()
            .HasIndex(h => h.LastSeenUtc)
            .IsDescending(true);

        // Mp3Track (M0029) — FilePath is the rescan upsert key.
        mb.Entity<Mp3Track>()
            .HasIndex(t => t.FilePath)
            .IsUnique();

        // YouTubePlaylistItem (B4 / music-curator #29) — VideoId is the upsert key.
        mb.Entity<YouTubePlaylistItem>()
            .HasIndex(t => t.VideoId)
            .IsUnique();

        // DiffComment (W3) — comments fetched per review session, newest first.
        mb.Entity<DiffComment>()
            .HasIndex(c => new { c.SessionId, c.CreatedAtUtc });

        // Orchestration (W6) — Run 1—* Task; cascade delete tasks with the run.
        mb.Entity<OrchestrationTask>()
            .HasOne(t => t.Run)
            .WithMany(r => r.Tasks)
            .HasForeignKey(t => t.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        mb.Entity<OrchestrationTask>()
            .HasIndex(t => new { t.RunId, t.TaskKey })
            .IsUnique();
        mb.Entity<OrchestrationDispatch>()
            .HasIndex(d => new { d.RunId, d.TaskId });

        // Automation (scheduled runs) — enabled+next-run scan is the hot path.
        mb.Entity<Automation>()
            .HasIndex(a => new { a.Enabled, a.NextRunUtc });
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
