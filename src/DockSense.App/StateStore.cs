using System.Text.Json;
using DockSense.Core;

namespace DockSense.App;

/// <summary>What the firmware's iGPU memory setting was when this Windows session started.</summary>
/// <param name="BootTimeUtc">When Windows started; identifies the boot session.</param>
/// <param name="EffectiveRaw">The raw firmware value in effect during this boot session.</param>
internal sealed record IgpuMemoryBootState(DateTime BootTimeUtc, uint EffectiveRaw);

/// <summary>Runtime state that must survive restarts of DockSense (not user settings).</summary>
internal static class StateStore
{
    private const string PowerPlanFile = "state.json";
    private const string IgpuMemoryFile = "igpu-memory.json";

    /// <summary>Which plan DockSense owns and what to restore.</summary>
    public static PowerPlanState? Load() => Load<PowerPlanState>(PowerPlanFile);

    public static void Save(PowerPlanState state) => Save(PowerPlanFile, state);

    public static IgpuMemoryBootState? LoadIgpuMemory() => Load<IgpuMemoryBootState>(IgpuMemoryFile);

    public static void SaveIgpuMemory(IgpuMemoryBootState state) => Save(IgpuMemoryFile, state);

    private static T? Load<T>(string fileName)
        where T : class
    {
        var path = Path.Combine(Log.DataDirectory, fileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Ignoring unreadable state file {fileName}: {ex.Message}");
            return null;
        }
    }

    private static void Save<T>(string fileName, T state)
    {
        try
        {
            Directory.CreateDirectory(Log.DataDirectory);
            File.WriteAllText(Path.Combine(Log.DataDirectory, fileName), JsonSerializer.Serialize(state));
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not save {fileName}: {ex.Message}");
        }
    }
}
