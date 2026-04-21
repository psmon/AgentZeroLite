using System.Windows;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace AgentZeroWpf.UI.APP;

public partial class TerminalTestWindow : Window
{
    public TerminalTestWindow()
    {
        InitializeComponent();

        TestTerm.Theme = new TerminalTheme
        {
            DefaultBackground = EasyTerminalControl.ColorToVal(Color.FromRgb(0x0A, 0x0A, 0x12)),
            DefaultForeground = EasyTerminalControl.ColorToVal(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            DefaultSelectionBackground = 0x004B72,
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable = new uint[]
            {
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2,
            },
        };

        Loaded += (_, _) =>
        {
            ThemeHelper.ApplyDarkTitleBar(this);

            // Start() 대신 RestartTerm() 사용 — 출력 파이프를 UI에 연결
            AppLogger.Log("[TEST] RestartTerm() 호출");
            TestTerm.RestartTerm();
            AppLogger.Log("[TEST] RestartTerm() 완료");
        };
    }
}
