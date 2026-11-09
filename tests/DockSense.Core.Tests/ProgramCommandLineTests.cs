using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class ProgramCommandLineTests
{
    private const string PowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    [Fact]
    public void Programs_start_as_they_are()
    {
        Assert.Equal((@"C:\Games\steam.exe", "-gamepadui"), ProgramCommandLine.Resolve(@"C:\Games\steam.exe", "-gamepadui", PowerShell));
        Assert.Equal(("steam://open/bigpicture", ""), ProgramCommandLine.Resolve("steam://open/bigpicture", "", PowerShell));
    }

    [Fact]
    public void Powershell_scripts_run_through_powershell_with_their_arguments()
    {
        var (file, arguments) = ProgramCommandLine.Resolve(@"C:\My Scripts\dock.PS1", "-Mode egpu", PowerShell);

        Assert.Equal(PowerShell, file);
        Assert.Equal("-NoProfile -ExecutionPolicy Bypass -File \"C:\\My Scripts\\dock.PS1\" -Mode egpu", arguments);
    }

    [Fact]
    public void Powershell_script_without_arguments()
    {
        Assert.Equal(
            (PowerShell, "-NoProfile -ExecutionPolicy Bypass -File \"C:\\s\\a.ps1\""),
            ProgramCommandLine.Resolve(@"C:\s\a.ps1", null!, PowerShell));
    }
}
