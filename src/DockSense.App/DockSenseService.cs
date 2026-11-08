using System.ComponentModel;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>
/// Ties detection, debouncing and profile application together. Everything runs on the UI thread: Windows
/// notifications only schedule a re-check, and timers do the work.
/// </summary>
internal sealed class DockSenseService : IDisposable
{
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(1500);

    private readonly ConfigStore _configStore;
    private readonly System.Windows.Forms.Timer _rescanTimer = new();
    private readonly System.Windows.Forms.Timer _settleTimer = new();
    private SystemNotifications? _notifications;
    private DockSenseConfig _config = new();
    private ModeStabilizer _stabilizer = new(DockMode.Handheld, TimeSpan.Zero);
    private PowerPlanController _planController = new();

    public DockSenseService(ConfigStore configStore)
    {
        _configStore = configStore;
        _rescanTimer.Interval = (int)CoalesceDelay.TotalMilliseconds;
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); Evaluate(); };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); Evaluate(); };
    }

    /// <summary>Raised whenever the mode, plan, pause state or snapshot changes (for the tray UI).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised for things the user should notice (e.g. DockSense gave up re-applying its plan).</summary>
    public event Action<string>? Notice;

    public DockMode Mode => _stabilizer.Current;

    public DeviceSnapshot? Snapshot { get; private set; }

    public bool Paused { get; private set; }

    public DockSenseConfig Config => _config;

    public void Start()
    {
        _config = _configStore.Load(Log.Warn);
        Snapshot = TakeSnapshot() ?? new DeviceSnapshot([], [], OnAcPower: true);
        var mode = ModeResolver.Resolve(Snapshot, _config.Detection);
        _stabilizer = new ModeStabilizer(mode, TimeSpan.FromSeconds(Math.Max(0, _config.Detection.SettleSeconds)));
        _planController = CreatePlanController(StateStore.Load());

        Log.Info($"Started. {Describe(Snapshot)} -> {mode}");
        ApplyProfile(mode, "startup");

        // Register only after the initial apply: Windows immediately reports the current plan, which is then ours.
        _notifications = new SystemNotifications();
        _notifications.HardwareChanged += ScheduleRescan;
        _notifications.ActivePlanChanged += OnActivePlanChanged;
    }

    public void ScheduleRescan(string reason)
    {
        Log.Info($"Re-check scheduled: {reason}");
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    public void SetPaused(bool paused)
    {
        if (Paused == paused)
        {
            return;
        }

        Paused = paused;
        Log.Info(paused ? "Automatic switching paused." : "Automatic switching resumed.");
        if (!paused)
        {
            Snapshot = TakeSnapshot() ?? Snapshot;
            ApplyProfile(_stabilizer.Current, "resumed");
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads config.json (after the user edited it) and re-applies the current mode.</summary>
    public void ReloadConfig()
    {
        _config = _configStore.Load(Log.Warn);
        _stabilizer = new ModeStabilizer(_stabilizer.Current, TimeSpan.FromSeconds(Math.Max(0, _config.Detection.SettleSeconds)));
        _planController = CreatePlanController(_planController.State);
        Log.Info("Config reloaded.");
        Evaluate(force: true);
    }

    /// <summary>Makes sure config.json exists on disk (with defaults) so the user can open and edit it.</summary>
    public string EnsureConfigFile()
    {
        if (!File.Exists(_configStore.FilePath))
        {
            _configStore.Save(_config);
        }

        return _configStore.FilePath;
    }

    public void Dispose()
    {
        _notifications?.Dispose();
        _rescanTimer.Dispose();
        _settleTimer.Dispose();
    }

    private PowerPlanController CreatePlanController(PowerPlanState? savedState)
    {
        // No saved state but our own plan is active (e.g. state file deleted): treat it as owned so that leaving
        // the eGPU mode still switches away from it.
        if (savedState is null && _config.DockSensePerformancePlanId is { } ownPlan && SafeGetActivePlan() == ownPlan)
        {
            savedState = new PowerPlanState(ownPlan, null);
        }

        return new PowerPlanController(savedState) { ReassertEnabled = _config.ReassertPowerPlan };
    }

    private void Evaluate(bool force = false)
    {
        Snapshot = TakeSnapshot() ?? Snapshot;
        if (Snapshot is null)
        {
            return;
        }

        var observed = ModeResolver.Resolve(Snapshot, _config.Detection);
        var now = DateTimeOffset.Now;
        if (_stabilizer.Observe(observed, now))
        {
            Log.Info($"Mode changed to {observed}. {Describe(Snapshot)}");
            ApplyProfile(observed, "mode change");
        }
        else if (force)
        {
            ApplyProfile(_stabilizer.Current, "config reload");
        }
        else if (_stabilizer.PendingDueAt is { } due)
        {
            Log.Info($"Waiting for {observed} to settle (until {due:HH:mm:ss}).");
            _settleTimer.Stop();
            _settleTimer.Interval = Math.Max(100, (int)Math.Ceiling((due - now).TotalMilliseconds) + 100);
            _settleTimer.Start();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyProfile(DockMode mode, string reason)
    {
        if (Paused)
        {
            Log.Info($"Paused: not applying the {mode} profile ({reason}).");
            return;
        }

        var profile = _config.ProfileFor(mode);
        ApplyPowerPlan(mode, profile, reason);
        ApplyFse(mode, profile);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPowerPlan(DockMode mode, ModeProfile profile, string reason)
    {
        try
        {
            var target = ResolvePlan(profile.PowerPlan);
            var active = PowerPlans.GetActive();
            var toActivate = _planController.OnModeApplied(target, active, PowerPlans.Balanced);
            if (toActivate is { } plan)
            {
                PowerPlans.SetActive(plan);
                Log.Info($"{mode} ({reason}): power plan {PowerPlans.Describe(active)} -> {PowerPlans.Describe(plan)}");
            }

            StateStore.Save(_planController.State);
        }
        catch (Win32Exception ex)
        {
            Log.Error($"Could not apply the {mode} power plan", ex);
        }
    }

    private Guid? ResolvePlan(PowerPlanChoice choice)
    {
        switch (choice.Kind)
        {
            case PowerPlanKind.Specific when choice.PlanId is { } id:
                if (PowerPlans.Exists(id))
                {
                    return id;
                }

                Log.Warn($"Configured power plan {{{id}}} does not exist; leaving the plan unmanaged.");
                return null;

            case PowerPlanKind.DockSensePerformance:
                if (_config.DockSensePerformancePlanId is { } existing && PowerPlans.Exists(existing))
                {
                    return existing;
                }

                var created = PowerPlans.CreateDockSensePerformancePlan();
                Log.Info($"Created power plan {PowerPlans.Describe(created)}.");
                _config = _config with { DockSensePerformancePlanId = created };
                _configStore.Save(_config);
                return created;

            default:
                return null;
        }
    }

    private static void ApplyFse(DockMode mode, ModeProfile profile)
    {
        if (profile.Fse == FseStartup.Unchanged || !FseSettings.IsAvailable)
        {
            return;
        }

        var wantFse = profile.Fse == FseStartup.FullScreenExperience;
        if (FseSettings.SetStartupToFse(wantFse))
        {
            Log.Info($"{mode}: Full Screen Experience at sign-in -> {(wantFse ? "on" : "off")}");
        }

        if (profile.FseApplyImmediately)
        {
            Log.Info($"{mode}: live FSE switching is not implemented yet; the setting applies at next sign-in.");
        }
    }

    private void OnActivePlanChanged(Guid newActive)
    {
        if (Paused)
        {
            return;
        }

        var reapply = _planController.OnActivePlanChanged(newActive, DateTimeOffset.Now);
        if (reapply is { } plan)
        {
            Log.Info($"Power plan was switched to {PowerPlans.Describe(newActive)} by another program; re-applying {PowerPlans.Describe(plan)}.");
            TrySetActive(plan);
        }
        else if (_planController.GaveUp)
        {
            var message = $"Another program keeps switching the power plan (now {PowerPlans.GetName(newActive)}). " +
                "DockSense stopped re-applying its plan until the next mode change.";
            Log.Warn(message);
            Notice?.Invoke(message);
        }

        StateStore.Save(_planController.State);
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void TrySetActive(Guid plan)
    {
        try
        {
            PowerPlans.SetActive(plan);
        }
        catch (Win32Exception ex)
        {
            Log.Error($"Could not activate power plan {{{plan}}}", ex);
        }
    }

    private static Guid? SafeGetActivePlan()
    {
        try
        {
            return PowerPlans.GetActive();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static DeviceSnapshot? TakeSnapshot()
    {
        try
        {
            return new DeviceSnapshot(
                GpuProbe.GetWorkingAdapters(),
                DisplayProbe.GetActiveDisplays(),
                SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not read the hardware state", ex);
            return null;
        }
    }

    private static string Describe(DeviceSnapshot snapshot)
    {
        var gpus = string.Join(", ", snapshot.Gpus.Select(g => g.IsExternal ? $"{g.Name} (external)" : g.Name));
        var displays = string.Join(", ", snapshot.Displays.Select(d =>
            $"{(string.IsNullOrEmpty(d.Name) ? "display" : d.Name)} ({(d.IsInternal ? "internal" : "external")})"));
        return $"GPUs: [{gpus}]; displays: [{displays}]; AC: {snapshot.OnAcPower}";
    }
}
