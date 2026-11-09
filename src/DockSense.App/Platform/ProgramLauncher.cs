using System.ComponentModel;
using System.Diagnostics;
using DockSense.Core;

namespace DockSense.App.Platform;

/// <summary>Starts the programs configured in a mode profile.</summary>
internal static class ProgramLauncher
{
    private const int ERROR_CANCELLED = 1223;

    /// <summary>Windows PowerShell, present on every supported Windows version (runs .ps1 scripts).</summary>
    private static readonly string WindowsPowerShell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>A short display name for a configured program, e.g. "steam.exe".</summary>
    public static string DisplayName(ProgramLaunch program)
    {
        var path = Environment.ExpandEnvironmentVariables(program.Path.Trim().Trim('"'));
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// Starts the program through the shell, so programs, scripts, documents and URLs all work. Elevated launches
    /// wait for the UAC prompt, so this must not run on the UI thread. Returns null on success, or an error message.
    /// </summary>
    public static string? Start(ProgramLaunch program)
    {
        var path = Environment.ExpandEnvironmentVariables(program.Path.Trim().Trim('"'));
        var (fileName, arguments) = ProgramCommandLine.Resolve(
            path, Environment.ExpandEnvironmentVariables(program.Arguments ?? string.Empty), WindowsPowerShell);
        var info = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WindowStyle = program.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            Verb = program.RunAsAdministrator ? "runas" : string.Empty,
        };

        if (Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { Length: > 0 } directory && Directory.Exists(directory))
        {
            info.WorkingDirectory = directory;
        }

        try
        {
            using var process = Process.Start(info);
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return "cancelled at the administrator prompt";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return ex.Message;
        }
    }
}
