using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class StartupEntryTests
{
    private const string Command = "\"C:\\Program Files (x86)\\DockSense\\DockSense.exe\" --autostart";
    private const string Legacy = "\"C:\\Program Files (x86)\\DockSense\\DockSense.exe\"";
    private static readonly string[] Expected = [Command, Legacy];

    // Values Windows wrote on the Ally X (build 26300) and the ones older builds use.
    private static readonly byte[] DisabledNow = [0x01, 0, 0, 0, 0x42, 0xFD, 0xCE, 0xB7, 0xC7, 0x73, 0xDD, 0x01];
    private static readonly byte[] EnabledNow = new byte[12];
    private static readonly byte[] DisabledOlder = [0x03, 0, 0, 0, 0x42, 0xFD, 0xCE, 0xB7, 0xC7, 0x73, 0xDD, 0x01];
    private static readonly byte[] EnabledOlder = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_run_entry_is_off(string? command)
    {
        Assert.Equal(new StartupStatus(StartupState.Off), StartupEntry.Evaluate(command, Expected, null, null, xboxModeAvailable: true));
    }

    [Fact]
    public void An_entry_for_this_copy_is_on()
    {
        Assert.Equal(StartupState.On, StartupEntry.Evaluate(Command, Expected, null, 1, true).State);
        Assert.Equal(StartupState.On, StartupEntry.Evaluate(Legacy, Expected, EnabledNow, 1, true).State);
        Assert.Equal(StartupState.On, StartupEntry.Evaluate(Command.ToUpperInvariant(), Expected, EnabledOlder, 1, true).State);
    }

    [Fact]
    public void An_entry_switched_off_in_windows_is_reported()
    {
        Assert.Equal(new StartupStatus(StartupState.DisabledByWindows), StartupEntry.Evaluate(Command, Expected, DisabledNow, 1, true));
        Assert.Equal(new StartupStatus(StartupState.DisabledByWindows), StartupEntry.Evaluate(Command, Expected, DisabledOlder, 1, true));
    }

    [Fact]
    public void An_entry_for_another_copy_is_reported_with_its_command()
    {
        const string other = "\"D:\\Old\\DockSense.exe\" --autostart";

        var status = StartupEntry.Evaluate(other, Expected, DisabledNow, null, true);

        Assert.Equal(new StartupStatus(StartupState.OtherCopy, OtherCommand: other), status);
    }

    [Theory]
    [InlineData(null, true)] // never chosen: Windows waits for the desktop
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(1, false)] // "Start at log in"
    public void Without_start_at_log_in_xbox_mode_waits_for_the_desktop(int? xboxModeValue, bool waits)
    {
        Assert.Equal(waits, StartupEntry.Evaluate(Command, Expected, null, xboxModeValue, xboxModeAvailable: true).WaitsForDesktop);
    }

    [Fact]
    public void Xbox_mode_does_not_matter_where_it_does_not_exist_or_the_entry_is_not_on()
    {
        Assert.False(StartupEntry.Evaluate(Command, Expected, null, null, xboxModeAvailable: false).WaitsForDesktop);
        Assert.False(StartupEntry.Evaluate(Command, Expected, DisabledNow, null, true).WaitsForDesktop);
        Assert.False(StartupEntry.Evaluate(null, Expected, null, null, true).WaitsForDesktop);
    }

    [Fact]
    public void Disabled_is_an_odd_first_byte()
    {
        Assert.False(StartupEntry.IsDisabledByWindows(null));
        Assert.False(StartupEntry.IsDisabledByWindows([]));
        Assert.False(StartupEntry.IsDisabledByWindows(EnabledNow));
        Assert.False(StartupEntry.IsDisabledByWindows(EnabledOlder));
        Assert.True(StartupEntry.IsDisabledByWindows(DisabledNow));
        Assert.True(StartupEntry.IsDisabledByWindows(DisabledOlder));
        Assert.True(StartupEntry.IsDisabledByWindows([0x07]));
        Assert.False(StartupEntry.IsDisabledByWindows([0x06]));
    }
}
