using System;
using System.Linq;
using System.Windows.Threading;
using Agent.Common;
using Agent.Common.Automations;
using Agent.Common.Data;

namespace AgentZeroWpf.Services;

/// <summary>
/// GUI-side scheduler for Automations. On a periodic tick it finds enabled
/// automations whose <c>NextRunUtc</c> has passed, dispatches each prompt to the
/// bot, then advances their next-run time. The schedule math is the pure
/// <see cref="AutomationSchedule"/>; this class only handles timing + dispatch.
/// </summary>
public sealed class AutomationScheduler
{
    private readonly DispatcherTimer _timer;
    private readonly Action<string> _dispatchPrompt;
    private bool _busy;

    public AutomationScheduler(Action<string> dispatchPrompt, TimeSpan? interval = null)
    {
        _dispatchPrompt = dispatchPrompt;
        _timer = new DispatcherTimer { Interval = interval ?? TimeSpan.FromSeconds(60) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var nowUtc = DateTime.UtcNow;
            using var db = new AppDbContext();
            var due = db.Automations
                .Where(a => a.Enabled && a.NextRunUtc != null && a.NextRunUtc <= nowUtc)
                .OrderBy(a => a.NextRunUtc)
                .ToList();

            foreach (var a in due)
            {
                try
                {
                    _dispatchPrompt(a.Prompt);
                    AppLogger.Log($"[Automation] fired #{a.Id} '{a.Name}'");
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Automation] dispatch #{a.Id} failed: {ex.Message}");
                }

                a.LastRunUtc = nowUtc;
                a.NextRunUtc = AutomationSchedule.TryComputeNext(a.Schedule, nowUtc, out var next, out _)
                    ? next
                    : null; // unparseable schedule → stop firing (leave enabled for visibility)
            }
            if (due.Count > 0) db.SaveChanges();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Automation] scheduler tick failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }
}
