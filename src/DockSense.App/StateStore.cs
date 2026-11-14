using System.Text.Json;
using DockSense.Core;

namespace DockSense.App;

/// <summary>What the firmware's iGPU memory setting was when this Windows session started.</summary>
/// <param name="Boot">The boot session this was recorded in.</param>
/// <param name="EffectiveRaw">The raw firmware value in effect during this boot session.</param>
internal sealed record IgpuMemoryBootState(BootSession Boot, uint EffectiveRaw);

/// <summary>A downloaded and verified release, waiting to replace the running executable.</summary>
/// <param name="Version">The release version, as in its tag (e.g. "0.3").</param>
/// <param name="Path">The extracted DockSense.exe in the updates folder.</param>
/// <param name="Sha256">Lower-case hex digest of that file, checked again right before it is installed.</param>
internal sealed record StagedUpdate(string Version, string Path, string Sha256);

/// <summary>What the updater remembers between runs.</summary>
/// <param name="LastCheck">When GitHub was last asked successfully.</param>
/// <param name="NotifiedVersion">The newest version the user was already told about.</param>
internal sealed record UpdateState(DateTimeOffset? LastCheck = null, string? NotifiedVersion = null, StagedUpdate? Staged = null);

/// <summary>Runtime state that must survive restarts of DockSense (not user settings).</summary>
internal static class StateStore
{
    private const string PowerPlanFile = "state.json";
    private const string IgpuMemoryFile = "igpu-memory.json";
    private const string UpdateFile = "update.json";

    /// <summary>Which plan DockSense owns and what to restore.</summary>
    public static PowerPlanState? Load() => Load<PowerPlanState>(PowerPlanFile);

    public static void Save(PowerPlanState state) => Save(PowerPlanFile, state);

    public static IgpuMemoryBootState? LoadIgpuMemory() => Load<IgpuMemoryBootState>(IgpuMemoryFile);

    public static void SaveIgpuMemory(IgpuMemoryBootState state) => Save(IgpuMemoryFile, state);

    public static UpdateState LoadUpdate() => Load<UpdateState>(UpdateFile) ?? new UpdateState();

    public static void SaveUpdate(UpdateState state) => Save(UpdateFile, state);

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
