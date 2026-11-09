using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class PowerSettingCatalogTests
{
    private static PowerSettingDefinition Setting(string key) => PowerSettingCatalog.Settings.Single(s => s.Key == key);

    [Fact]
    public void Keys_and_setting_guids_are_unique()
    {
        var settings = PowerSettingCatalog.Settings;

        var allGuids = settings.Select(s => s.Setting).Concat(settings.SelectMany(s => s.Twins)).ToList();

        Assert.Equal(settings.Count, settings.Select(s => s.Key).Distinct().Count());
        Assert.Equal(allGuids.Count, allGuids.Distinct().Count());
    }

    [Fact]
    public void Minutes_are_stored_as_seconds()
    {
        var sleep = Setting("Sleep");

        Assert.Equal(15 * 60u, sleep.ToRaw(15));
        Assert.Equal(15, sleep.ToDisplay(900));
        Assert.Equal(0u, sleep.ToRaw(0));
    }

    [Fact]
    public void Odd_second_values_round_to_whole_minutes_for_display()
    {
        Assert.Equal(2, Setting("DisplayOff").ToDisplay(90));
    }

    [Fact]
    public void Values_are_clamped_to_the_allowed_range()
    {
        var epp = Setting("Epp");

        Assert.Equal(100u, epp.ToRaw(250));
        Assert.Equal(0u, epp.ToRaw(-5));
        Assert.Equal(42u, epp.ToRaw(41.6));
    }

    [Fact]
    public void Choices_accept_only_listed_values()
    {
        var boost = Setting("Boost");

        Assert.True(boost.IsValid(2));
        Assert.False(boost.IsValid(7));
        Assert.Equal(6, boost.Maximum);
    }

    [Fact]
    public void Processor_limits_also_write_the_efficiency_class_copy()
    {
        Assert.Equal([new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")], Setting("Epp").Twins);
        Assert.Equal([new Guid("bae08b81-2d5e-4688-ad6a-13243356654b")], Setting("CorePreference").Twins);
    }

    [Fact]
    public void Overlays_are_not_built_in_plans()
    {
        Assert.Empty(PowerSettingCatalog.Overlays.Intersect(PowerSettingCatalog.BuiltInPlans));
        Assert.Contains(PowerSettingCatalog.BalancedPlan, PowerSettingCatalog.BuiltInPlans);
    }
}
