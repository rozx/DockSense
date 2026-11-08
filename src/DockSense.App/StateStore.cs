using System.Text.Json;
using DockSense.Core;

namespace DockSense.App;

/// <summary>Runtime state that must survive restarts (which plan DockSense owns and what to restore).</summary>
internal static class StateStore
{
    private static readonly string FilePath = Path.Combine(Log.DataDirectory, "state.json");

    public static PowerPlanState? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<PowerPlanState>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Ignoring unreadable state file: {ex.Message}");
            return null;
        }
    }

    public static void Save(PowerPlanState state)
    {
        try
        {
            Directory.CreateDirectory(Log.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state));
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not save state: {ex.Message}");
        }
    }
}
