using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Akka.Actor;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Agent.Common.Actors;
using Agent.Common.Llm;
using Agent.Common.Llm.Tools;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Models;
using AgentZeroAvalonia.Tools;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// 채팅 화면 ViewModel. WPF의 AgentBotWindow가 하던 액터 배선을 최소 형태로
/// 재현한다 — 단, External(OpenAI 호환 REST) 백엔드 전용. (로컬 llama.cpp는
/// win-x64 네이티브 전용이라 Mac에서 미동작 → 코어는 REST로 cross-platform.)
///
/// 흐름:  Stage.Ask(CreateBot) → bot ref
///        bot.Tell(SetAgentLoopCallbacks) + bot.Tell(AgentLoopBindings)  (1회)
///        전송 시  bot.Tell(StartAgentLoop(text))
///        진행/결과 콜백은 액터 스레드에서 오므로 Dispatcher.UIThread로 마샬링.
/// </summary>
public partial class AgentChatViewModel : ObservableObject
{
    private IActorRef? _bot;
    private bool _wired;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _statusText = "초기화 중…";

    [ObservableProperty]
    private bool _isBusy;

    public AgentChatViewModel()
    {
        Messages.Add(new ChatMessage
        {
            Role = ChatRole.System,
            Text = "AgentZero Lite (Avalonia) — External LLM 채팅. " +
                   "엔드포인트는 %LOCALAPPDATA%\\AgentZeroLite\\llm-settings.json 로 설정됩니다.",
        });
    }

    /// <summary>앱 시작 후 View가 1회 호출. Stage에서 bot 액터를 받아 콜백/바인딩을 건다.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            if (!ActorSystemManager.IsInitialized)
            {
                StatusText = "ActorSystem 미초기화 — 채팅 불가";
                return;
            }

            var created = await ActorSystemManager.Stage
                .Ask<BotCreated>(new CreateBot(), TimeSpan.FromSeconds(5));
            _bot = created.BotRef;

            WireAgentLoop();
            StatusText = "준비됨";
        }
        catch (Exception ex)
        {
            StatusText = $"초기화 실패: {ex.Message}";
        }
    }

    private void WireAgentLoop()
    {
        if (_wired || _bot is null) return;
        _wired = true;

        // 진행/결과 콜백 — 액터 스레드 → UI 스레드 마샬링.
        _bot.Tell(new SetAgentLoopCallbacks(
            OnProgress: p => Dispatcher.UIThread.Post(() => HandleProgress(p)),
            OnResult:   r => Dispatcher.UIThread.Post(() => HandleResult(r))));

        // 백엔드 팩토리 묶음. 액터가 OptionsFactory 결과에 콜백을 주입한 뒤
        // AgentLoopFactory(opts, toolbelt)로 실제 루프를 만든다.
        _bot.Tell(new AgentLoopBindings(
            // 실제 터미널 제어 toolbelt — 에이전트가 TerminalRegistry의 PTY 세션을
            // list/read/send/send_key로 구동. (터미널 탭이 세션을 등록해 둠.)
            ToolbeltFactory: () => new PtyTerminalToolbelt(),
            OptionsFactory:  () => new AgentLoopOptions
            {
                MaxTokensPerTurn = 1024,
                Temperature = 0.0f,
            },
            AgentLoopFactory: (opts, host) =>
            {
                var settings = LlmSettingsStore.Load();
                var provider = settings.CreateExternalProvider();
                if (provider is null) return null; // 미설정 → 액터가 실패 결과 전송
                var model = settings.ResolveExternalModel();
                if (string.IsNullOrWhiteSpace(model)) return null;
                return new ExternalAgentLoop(provider, model, host, opts);
            }));
    }

    private bool CanSend() => !IsBusy && _bot is not null && !string.IsNullOrWhiteSpace(InputText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || _bot is null) return;

        // 미설정 사전 안내 (액터 실패 결과보다 친절한 메시지).
        if (LlmSettingsStore.Load().CreateExternalProvider() is null)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "External LLM이 설정되지 않았습니다. llm-settings.json에 " +
                       "Provider/엔드포인트/모델을 지정하세요.",
            });
            return;
        }

        Messages.Add(new ChatMessage { Role = ChatRole.User, Text = text });
        InputText = string.Empty;
        IsBusy = true;
        StatusText = "전송됨 — 응답 대기 중…";
        SendCommand.NotifyCanExecuteChanged();

        _bot.Tell(new StartAgentLoop(text));
    }

    private void HandleProgress(AgentLoopProgress p)
    {
        StatusText = p.Phase switch
        {
            AgentLoopPhase.Thinking   => "생각 중…",
            AgentLoopPhase.Generating => $"생성 중… ({p.Tokens} 토큰)",
            AgentLoopPhase.Acting     => "도구 실행 중…",
            _ => StatusText,
        };
    }

    private void HandleResult(AgentLoopResult r)
    {
        IsBusy = false;
        SendCommand.NotifyCanExecuteChanged();

        if (r.Success)
        {
            Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = r.FinalMessage });
            StatusText = $"완료 ({r.TurnCount} turn, {r.ElapsedMs} ms)";
        }
        else
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = $"실패: {r.FailureReason ?? "알 수 없는 오류"}",
            });
            StatusText = "실패";
        }
    }

    // InputText 변경 시 Send 버튼 활성 상태 갱신.
    partial void OnInputTextChanged(string value) => SendCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
}
