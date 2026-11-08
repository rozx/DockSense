using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App;

/// <summary>One line in the dashboard's "Recent activity" list.</summary>
public sealed record ActivityEntry(DateTime Time, string Message);

/// <summary>Raised after a mode's profile has been applied.</summary>
public sealed record ModeApplied(DockMode Mode, DeviceSnapshot Snapshot, string? PlanName, bool IsStartup);

/// <summary>
/// Ties detection, debouncing and profile application together. Everything runs on the UI thread: Windows
/// notifications only schedule a re-check, and dispatcher timers do the work.
/// </summary>
public sealed class DockSenseService : IDisposable
{
    private const int MaxActivity = 50;
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(1500);

    private readonly ConfigStore _configStore;
    private readonly DispatcherTimer _rescanTimer = new() { Interval = CoalesceDelay };
    private readonly DispatcherTimer _settleTimer = new();
    private SystemNotifications? _notifications;
    private ModeStabilizer _stabilizer = new(DockMode.Handheld, TimeSpan.Zero);
    private PowerPlanController _planController = new();

    public DockSenseService(ConfigStore configStore)
    {
        _configStore = configStore;
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); Evaluate(); };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); Evaluate(); };
    }

    /// <summary>Raised whenever anything shown in the UI may have changed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised after a mode profile was applied (startup and every mode change).</summary>
    public event Action<ModeApplied>? ModeApplied;

    /// <summary>Raised for problems the user should know about.</summary>
    public event Action<string>? Warning;

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public DockMode Mode => _stabilizer.Current;

    public DeviceSnapshot? Snapshot { get; private set; }

    public bool Paused { get; private set; }

    public DockSenseConfig Config { get; private set; } = new();

    public GameModeReport? GameMode { get; private set; }

    /// <summary>True when config.json did not exist at startup (first run).</summary>
    public bool IsFirstRun { get; private set; }

    public string ConfigPath => _configStore.FilePath;

    public void Start()
    {
        IsFirstRun = !File.Exists(_configStore.FilePath);
        Config = _configStore.Load(Log.Warn);
        if (IsFirstRun)
        {
            _configStore.Save(Config);
        }

        Snapshot = TakeSnapshot() ?? new DeviceSnapshot([], [], OnAcPower: true);
        var mode = ModeResolver.Resolve(Snapshot, Config.Detection);
        _stabilizer = new ModeStabilizer(mode, SettleTime);
        _planController = CreatePlanController(StateStore.Load());

        Log.Info($"Started. {Describe(Snapshot)} -> {mode}");
        AddActivity(Loc.F("Act_Started", ModeName(mode)));
        ApplyProfile(mode, "startup", isStartup: true);

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
        AddActivity(Loc.T(paused ? "Act_Paused" : "Act_Resumed"));
        if (!paused)
        {
            Snapshot = TakeSnapshot() ?? Snapshot;
            ApplyProfile(_stabilizer.Current, "resumed", isStartup: false);
        }

        RaiseStatusChanged();
    }

    /// <summary>Changes and saves the config. When <paramref name="reapply"/> is set, the current mode is re-applied.</summary>
    public void UpdateConfig(Func<DockSenseConfig, DockSenseConfig> change, bool reapply)
    {
        var before = Config;
        Config = change(Config);
        if (Config == before)
        {
            return;
        }

        _configStore.Save(Config);
        if (Config.Detection.SettleSeconds != before.Detection.SettleSeconds)
        {
            _stabilizer = new ModeStabilizer(_stabilizer.Current, SettleTime);
        }

        if (Config.ReassertPowerPlan != before.ReassertPowerPlan)
        {
            _planController = CreatePlanController(_planController.State);
        }

        Log.Info("Settings saved.");
        if (reapply)
        {
            Evaluate(force: true);
        }
        else
        {
            RaiseStatusChanged();
        }
    }

    public void Dispose()
    {
        _notifications?.Dispose();
        _rescanTimer.Stop();
        _settleTimer.Stop();
    }

    public static string ModeName(DockMode mode) => Loc.T($"Mode_{mode}");

    private TimeSpan SettleTime => TimeSpan.FromSeconds(Math.Clamp(Config.Detection.SettleSeconds, 0, 120));

    private PowerPlanController CreatePlanController(PowerPlanState? savedState)
    {
        // No saved state but our own plan is active (e.g. state file deleted): treat it as owned so that leaving
        // the eGPU mode still switches away from it.
        if (savedState is null && Config.DockSensePerformancePlanId is { } ownPlan && SafeGetActivePlan() == ownPlan)
        {
            savedState = new PowerPlanState(ownPlan, null);
        }

        return new PowerPlanController(savedState) { ReassertEnabled = Config.ReassertPowerPlan };
    }

    private void Evaluate(bool force = false)
    {
        Snapshot = TakeSnapshot() ?? Snapshot;
        if (Snapshot is null)
        {
            return;
        }

        var observed = ModeResolver.Resolve(Snapshot, Config.Detection);
        var now = DateTimeOffset.Now;
        if (_stabilizer.Observe(observed, now))
        {
            Log.Info($"Mode changed to {observed}. {Describe(Snapshot)}");
            AddActivity(Loc.F("Act_ModeChanged", ModeName(observed)));
            ApplyProfile(observed, "mode change", isStartup: false);
        }
        else if (force)
        {
            ApplyProfile(_stabilizer.Current, "settings changed", isStartup: false, announce: false);
        }
        else if (_stabilizer.PendingDueAt is { } due)
        {
            Log.Info($"Waiting for {observed} to settle (until {due:HH:mm:ss}).");
            _settleTimer.Stop();
            _settleTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, (due - now).TotalMilliseconds + 100));
            _settleTimer.Start();
        }

        RaiseStatusChanged();
    }

    private void ApplyProfile(DockMode mode, string reason, bool isStartup, bool announce = true)
    {
        if (Paused)
        {
            Log.Info($"Paused: not applying the {mode} profile ({reason}).");
            return;
        }

        var profile = Config.ProfileFor(mode);
        ApplyPowerPlan(mode, profile, reason);
        ApplyFse(mode, profile);

        if (announce && Snapshot is not null)
        {
            ModeApplied?.Invoke(new ModeApplied(mode, Snapshot, SafeGetActivePlanName(), isStartup));
        }

        RaiseStatusChanged();
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
                AddActivity(Loc.F("Act_PlanApplied", PowerPlans.GetName(plan)));
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
                return EnsureDockSensePlan();

            default:
                return null;
        }
    }

    /// <summary>Returns DockSense's own High Performance-based plan, creating it if needed.</summary>
    public Guid EnsureDockSensePlan()
    {
        if (Config.DockSensePerformancePlanId is { } existing && PowerPlans.Exists(existing))
        {
            return existing;
        }

        var created = PowerPlans.CreateDockSensePerformancePlan();
        Log.Info($"Created power plan {PowerPlans.Describe(created)}.");
        AddActivity(Loc.F("Act_PlanCreated", PowerPlans.DockSensePlanName));
        Config = Config with { DockSensePerformancePlanId = created };
        _configStore.Save(Config);
        return created;
    }

    private void ApplyFse(DockMode mode, ModeProfile profile)
    {
        if (profile.Fse == FseStartup.Unchanged || !FseSettings.IsAvailable)
        {
            return;
        }

        var wantFse = profile.Fse == FseStartup.FullScreenExperience;
        if (FseSettings.SetStartupToFse(wantFse))
        {
            Log.Info($"{mode}: Full Screen Experience at sign-in -> {(wantFse ? "on" : "off")}");
            AddActivity(Loc.F("Act_Fse", Loc.T(wantFse ? "Common_On" : "Common_Off")));
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
            RaiseStatusChanged();
            return;
        }

        var reapply = _planController.OnActivePlanChanged(newActive, DateTimeOffset.Now);
        if (reapply is { } plan)
        {
            Log.Info($"Power plan was switched to {PowerPlans.Describe(newActive)} by another program; re-applying {PowerPlans.Describe(plan)}.");
            AddActivity(Loc.F("Act_PlanReasserted", PowerPlans.GetName(newActive), PowerPlans.GetName(plan)));
            TrySetActive(plan);
        }
        else if (_planController.GaveUp)
        {
            Log.Warn($"Another program keeps switching the power plan (now {PowerPlans.Describe(newActive)}); giving up until the next mode change.");
            var message = Loc.T("Act_GaveUp");
            AddActivity(message);
            Warning?.Invoke(message);
        }

        StateStore.Save(_planController.State);
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        GameMode = SafeReadGameMode();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddActivity(string message)
    {
        Activity.Insert(0, new ActivityEntry(DateTime.Now, message));
        while (Activity.Count > MaxActivity)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }
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

    public static string? SafeGetActivePlanName() => SafeGetActivePlan() is { } plan ? PowerPlans.GetName(plan) : null;

    private static GameModeReport? SafeReadGameMode()
    {
        try
        {
            return GameModeDiagnostics.Read();
        }
        catch (Exception ex) when (ex is Win32Exception or System.Security.SecurityException or UnauthorizedAccessException)
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
                SystemParameters.PowerLineStatus == PowerLineStatus.Online);
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
