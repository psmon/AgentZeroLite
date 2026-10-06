using System.Reflection;
using System.Runtime.InteropServices;
using Agent.Common;
using Agent.Common.Llm;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// The "Report this answer" panel over the bot transcript: the person picks what is wrong
/// with one AI answer, adds a note, and sends it by e-mail or as a GitHub issue — both
/// open pre-filled for the person to review and send (<see cref="AiContentReport"/>).
/// Microsoft Store policy 11.16 asks for exactly this; certification of submission 3
/// failed without it.
/// </summary>
public partial class AiReportViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private int _categoryIndex;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string? _sentMessage;

    public IReadOnlyList<string> CategoryNames { get; } =
        AiContentReport.Categories.Select(AiContentReport.Describe).ToArray();

    /// <summary>Opens a URI with the OS (mail client or browser). Set by the host.</summary>
    public Action<string>? OpenUri { get; set; }

    /// <summary>The model that wrote the answer, as configured. Read when a report is built.</summary>
    public Func<string?> ModelName { get; set; } = () => null;

    /// <summary>The answer's first line, for the panel header.</summary>
    public string Preview => Content.Length <= 120 ? Content.ReplaceLineEndings(" ") : Content[..117].ReplaceLineEndings(" ") + "…";

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(Preview));

    public void Open(string content)
    {
        Content = content;
        CategoryIndex = 0;
        Note = "";
        SentMessage = null;
        IsOpen = true;
    }

    public AiContentReport Build() => new(
        AiContentReport.Categories[Math.Clamp(CategoryIndex, 0, AiContentReport.Categories.Count - 1)],
        Note,
        Content,
        SafeModel(),
        AppVersion(),
        RuntimeInformation.OSDescription);

    [RelayCommand]
    public void SendEmail() => Send(Build().MailtoUri(), "e-mail");

    [RelayCommand]
    public void OpenIssue() => Send(Build().IssueUri(), "GitHub issue");

    [RelayCommand]
    public void Cancel() => IsOpen = false;

    private void Send(string uri, string via)
    {
        AppLogger.Log($"[Report] AI content report via {via} · category={CategoryIndex} · {Content.Length} chars");
        OpenUri?.Invoke(uri);
        IsOpen = false;
        SentMessage = $"Report opened as a {via} — review it and send it. Thank you.";
    }

    private string? SafeModel()
    {
        try { return ModelName(); }
        catch (Exception ex) { AppLogger.Log($"[Report] model name unavailable: {ex.Message}"); return null; }
    }

    private static string AppVersion() =>
        typeof(AiReportViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";
}
