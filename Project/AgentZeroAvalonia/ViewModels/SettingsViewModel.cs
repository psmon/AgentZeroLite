using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Agent.Common.Data;
using Agent.Common.Data.Entities;
using Agent.Common.Llm;
using Agent.Common.Llm.Providers;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// 설정 — External LLM 구성(채팅이 사용하는 provider/endpoint/model) + CLI 정의 CRUD.
/// (Local 모델 다운로드/튜닝·Voice·Music 탭은 cross-platform 범위 밖이라 제외.)
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<string> Providers { get; } = ExternalProviderNames.All;

    [ObservableProperty] private string _provider = ExternalProviderNames.Webnori;
    [ObservableProperty] private string _selectedModel = "";
    [ObservableProperty] private int _maxTokens = 4096;
    [ObservableProperty] private string _openAIApiKey = "";
    [ObservableProperty] private string _openAIBaseUrl = "";
    [ObservableProperty] private string _lmStudioBaseUrl = "";
    [ObservableProperty] private string _lmStudioApiKey = "";
    [ObservableProperty] private string _ollamaBaseUrl = "";
    [ObservableProperty] private string _llmStatus = "";

    public ObservableCollection<CliDefinition> CliDefinitions { get; } = new();
    [ObservableProperty] private CliDefinition? _selectedCli;
    [ObservableProperty] private string _cliStatus = "";

    public SettingsViewModel()
    {
        LoadLlm();
        LoadCliDefinitions();
    }

    private void LoadLlm()
    {
        var s = LlmSettingsStore.Load();
        Provider = s.External.Provider;
        SelectedModel = s.External.SelectedModel;
        MaxTokens = s.External.MaxTokens;
        OpenAIApiKey = s.External.OpenAIApiKey;
        OpenAIBaseUrl = s.External.OpenAIBaseUrl;
        LmStudioBaseUrl = s.External.LMStudioBaseUrl;
        LmStudioApiKey = s.External.LMStudioApiKey;
        OllamaBaseUrl = s.External.OllamaBaseUrl;
    }

    [RelayCommand]
    private void SaveLlm()
    {
        var s = LlmSettingsStore.Load();
        s.ActiveBackend = LlmActiveBackend.External; // Avalonia 빌드는 External 전용
        s.External.Provider = Provider;
        s.External.SelectedModel = SelectedModel;
        s.External.MaxTokens = MaxTokens;
        s.External.OpenAIApiKey = OpenAIApiKey;
        s.External.OpenAIBaseUrl = OpenAIBaseUrl;
        s.External.LMStudioBaseUrl = LmStudioBaseUrl;
        s.External.LMStudioApiKey = LmStudioApiKey;
        s.External.OllamaBaseUrl = OllamaBaseUrl;
        LlmSettingsStore.Save(s);
        LlmStatus = $"저장됨 — {Provider} / {(string.IsNullOrWhiteSpace(SelectedModel) ? "(기본 모델)" : SelectedModel)}";
    }

    private void LoadCliDefinitions()
    {
        CliDefinitions.Clear();
        try
        {
            using var db = new AppDbContext();
            foreach (var c in db.CliDefinitions.OrderBy(c => c.SortOrder).ToList())
                CliDefinitions.Add(c);
            CliStatus = $"{CliDefinitions.Count}개";
        }
        catch (System.Exception ex) { CliStatus = $"로드 실패: {ex.Message}"; }
    }

    [RelayCommand]
    private void AddCli()
    {
        try
        {
            using var db = new AppDbContext();
            var maxSort = db.CliDefinitions.Any() ? db.CliDefinitions.Max(c => c.SortOrder) : -1;
            var def = new CliDefinition
            {
                Name = "새 CLI",
                ExePath = System.OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/zsh",
                IsBuiltIn = false,
                SortOrder = maxSort + 1,
            };
            db.CliDefinitions.Add(def);
            db.SaveChanges();
        }
        catch (System.Exception ex) { CliStatus = $"추가 실패: {ex.Message}"; return; }
        LoadCliDefinitions();
    }

    [RelayCommand]
    private void DeleteCli()
    {
        if (SelectedCli is null) { CliStatus = "삭제할 항목을 선택하세요"; return; }
        if (SelectedCli.IsBuiltIn) { CliStatus = "기본 제공 CLI는 삭제할 수 없습니다"; return; }
        try
        {
            using var db = new AppDbContext();
            var row = db.CliDefinitions.Find(SelectedCli.Id);
            if (row is not null) { db.CliDefinitions.Remove(row); db.SaveChanges(); }
        }
        catch (System.Exception ex) { CliStatus = $"삭제 실패: {ex.Message}"; return; }
        LoadCliDefinitions();
    }
}
