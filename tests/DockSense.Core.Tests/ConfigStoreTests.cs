using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DockSenseTests", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_dir, "config.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_returns_defaults()
    {
        var config = new ConfigStore(ConfigPath).Load();

        Assert.Equal(new DockSenseConfig().SchemaVersion, config.SchemaVersion);
        Assert.Equal(PowerPlanKind.DockSensePerformance, config.Egpu.PowerPlan.Kind);
        Assert.Equal(FseStartup.FullScreenExperience, config.Handheld.Fse);
        Assert.Equal(FseStartup.Desktop, config.Docked.Fse);
    }

    [Fact]
    public void Save_then_load_round_trips()
    {
        var planId = Guid.NewGuid();
        var original = new DockSenseConfig
        {
            Docked = new ModeProfile
            {
                PowerPlan = PowerPlanChoice.Specific(planId),
                Fse = FseStartup.Unchanged,
                FseApplyImmediately = true,
                Programs = [new ProgramLaunch { Path = @"%LOCALAPPDATA%\Tool\tool.exe", Arguments = "--fast", When = ProgramTrigger.Leave, Hidden = true }],
            },
            Detection = new DetectionOptions { DockedRequiresAcPower = true, SettleSeconds = 8, ForceEgpuInstanceIds = [@"PCI\VEN_10DE&DEV_2684\X"] },
            ReassertPowerPlan = false,
            DockSensePerformancePlanId = planId,
            Ui = new UiSettings { Language = "zh-Hans", Theme = AppTheme.Dark, NotifyOnModeChange = false },
        };
        var store = new ConfigStore(ConfigPath);

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.Docked, loaded.Docked);
        Assert.Equal(original.Docked.Programs, loaded.Docked.Programs);
        Assert.Equal(PowerPlanKind.Specific, loaded.Docked.PowerPlan.Kind);
        Assert.Equal(planId, loaded.Docked.PowerPlan.PlanId);
        Assert.True(loaded.Detection.DockedRequiresAcPower);
        Assert.Equal(8, loaded.Detection.SettleSeconds);
        Assert.Equal(original.Detection.ForceEgpuInstanceIds, loaded.Detection.ForceEgpuInstanceIds);
        Assert.False(loaded.ReassertPowerPlan);
        Assert.Equal(planId, loaded.DockSensePerformancePlanId);
        Assert.Equal(original.Ui, loaded.Ui);
    }

    [Fact]
    public void Config_written_before_ui_settings_existed_gets_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "schemaVersion": 1, "reassertPowerPlan": false }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.False(config.ReassertPowerPlan);
        Assert.Equal("system", config.Ui.Language);
        Assert.True(config.Ui.NotifyOnModeChange);
    }

    [Fact]
    public void Null_or_missing_program_list_means_no_programs()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{ "handheld": { "programs": null }, "docked": { "fse": "Desktop" } }""");

        var config = new ConfigStore(ConfigPath).Load();

        Assert.Empty(config.Handheld.Programs);
        Assert.Empty(config.Docked.Programs);
        Assert.Equal(new ModeProfile { Fse = FseStartup.Desktop }, config.Docked);
    }

    [Fact]
    public void Json_is_human_readable()
    {
        new ConfigStore(ConfigPath).Save(new DockSenseConfig());

        var json = File.ReadAllText(ConfigPath);

        Assert.Contains("\"kind\": \"DockSensePerformance\"", json);
        Assert.Contains("\"fse\": \"FullScreenExperience\"", json);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_defaults_are_used()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ this is not json");
        string? logged = null;

        var config = new ConfigStore(ConfigPath).Load(message => logged = message);

        Assert.Equal(PowerPlanKind.DockSensePerformance, config.Egpu.PowerPlan.Kind);
        Assert.False(File.Exists(ConfigPath));
        Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
        Assert.NotNull(logged);
    }
}
