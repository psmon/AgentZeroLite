using System.IO;
using Agent.Common.Data.Entities;
using Agent.Common.Services;
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

    public AppDbContext() { }

    /// <summary>For tests and hosts that point the context at another file (M0033).</summary>
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        if (options.IsConfigured) return;
        options
            .UseSqlite($"Data Source={_dbPath}")
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

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

    /// <summary>
    /// Runtime seeding, on top of the migration's <c>HasData</c> shells. Two layers, for
    /// two different reasons.
    ///
    /// <para><b>Shells.</b> Windows gets CMD/PW5/PW7 from the migration. Other OSes
    /// (M0033) would see three <c>.exe</c> rows the launcher hides, so a zsh/bash pair is
    /// added once — no migration, because the same table serves both hosts and a Windows
    /// database must not grow POSIX rows.</para>
    ///
    /// <para><b>Agent CLI profiles</b> (Claude, Codex) are checked <em>one by one</em>
    /// rather than behind a single "did we seed yet?" flag. Every existing installation
    /// already has Claude, so a guard that returned once it saw Claude would never add
    /// Codex to any database in the field — the new built-in would only ever appear for
    /// people who installed the app fresh.</para>
    /// </summary>
    internal static void EnsureDefaultCliDefinitions(AppDbContext db, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var changed = false;
        var sort = db.CliDefinitions.Any() ? db.CliDefinitions.Max(d => d.SortOrder) : -1;

        if (!windows)
        {
            var hasPosixShell = db.CliDefinitions.AsEnumerable()
                .Any(d => !d.IsRemote && !string.IsNullOrEmpty(d.ExePath)
                          && !d.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (!hasPosixShell)
            {
                Add(new CliDefinition { Name = "zsh", ExePath = "/bin/zsh", Arguments = "-l" });
                Add(new CliDefinition { Name = "bash", ExePath = "/bin/bash", Arguments = "-l" });
            }
        }

        foreach (var profile in DefaultAgentCliProfiles(windows))
        {
            // Name alone is not the key: a row can exist for the other OS (an .exe
            // definition on a mac), which this host hides — so it would be invisible and
            // un-runnable while still suppressing the seed.
            var exists = db.CliDefinitions.AsEnumerable().Any(d =>
                string.Equals(d.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                && TerminalLaunchPlanner.IsAvailableOnThisOs(d, windows));
            if (!exists) Add(profile);
        }

        if (changed) db.SaveChanges();

        void Add(CliDefinition definition)
        {
            definition.IsBuiltIn = true;
            definition.SortOrder = ++sort;   // appended in catalog order, after whatever is already there
            db.CliDefinitions.Add(definition);
            changed = true;
        }
    }

    /// <summary>
    /// The built-in agent CLI rows for this OS. Both launch through a shell rather than
    /// invoking the agent directly: PowerShell 5 on Windows (<c>powershell.exe</c> — the
    /// one shell every Windows install has, so the definition works before anyone has
    /// installed PowerShell 7), zsh elsewhere. <c>-NoExit</c> / <c>exec zsh -l</c> keep
    /// the shell alive when the agent quits, so the tab stays usable instead of closing
    /// on exit. Whether the agent itself is installed is
    /// <see cref="AgentCliTools"/>' question — a row for a missing tool is still the
    /// right row, and the settings page offers to fetch it.
    /// </summary>
    private static IEnumerable<CliDefinition> DefaultAgentCliProfiles(bool windows)
    {
        foreach (var tool in AgentCliTools.All)
        {
            yield return windows
                ? new CliDefinition
                {
                    Name = tool.Name,
                    ExePath = "powershell.exe",
                    Arguments = $"-NoExit -Command {tool.Command}",
                }
                : new CliDefinition
                {
                    Name = tool.Name,
                    ExePath = "/bin/zsh",
                    Arguments = $"-l -c \"{tool.Command}; exec zsh -l\"",
                };
        }
    }
}
