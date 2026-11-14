using Agent.Common.Data.Entities;
using AgentZeroAvalonia.ViewModels;
using Xunit;

namespace AgentZeroAvalonia.Tests;

public class SettingsViewModelTests
{
    [Fact]
    public void Cli_definition_validation_needs_a_name_and_an_executable()
    {
        var item = new CliDefinitionItem(new CliDefinition());
        Assert.Equal("A name is required.", item.Validate());
        item.Name = "zsh";
        Assert.Equal("An executable is required.", item.Validate());
        item.ExePath = "/bin/zsh";
        Assert.Null(item.Validate());
        item.IsRemote = true;
        Assert.Equal("SSH needs a host.", item.Validate());
        item.SshHost = "example.org";
        // ssh connects as user@host — half a target is a tab that would silently open a
        // local shell instead.
        Assert.Equal("SSH needs a user.", item.Validate());
        item.SshUser = "me";
        Assert.Null(item.Validate());
    }

    [Fact]
    public void Cli_definition_item_round_trips_to_the_entity_without_touching_the_stored_password()
    {
        var entity = new CliDefinition
        {
            Id = 7, Name = "Claude", ExePath = "claude", Arguments = "--resume", IsBuiltIn = true, ReducedMotion = true,
            IsRemote = true, SshHost = "h", SshUser = "u", SshAuthMethod = "Password", EncryptedPassword = "dpapi:v1:xyz",
        };
        var item = new CliDefinitionItem(entity);
        Assert.True(item.IsBuiltIn);
        Assert.False(item.UsePublicKey);
        Assert.Equal("(unchanged)", item.PasswordHint);

        var target = new CliDefinition { Id = 7 };
        item.Name = "Claude Code";
        item.ApplyTo(target, protect: s => "sealed:" + s);
        Assert.Equal("Claude Code", target.Name);
        Assert.Equal("--resume", target.Arguments);
        Assert.Equal("dpapi:v1:xyz", target.EncryptedPassword);   // no new password typed: kept
        Assert.Equal("Password", target.SshAuthMethod);

        item.NewPassword = "hunter2";
        item.ApplyTo(target, protect: s => "sealed:" + s);
        Assert.Equal("sealed:hunter2", target.EncryptedPassword);

        item.UsePublicKey = true;
        item.SshKeyPath = "/home/me/.ssh/id.pem";
        item.ApplyTo(target, protect: s => "sealed:" + s);
        Assert.Equal("PublicKey", target.SshAuthMethod);
        Assert.Equal("/home/me/.ssh/id.pem", target.SshKeyPath);
    }

    [Fact]
    public void Terminal_settings_are_clamped_before_saving()
    {
        var s = SettingsViewModel.BuildTerminalSettings(fontFamily: "  ", fontSize: 99, lineHeight: 0.1, cursorBlink: true, useWebGl: false, themeName: "Nope");
        Assert.Equal(32, s.FontSize);
        Assert.Equal(0.8, s.LineHeight);
        Assert.False(string.IsNullOrWhiteSpace(s.FontFamily));      // fell back to the default stack
        Assert.True(s.CursorBlink);
        Assert.NotEqual("Nope", s.ThemeName);                         // unknown theme falls back to the default
    }

    [Fact]
    public void Cli_definition_takes_a_command_and_writes_the_shell_flags_itself()
    {
        var item = new CliDefinitionItem(new CliDefinition { ExePath = "powershell.exe", Arguments = "-NoExit -Command claude" });
        Assert.Equal("claude", item.RunCommand);            // the seeded row reads as its command
        Assert.True(item.UsesRunCommand);

        item.RunCommand = "codex";
        Assert.Equal("-NoExit -Command codex", item.Arguments);
        item.ExePath = "cmd.exe";                            // another shell, same command
        Assert.Equal("/K codex", item.Arguments);
        item.ExePath = "/bin/bash";
        Assert.Equal("-l -i -c \"codex; exec bash -l\"", item.Arguments);
        Assert.Equal("/bin/bash -l -i -c \"codex; exec bash -l\"", item.LaunchPreview);

        var custom = new CliDefinitionItem(new CliDefinition { ExePath = "pwsh.exe", Arguments = "-NoLogo -NoExit -Command claude" });
        Assert.True(custom.RawArguments);                    // hand-written flags are shown, never rewritten
        Assert.False(custom.UsesRunCommand);
        Assert.Equal("-NoLogo -NoExit -Command claude", custom.Arguments);

        var program = new CliDefinitionItem(new CliDefinition { ExePath = "claude", Arguments = "--resume" });
        Assert.False(program.IsShell);                       // a program run directly keeps its own arguments
        Assert.True(program.ShowArguments);
    }

    [Fact]
    public async Task Font_catalog_always_offers_the_bundled_font()
    {
        // Headless there may be no font manager at all; the font the app ships must still be listed.
        var fonts = await AgentZeroAvalonia.Services.InstalledFontCatalog.LoadAsync();
        var bundled = Assert.Single(fonts, f => f.Name == AgentZeroAvalonia.Services.InstalledFontCatalog.BundledFamily);
        Assert.True(bundled.IsBundled);
        Assert.True(bundled.IsMonospace);
        Assert.Equal(fonts.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => f.Name), fonts.Select(f => f.Name));
    }
}
