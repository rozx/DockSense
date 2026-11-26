using DockSense.Core;

namespace DockSense.Core.Tests;

public sealed class TdpTests
{
    private static readonly TdpDevice AllyX = TdpDevice.Find("RC74XA")!;
    private static readonly TdpDevice Ally = TdpDevice.Find("RC71L")!;

    [Theory]
    [InlineData("RC74XA", "RC74XA")]
    [InlineData("rc74xa", "RC74XA")]
    [InlineData(" RC73XA ", "RC73XA")]
    [InlineData("RC72LA", "RC72")]
    [InlineData("RC71L", "RC71")]
    public void A_known_board_is_found_by_the_start_of_its_name(string board, string expected)
    {
        Assert.Equal(expected, TdpDevice.Find(board)?.Board);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RC73YA")] // ROG Xbox Ally: another processor, limits not known
    [InlineData("MS-1T8K")]
    [InlineData("XRC74XA")]
    public void An_unknown_board_is_not_supported(string? board)
    {
        Assert.Null(TdpDevice.Find(board));
    }

    [Fact]
    public void The_xbox_ally_x_has_the_manufacturers_range()
    {
        Assert.Equal(7, AllyX.MinWatts);
        Assert.Equal(35, AllyX.WarnAboveWatts);
        Assert.Equal(55, AllyX.CeilingWatts);
        Assert.False(AllyX.Offers(6));
        Assert.True(AllyX.Offers(7));
        Assert.True(AllyX.Offers(55));
        Assert.False(AllyX.Offers(56));
    }

    [Theory]
    [InlineData(35, false)]
    [InlineData(36, true)]
    public void Above_the_manufacturers_maximum_is_flagged(int watts, bool exceeds)
    {
        Assert.Equal(exceeds, AllyX.ExceedsManufacturerMax(watts));
    }

    [Fact]
    public void The_warning_starts_at_the_lower_of_the_two_maximums()
    {
        // 30 W plugged in, 25 W on battery: 28 W is fine on the charger but too much on battery.
        Assert.Equal(25, Ally.WarnAboveWatts);
        Assert.True(Ally.ExceedsManufacturerMax(28));
    }

    [Theory]
    [InlineData(20, true, 20)]
    [InlineData(20, false, 20)]
    [InlineData(50, true, 50)]
    [InlineData(50, false, 35)] // never more than the firmware's own burst limit for the power source
    [InlineData(3, true, 7)]
    [InlineData(90, true, 53)]
    public void The_value_sent_stays_within_what_the_firmware_itself_uses(int watts, bool onAc, int sent)
    {
        Assert.Equal(sent, Ally.Clamp(watts, onAc));
    }

    [Theory]
    [InlineData(0x00B20F40, 0x1A, 0x24)] // Ryzen AI Z2 Extreme: "Family 26 Model 36"
    [InlineData(0x00A70F41, 0x19, 0x74)] // Ryzen Z1 Extreme
    [InlineData(0x000906A3, 0x6, 0x9A)]  // an Intel processor
    public void Family_and_model_come_from_cpuid(int eax, int family, int model)
    {
        Assert.Equal((family, model), AmdSmuPowerCommands.Identify(eax));
    }

    [Fact]
    public void Strix_point_uses_the_mailbox_that_was_measured()
    {
        var commands = AmdSmuPowerCommands.For(0x1A, 0x24)!;

        Assert.Equal(new SmuMailbox(0x3B10928, 0x3B10978, 0x3B10998), commands.Mailbox);
        Assert.Equal((0x14u, 0x15u, 0x16u), (commands.Sustained, commands.Fast, commands.Slow));
    }

    [Theory]
    [InlineData(0x19, 0x74)] // Phoenix
    [InlineData(0x19, 0x75)] // Hawk Point
    [InlineData(0x19, 0x78)] // Phoenix 2
    public void The_z1_series_uses_the_older_mailbox(int family, int model)
    {
        Assert.Equal(new SmuMailbox(0x3B10528, 0x3B10578, 0x3B10998), AmdSmuPowerCommands.For(family, model)!.Mailbox);
    }

    [Theory]
    [InlineData(0x1A, 0x60)] // Krackan Point: not measured, no supported device
    [InlineData(0x19, 0x44)] // Rembrandt
    [InlineData(0x17, 0x90)] // Van Gogh
    [InlineData(0x6, 0x9A)]
    public void Other_processors_get_no_commands(int family, int model)
    {
        Assert.Null(AmdSmuPowerCommands.For(family, model));
    }

    [Theory]
    [InlineData("--set-tdp 20", 20)]
    [InlineData("--SET-TDP 7", 7)]
    [InlineData("--set-tdp 055", 55)]
    public void The_helper_takes_a_whole_number_of_watts(string commandLine, int watts)
    {
        Assert.Equal(watts, TdpHelperTask.ParseWatts(commandLine.Split(' ')));
    }

    [Theory]
    [InlineData("--set-tdp")]
    [InlineData("--set-tdp ")]
    [InlineData("--set-tdp -5")]
    [InlineData("--set-tdp 20.5")]
    [InlineData("--set-tdp 1000")]
    [InlineData("--set-tdp 20 extra")]
    [InlineData("--set-tdp $(Arg0)")]
    [InlineData("--other 20")]
    public void The_helper_takes_nothing_else(string commandLine)
    {
        Assert.Null(TdpHelperTask.ParseWatts(commandLine.Split(' ')));
    }

    [Fact]
    public void The_helper_task_runs_elevated_on_demand_with_the_watts_as_its_parameter()
    {
        var xml = TdpHelperTask.BuildXml(@"C:\Program Files\DockSense\DockSense.exe", "S-1-5-21-1-2-3-1001");

        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<UserId>S-1-5-21-1-2-3-1001</UserId>", xml);
        Assert.Contains("<Triggers />", xml);
        Assert.Contains("<AllowStartOnDemand>true</AllowStartOnDemand>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<Arguments>--set-tdp $(Arg0)</Arguments>", xml);
    }

    [Fact]
    public void The_helper_task_is_recognized_when_it_starts_this_copy()
    {
        const string exe = @"C:\Program Files\DockSense\DockSense.exe";
        var task = LogonTaskDefinition.Parse(TdpHelperTask.BuildXml(exe, "S-1-5-21-1-2-3-1001"));

        Assert.True(TdpHelperTask.Starts(task, exe));
        Assert.False(TdpHelperTask.Starts(task, @"D:\Other\DockSense.exe"));
        Assert.False(TdpHelperTask.Starts(task! with { Enabled = false }, exe));
        Assert.False(TdpHelperTask.Starts(task with { Arguments = "--set-tdp 55" }, exe));
        Assert.False(TdpHelperTask.Starts(null, exe));
    }

    [Fact]
    public void A_task_changed_to_run_without_the_highest_privileges_is_not_the_helper()
    {
        var xml = TdpHelperTask.BuildXml(@"C:\Program Files\DockSense\DockSense.exe", "S-1-5-21-1-2-3-1001");

        Assert.True(TdpHelperTask.RunsElevated(xml));
        Assert.False(TdpHelperTask.RunsElevated(xml.Replace("HighestAvailable", "LeastPrivilege")));
        Assert.False(TdpHelperTask.RunsElevated(xml.Replace("<RunLevel>HighestAvailable</RunLevel>", string.Empty)));
        Assert.False(TdpHelperTask.RunsElevated("not xml"));
        Assert.False(TdpHelperTask.RunsElevated(null));
    }

    [Fact]
    public void Only_the_user_named_in_it_may_start_the_task_and_only_administrators_may_change_it()
    {
        Assert.Equal("D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;S-1-5-21-1-2-3-1001)", TdpHelperTask.SecurityDescriptorFor("S-1-5-21-1-2-3-1001"));
    }

    [Theory]
    [InlineData(20, false, TdpAction.Set)]
    [InlineData(20, true, TdpAction.Set)]
    [InlineData(null, true, TdpAction.HandBack)]
    [InlineData(null, false, TdpAction.None)]
    [InlineData(90, true, TdpAction.HandBack)] // a value from another device's settings is not sent
    [InlineData(90, false, TdpAction.None)]
    public void A_profile_sets_its_value_or_gives_control_back(int? watts, bool limitInForce, TdpAction expected)
    {
        Assert.Equal(expected, TdpPlanner.Plan(new ModeProfile { TdpWatts = watts }, AllyX, limitInForce));
    }

    [Theory]
    [InlineData(45, false, TdpAction.Set)]     // above the battery ceiling of 35 W: still set, and clamped when it is sent
    [InlineData(53, false, TdpAction.Set)]
    [InlineData(54, true, TdpAction.HandBack)] // above the highest ceiling
    [InlineData(3, true, TdpAction.HandBack)]  // below the minimum
    [InlineData(3, false, TdpAction.None)]
    public void On_the_older_models_the_plan_does_not_depend_on_the_power_source(int watts, bool limitInForce, TdpAction expected)
    {
        // Confirmed on this board up to its ceiling, so that only the range decides here.
        Assert.Equal(expected, TdpPlanner.Plan(new ModeProfile { TdpWatts = watts }, Ally, limitInForce, confirmedBoard: "RC71", confirmedWatts: 53));
    }

    [Theory]
    [InlineData(35, null, 0, TdpAction.Set)]          // within the manufacturer's maximum: nothing to confirm
    [InlineData(45, null, 0, TdpAction.HandBack)]     // above it and never confirmed: treated as no value
    [InlineData(45, "RC74XA", 45, TdpAction.Set)]
    [InlineData(45, "rc74xa", 50, TdpAction.Set)]     // a higher confirmation covers lower values
    [InlineData(50, "RC74XA", 45, TdpAction.HandBack)] // going higher needs a new confirmation
    [InlineData(45, "RC72", 53, TdpAction.HandBack)]  // confirmed on another device
    public void Above_the_maximum_a_value_counts_only_once_it_was_confirmed_on_this_board(int watts, string? board, int confirmed, TdpAction expected)
    {
        Assert.Equal(expected, TdpPlanner.Plan(new ModeProfile { TdpWatts = watts }, AllyX, limitInForce: true, board, confirmed));
    }

    [Fact]
    public void A_value_fine_on_one_board_can_need_confirming_on_another()
    {
        // 35 W is the Xbox Ally X's maximum, but above the ROG Ally X's 30 W plugged in and 25 W on battery.
        Assert.True(AllyX.IsConfirmed(35, confirmedBoard: null, confirmedWatts: 0));
        Assert.False(Ally.IsConfirmed(35, confirmedBoard: null, confirmedWatts: 0));
        Assert.False(Ally.IsConfirmed(35, confirmedBoard: "RC74XA", confirmedWatts: 55));
        Assert.True(Ally.IsConfirmed(35, confirmedBoard: "RC71", confirmedWatts: 35));
    }

    [Theory]
    [InlineData(120, TdpWriteResult.Ok, 20)]            // 100 plus the watts that were sent
    [InlineData(155, TdpWriteResult.Ok, 55)]
    [InlineData(0, TdpWriteResult.Unconfirmed, null)]   // "done" without saying what was sent is not an answer
    [InlineData(3, TdpWriteResult.NotElevated, null)]
    [InlineData(9, TdpWriteResult.Busy, null)]
    [InlineData(10, TdpWriteResult.PartlyApplied, null)]
    [InlineData(-1073741510, TdpWriteResult.Unconfirmed, null)] // ended from outside
    [InlineData(42, TdpWriteResult.Unconfirmed, null)]
    public void The_helpers_exit_code_says_how_it_ended_and_what_it_sent(int exitCode, TdpWriteResult result, int? sent)
    {
        Assert.Equal(new TdpWriteOutcome(result, sent), TdpHelperTask.OutcomeOf(exitCode));
    }

    [Fact]
    public void A_write_that_went_through_reports_its_watts()
    {
        Assert.Equal(new TdpWriteOutcome(TdpWriteResult.Ok, 35), TdpHelperTask.OutcomeOf(TdpHelperTask.ExitCodeForSent(35)));
    }

    [Theory]
    [InlineData(53, true, 53)]
    [InlineData(54, true, 53)]
    [InlineData(35, false, 35)]
    [InlineData(36, false, 35)]
    public void The_older_models_stop_at_their_ceiling_for_the_power_source(int watts, bool onAc, int sent)
    {
        Assert.Equal(sent, Ally.Clamp(watts, onAc));
    }

    [Theory]
    [InlineData(55, true, 55)]
    [InlineData(56, true, 55)]
    [InlineData(55, false, 55)]
    [InlineData(6, false, 7)]
    public void The_xbox_ally_x_stops_at_55_watts_on_either_power_source(int watts, bool onAc, int sent)
    {
        Assert.Equal(sent, AllyX.Clamp(watts, onAc));
    }

    [Fact]
    public void An_unsupported_device_is_left_alone()
    {
        Assert.Equal(TdpAction.None, TdpPlanner.Plan(new ModeProfile { TdpWatts = 20 }, device: null, limitInForce: true));
    }

    [Theory]
    [InlineData(1, true, 2u)]     // Silent
    [InlineData(2, true, 0u)]     // Performance
    [InlineData(3, false, 1u)]    // Turbo
    [InlineData(4, true, 1u)]     // Manual: its own values are unknown, so the default for the power source
    [InlineData(0, false, 0u)]    // Windows
    [InlineData(null, true, 1u)]  // no Armoury Crate
    [InlineData(null, false, 0u)]
    public void Handing_back_re_sends_the_mode_armoury_crate_has_selected(int? armouryCrateMode, bool onAc, uint asusMode)
    {
        Assert.Equal(asusMode, TdpPlanner.AsusModeToRestore(armouryCrateMode, onAc));
    }

    [Fact]
    public void The_tdp_is_part_of_a_profiles_identity_and_its_json()
    {
        Assert.NotEqual(new ModeProfile { TdpWatts = 20 }, new ModeProfile());
        Assert.Equal(new ModeProfile { TdpWatts = 20 }, new ModeProfile { TdpWatts = 20 });

        var dir = Path.Combine(Path.GetTempPath(), "DockSenseTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"));
            store.Save(new DockSenseConfig { Egpu = new ModeProfile { TdpWatts = 45 } });

            Assert.Contains("\"tdpWatts\": 45", File.ReadAllText(store.FilePath));
            Assert.Equal(45, store.Load().Egpu.TdpWatts);
            Assert.Null(store.Load().Handheld.TdpWatts);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
