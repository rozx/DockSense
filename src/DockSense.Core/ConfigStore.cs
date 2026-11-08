using System.Text.Json;
using System.Text.Json.Serialization;

namespace DockSense.Core;

/// <summary>Loads and saves <see cref="DockSenseConfig"/> as JSON. Never throws on a missing or corrupt file.</summary>
public sealed class ConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConfigStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
    }

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockSense", "config.json");

    public string FilePath { get; }

    /// <summary>
    /// Returns the stored config, or defaults when the file is missing. A corrupt file is renamed to
    /// <c>config.json.bad-yyyyMMddHHmmss</c> (so the user's settings are not silently lost) and defaults are returned.
    /// </summary>
    public DockSenseConfig Load(Action<string>? log = null)
    {
        if (!File.Exists(FilePath))
        {
            return new DockSenseConfig();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DockSenseConfig>(json, JsonOptions) ?? new DockSenseConfig();
        }
        catch (JsonException ex)
        {
            var backup = $"{FilePath}.bad-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(FilePath, backup, overwrite: true);
            log?.Invoke($"Config file was unreadable ({ex.Message}); moved it to {backup} and using defaults.");
            return new DockSenseConfig();
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash cannot leave a half-written config.</summary>
    public void Save(DockSenseConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
