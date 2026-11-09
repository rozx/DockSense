namespace DockSense.Core;

/// <summary>Turns a configured program into the file and arguments to start.</summary>
public static class ProgramCommandLine
{
    /// <summary>
    /// Returns what to start for <paramref name="path"/> (already expanded). Programs, batch files, shortcuts and
    /// URLs start as they are. PowerShell scripts run through <paramref name="powerShell"/>, because the shell's
    /// default action for <c>.ps1</c> opens them in an editor instead of running them.
    /// </summary>
    public static (string FileName, string Arguments) Resolve(string path, string arguments, string powerShell)
    {
        ArgumentNullException.ThrowIfNull(path);
        arguments ??= string.Empty;

        if (path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var script = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"";
            return (powerShell, arguments.Length == 0 ? script : $"{script} {arguments}");
        }

        return (path, arguments);
    }
}
