using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using Agent.Common;
using Agent.Common.Data;
using Agent.Common.Telemetry;

namespace AgentZeroWpf.UI.Components;

/// <summary>
/// Budget settings tab (orca-adoption cost layer / mission W9 follow-up). Edits a
/// monthly USD cap + per-model price overrides (persisted by
/// <see cref="BudgetSettingsStore"/>) and shows a month-to-date spend readout that
/// turns amber near the cap and red once over it.
/// </summary>
public partial class SettingsPanel
{
    /// <summary>Editable price row bound to the DataGrid.</summary>
    public sealed class PriceRowVm
    {
        public string Key { get; set; } = "";
        public decimal InputPerMTok { get; set; }
        public decimal OutputPerMTok { get; set; }
        public decimal CacheWritePerMTok { get; set; }
        public decimal CacheReadPerMTok { get; set; }
    }

    private readonly ObservableCollection<PriceRowVm> _priceRows = new();

    private void InitializeBudgetTab()
    {
        try
        {
            dgPrices.ItemsSource = _priceRows;
            LoadBudgetSettingsToUi();
            RefreshBudgetStatus();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Budget] init failed: {ex.Message}");
        }
    }

    private void LoadBudgetSettingsToUi()
    {
        var s = BudgetSettingsStore.Load();
        tbBudgetCap.Text = s.MonthlyCapUsd.ToString(CultureInfo.InvariantCulture);
        _priceRows.Clear();
        foreach (var o in s.Overrides)
            _priceRows.Add(new PriceRowVm
            {
                Key = o.Key,
                InputPerMTok = o.InputPerMTok,
                OutputPerMTok = o.OutputPerMTok,
                CacheWritePerMTok = o.CacheWritePerMTok,
                CacheReadPerMTok = o.CacheReadPerMTok,
            });
    }

    private BudgetSettings ReadBudgetSettingsFromUi()
    {
        var s = new BudgetSettings();
        if (decimal.TryParse(tbBudgetCap.Text?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var cap))
            s.MonthlyCapUsd = cap;
        foreach (var r in _priceRows)
        {
            if (string.IsNullOrWhiteSpace(r.Key)) continue;
            s.Overrides.Add(new BudgetSettings.PriceOverride
            {
                Key = r.Key.Trim(),
                InputPerMTok = r.InputPerMTok,
                OutputPerMTok = r.OutputPerMTok,
                CacheWritePerMTok = r.CacheWritePerMTok,
                CacheReadPerMTok = r.CacheReadPerMTok,
            });
        }
        return s;
    }

    private void OnBudgetSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ReadBudgetSettingsFromUi();
            BudgetSettingsStore.Save(s);
            RefreshBudgetStatus(s);
        }
        catch (Exception ex)
        {
            lblBudgetStatus.Text = $"Save failed: {ex.Message}";
            lblBudgetStatus.Foreground = BudgetBrush("#FF4D4D");
        }
    }

    private void OnBudgetRefresh(object sender, RoutedEventArgs e) => RefreshBudgetStatus();

    private void RefreshBudgetStatus(BudgetSettings? settings = null)
    {
        try
        {
            var s = settings ?? ReadBudgetSettingsFromUi();
            decimal spent = 0m;
            try
            {
                using var db = new AppDbContext();
                var since = BudgetSettings.StartOfMonthUtc(DateTime.UtcNow);
                var records = db.TokenUsageRecords.Where(r => r.RecordedAt >= since).ToList();
                spent = TokenCostCalculator.TotalUsd(records, s.EffectiveTable());
            }
            catch
            {
                // DB unavailable (first run / migration pending) → show the cap only.
            }

            var status = s.Evaluate(spent);
            if (!status.HasCap)
            {
                lblBudgetStatus.Text = $"Spent this month: ${spent:F2}  (no cap set)";
                lblBudgetStatus.Foreground = BudgetBrush("#00FFA3");
            }
            else
            {
                var pct = status.FractionUsed * 100.0;
                var tail = status.OverBudget ? "  ⚠ OVER BUDGET"
                    : status.NearingCap() ? "  ⚠ nearing cap"
                    : "";
                lblBudgetStatus.Text = $"Spent this month: ${spent:F2} / ${status.CapUsd:F2}  ({pct:F0}%){tail}";
                lblBudgetStatus.Foreground = BudgetBrush(
                    status.OverBudget ? "#FF4D4D" : status.NearingCap() ? "#FFC24D" : "#00FFA3");
            }
        }
        catch (Exception ex)
        {
            lblBudgetStatus.Text = $"Status error: {ex.Message}";
        }
    }

    private static System.Windows.Media.Brush BudgetBrush(string hex)
        => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
}
