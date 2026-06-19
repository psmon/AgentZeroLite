using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Akka.Actor;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Agent.Common.Actors;
using Agent.Common.Services;
using AgentZeroAvalonia.Actors;
using AgentZeroAvalonia.Services;

namespace AgentZeroAvalonia.ViewModels;

/// <summary>
/// 터미널 화면 ViewModel. cross-platform <see cref="PtyTerminalSession"/>를
/// 띄워 원시 출력을 표시하고 입력을 전달한다. (원시 I/O MVP — 완전한 VT100
/// 렌더링은 후속 단계.)
/// </summary>
public partial class TerminalViewModel : ObservableObject, IDisposable
{
    // ANSI 이스케이프 정리 — 표시용. 원시 로그(ITerminalSession)는 그대로 보존.
    //  CSI:  ESC [ ... 종결문자        OSC:  ESC ] ... (BEL | ESC \)
    private static readonly Regex AnsiCsi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex AnsiOsc = new(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.Compiled);
    private static readonly Regex OtherCtl = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    private const int MaxDisplayChars = 100_000;
    private const string WorkspaceName = "default";

    private readonly int _tab;
    private readonly string _terminalId;
    private ITerminalSession? _session;
    private bool _initialized;
    private readonly StringBuilder _display = new();

    public TerminalViewModel() : this(0) { }

    public TerminalViewModel(int tab)
    {
        _tab = tab;
        _terminalId = $"term-{tab}";
    }

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _statusText = "터미널 시작 중…";

    /// <summary>View가 1회 호출. OS 기본 셸로 PTY 세션을 띄우고 레지스트리+액터에 연결.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _session = await PtyTerminalSession.CreateAsync(_terminalId);
            _session.OutputReceived += OnOutput;

            // 1) 에이전트 toolbelt 조회용 레지스트리 등록 (group 0 / tab N).
            TerminalRegistry.Register(0, _tab, $"shell {_tab}", _session);

            // 2) 액터 토폴로지 연결: 워크스페이스(멱등) → 터미널 액터 생성 → 세션 바인드.
            //    (/user/stage/ws-default/term-{id} 가 ITerminalSession을 래핑)
            var stage = ActorSystemManager.Stage;
            stage.Tell(new RegisterWorkspace(WorkspaceName, Environment.CurrentDirectory));
            stage.Tell(new CreateTerminalInWorkspace(WorkspaceName, _terminalId, _session.SessionId));
            stage.Tell(new BindSessionInWorkspace(WorkspaceName, _terminalId, _session));

            StatusText = $"실행 중 (session {_session.InternalId}, /ws-{WorkspaceName}/{_terminalId})";
        }
        catch (Exception ex)
        {
            StatusText = $"터미널 시작 실패: {ex.Message}";
        }
    }

    private void OnOutput(TerminalOutputFrame frame)
    {
        // 액터/PTY 읽기 스레드 → UI 스레드 마샬링.
        Dispatcher.UIThread.Post(() =>
        {
            _display.Append(Sanitize(frame.Text));
            if (_display.Length > MaxDisplayChars)
                _display.Remove(0, _display.Length - MaxDisplayChars);
            OutputText = _display.ToString();
        });
    }

    private static string Sanitize(string raw)
    {
        var s = AnsiOsc.Replace(raw, string.Empty);
        s = AnsiCsi.Replace(s, string.Empty);
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = OtherCtl.Replace(s, string.Empty);
        return s;
    }

    private bool CanSend() => _session is { IsRunning: true };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (_session is null) return;
        _session.Write(InputText.AsSpan());
        _session.SendControl(TerminalControl.Enter);
        InputText = string.Empty;
    }

    public void Dispose()
    {
        if (_session is not null)
        {
            _session.OutputReceived -= OnOutput;
            TerminalRegistry.Unregister(_session);
            if (ActorSystemManager.IsInitialized)
                ActorSystemManager.Stage.Tell(
                    new DestroyTerminalInWorkspace(WorkspaceName, _terminalId));
            (_session as IDisposable)?.Dispose();
            _session = null;
        }
    }
}
