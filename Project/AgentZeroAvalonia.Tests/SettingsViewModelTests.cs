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
}
