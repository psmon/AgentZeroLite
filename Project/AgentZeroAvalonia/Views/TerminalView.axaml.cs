using System;
using Avalonia.Controls;

namespace AgentZeroAvalonia.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();

        // OS 기본 셸 지정. (컨트롤이 attach 시 Process가 설정돼 있으면 자동 기동)
        Term.Process = DefaultShell();
        Term.StartingDirectory = Environment.CurrentDirectory;
    }

    private static string DefaultShell()
    {
        if (OperatingSystem.IsWindows()) return "powershell.exe";
        if (OperatingSystem.IsMacOS()) return "/bin/zsh";
        return "/bin/bash";
    }
}
