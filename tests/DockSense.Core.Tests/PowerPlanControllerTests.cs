using DockSense.Core;

namespace DockSense.Core.Tests;

public class PowerPlanControllerTests
{
    private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid ArmouryTurbo = new("6fecc5ae-f350-48a5-b669-b472cb895ccf");
    private static readonly Guid ArmouryPerformance = new("27fa6203-3987-4dcc-918d-748559d549ec");
    private static readonly Guid DockSensePlan = new("90b0541f-4daa-4804-82aa-02c1f564daf4");
    private static readonly Guid OtherPlan = new("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entering_managed_mode_activates_target_and_remembers_previous_plan()
    {
        var controller = new PowerPlanController();

        Assert.Equal(DockSensePlan, controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced));
        Assert.Equal(new PowerPlanState(DockSensePlan, ArmouryTurbo), controller.State);
    }

    [Fact]
    public void Leaving_managed_mode_restores_previous_plan()
    {
        var controller = new PowerPlanController();
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        Assert.Equal(ArmouryTurbo, controller.OnModeApplied(null, DockSensePlan, Balanced));
        Assert.Equal(new PowerPlanState(null, null), controller.State);
    }

    [Fact]
    public void Already_on_target_needs_no_switch_and_restores_fallback_later()
    {
        var controller = new PowerPlanController();

        Assert.Null(controller.OnModeApplied(DockSensePlan, DockSensePlan, Balanced));
        Assert.Equal(Balanced, controller.OnModeApplied(null, DockSensePlan, Balanced));
    }

    [Fact]
    public void Leaving_does_not_override_a_plan_someone_else_already_chose()
    {
        var controller = new PowerPlanController(new PowerPlanState(DockSensePlan, ArmouryTurbo));

        Assert.Null(controller.OnModeApplied(null, OtherPlan, Balanced));
        Assert.Equal(new PowerPlanState(null, null), controller.State);
    }

    [Fact]
    public void Unmanaged_to_unmanaged_does_nothing()
    {
        var controller = new PowerPlanController();

        Assert.Null(controller.OnModeApplied(null, ArmouryTurbo, Balanced));
    }

    [Fact]
    public void Switching_between_managed_modes_keeps_the_original_restore_target()
    {
        var controller = new PowerPlanController();
        controller.OnModeApplied(OtherPlan, ArmouryTurbo, Balanced);           // e.g. Docked -> a specific plan

        Assert.Equal(DockSensePlan, controller.OnModeApplied(DockSensePlan, OtherPlan, Balanced)); // -> eGPU
        Assert.Equal(ArmouryTurbo, controller.OnModeApplied(null, DockSensePlan, Balanced));       // -> Handheld
    }

    [Fact]
    public void Armoury_crate_unplug_sequence_ends_on_the_plan_armoury_chose()
    {
        // Measured: unplugging the dock drops AC first, Armoury Crate switches to its battery plan (Performance),
        // and the eGPU disappears ~10 s later.
        var controller = new PowerPlanController();
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        Assert.Equal(DockSensePlan, controller.OnActivePlanChanged(ArmouryPerformance, T0));   // still eGPU: take it back
        Assert.Null(controller.OnActivePlanChanged(DockSensePlan, T0.AddSeconds(1)));          // our own switch
        Assert.Equal(ArmouryPerformance, controller.OnModeApplied(null, DockSensePlan, Balanced)); // eGPU gone
    }

    [Fact]
    public void Gives_up_after_too_many_reverts_in_the_window()
    {
        var controller = new PowerPlanController { MaxReasserts = 3, ReassertWindow = TimeSpan.FromMinutes(2) };
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(DockSensePlan, controller.OnActivePlanChanged(ArmouryTurbo, T0.AddSeconds(i * 10)));
        }

        Assert.Null(controller.OnActivePlanChanged(ArmouryTurbo, T0.AddSeconds(30)));
        Assert.True(controller.GaveUp);
        Assert.Equal(new PowerPlanState(null, null), controller.State);
    }

    [Fact]
    public void Reverts_outside_the_window_do_not_count()
    {
        var controller = new PowerPlanController { MaxReasserts = 2, ReassertWindow = TimeSpan.FromMinutes(2) };
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        Assert.Equal(DockSensePlan, controller.OnActivePlanChanged(ArmouryTurbo, T0));
        Assert.Equal(DockSensePlan, controller.OnActivePlanChanged(ArmouryTurbo, T0.AddMinutes(1)));
        Assert.Equal(DockSensePlan, controller.OnActivePlanChanged(ArmouryTurbo, T0.AddMinutes(3)));
        Assert.False(controller.GaveUp);
    }

    [Fact]
    public void Next_mode_change_resets_give_up()
    {
        var controller = new PowerPlanController { MaxReasserts = 1 };
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);
        controller.OnActivePlanChanged(ArmouryTurbo, T0);
        controller.OnActivePlanChanged(ArmouryTurbo, T0.AddSeconds(1));
        Assert.True(controller.GaveUp);

        Assert.Equal(DockSensePlan, controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced));
        Assert.False(controller.GaveUp);
    }

    [Fact]
    public void With_reassert_disabled_an_external_change_ends_management()
    {
        var controller = new PowerPlanController { ReassertEnabled = false };
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        Assert.Null(controller.OnActivePlanChanged(OtherPlan, T0));
        Assert.Null(controller.OnModeApplied(null, OtherPlan, Balanced));
    }

    [Fact]
    public void Notification_for_our_own_plan_is_ignored()
    {
        var controller = new PowerPlanController();
        controller.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        Assert.Null(controller.OnActivePlanChanged(DockSensePlan, T0));
        Assert.Equal(new PowerPlanState(DockSensePlan, ArmouryTurbo), controller.State);
    }

    [Fact]
    public void Restored_state_survives_an_app_restart()
    {
        var before = new PowerPlanController();
        before.OnModeApplied(DockSensePlan, ArmouryTurbo, Balanced);

        var after = new PowerPlanController(before.State);

        Assert.Equal(ArmouryTurbo, after.OnModeApplied(null, DockSensePlan, Balanced));
    }
}
