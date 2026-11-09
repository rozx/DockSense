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

/// <summary>Raised after DockSense started a program configured for a mode.</summary>
/// <param name="Name">Short display name, e.g. "steam.exe".</param>
/// <param name="Mode">The mode being entered, or for <see cref="ProgramTrigger.Leave"/> programs the mode being left.</param>
/// <param name="Trigger">Why it ran.</param>
/// <param name="AtStartup">True when it ran because DockSense started in <paramref name="Mode"/>.</param>
public sealed record ProgramStarted(string Name, DockMode Mode, ProgramTrigger Trigger, bool AtStartup);

/// <summary>Programs to start for one profile application, with the transition that caused it.</summary>
internal sealed record ProgramBatch(DockMode? Previous, DockMode Current, bool AtStartup, IReadOnlyList<ProgramLaunch> Programs);

/// <summary>Why a mode's profile is being applied.</summary>
internal enum ApplyReason
{
    Startup,
    ModeChange,
    Resumed,
    SettingsChanged,
}

/// <summary>
/// Ties detection, debouncing and profile application together. Everything runs on the UI thread: Windows
/// notifications only schedule a re-check, and dispatcher timers do the work.
/// </summary>
public sealed class DockSenseService : IDisposable
{
    private const int MaxActivity = 50;
    private static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>At sign-in the shell may still be starting FSE; wait before checking and switching it.</summary>
    private static readonly TimeSpan StartupFseDelay = TimeSpan.FromSeconds(10);

    private readonly ConfigStore _configStore;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _rescanTimer = new() { Interval = CoalesceDelay };
    private readonly DispatcherTimer _settleTimer = new();
    private SystemNotifications? _notifications;
    private ModeStabilizer _stabilizer = new(DockMode.Handheld, TimeSpan.Zero);
    private PowerPlanController _planController = new();
    private DockMode? _lastApplied;
    private int _fseRequest;
    private bool _fseSwitching;
    private (DockMode? Previous, DockMode Current) _fseTransition;
    private readonly Queue<ProgramBatch> _programBatches = new();
    private bool _programsRunning;
    private IgpuMemoryBootState? _igpuBoot;

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

    /// <summary>Raised after a program configured for a mode was started (not for the Test button).</summary>
    public event Action<ProgramStarted>? ProgramStarted;

    /// <summary>Raised after a new iGPU memory size (in MB, 0 = Auto) was stored; it applies after a restart.</summary>
    public event Action<int>? IgpuMemoryChanged;

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public DockMode Mode => _stabilizer.Current;

    public DeviceSnapshot? Snapshot { get; private set; }

    public bool Paused { get; private set; }

    public DockSenseConfig Config { get; private set; } = new();

    public GameModeReport? GameMode { get; private set; }

    /// <summary>The firmware's iGPU memory options, or null when the device does not offer the setting.</summary>
    public IgpuMemoryOptions? IgpuMemory { get; private set; }

    /// <summary>iGPU memory in effect during this Windows session, in MB (0 = Auto); null if unknown.</summary>
    public int? IgpuMemoryEffectiveMb => IgpuMemory is { } options && _igpuBoot is { } boot ? options.ToMb(boot.EffectiveRaw) : null;

    /// <summary>iGPU memory that applies after the next restart, in MB, or null when nothing is pending.</summary>
    public int? IgpuMemoryPendingMb => IgpuMemory is { } options && _igpuBoot is { } boot && options.CurrentRaw != boot.EffectiveRaw
        ? options.ToMb(options.CurrentRaw)
        : null;

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

        InitializeIgpuMemory();
        Snapshot = TakeSnapshot() ?? new DeviceSnapshot([], [], OnAcPower: true);
        var mode = ModeResolver.Resolve(Snapshot, Config.Detection);
        _stabilizer = new ModeStabilizer(mode, SettleTime);
        _planController = CreatePlanController(StateStore.Load());

        Log.Info($"Started. {Describe(Snapshot)} -> {mode}");
        AddActivity(Loc.F("Act_Started", ModeName(mode)));
        ApplyProfile(mode, ApplyReason.Startup);

        // Register only after the initial apply: Windows immediately reports the current plan, which is then ours.
        _notifications = new SystemNotifications();
        _notifications.HardwareChanged += ScheduleRescan;
        _notifications.ActivePlanChanged += OnActivePlanChanged;
        FullScreenExperience.Subscribe(() => _dispatcher.BeginInvoke(RaiseStatusChanged));
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
            ApplyProfile(_stabilizer.Current, ApplyReason.Resumed);
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

    // ------------------------------------------------------------------ Power plan editor

    /// <summary>Creates a plan as a copy of <paramref name="source"/>. Throws <see cref="Win32Exception"/> on failure.</summary>
    public Guid CreatePlan(string name, Guid source)
    {
        var plan = PowerPlans.Create(name, source);
        Log.Info($"Created power plan {PowerPlans.Describe(plan)} from {PowerPlans.Describe(source)}.");
        AddActivity(Loc.F("Act_PlanCreated", name));
        RaiseStatusChanged();
        return plan;
    }

    /// <summary>Renames a plan and writes changed setting values. Throws <see cref="Win32Exception"/> on failure.</summary>
    public void SavePlan(Guid plan, string? newName, IReadOnlyList<(PowerSettingDefinition Setting, bool Ac, uint Value)> values)
    {
        if (newName is not null)
        {
            PowerPlans.Rename(plan, newName);
        }

        foreach (var (setting, ac, value) in values)
        {
            PowerPlans.WriteValue(plan, setting.Subgroup, setting.Setting, ac, value);
            foreach (var twin in setting.Twins)
            {
                // The efficiency-class copy may not exist on CPUs without hybrid cores; that is fine.
                if (PowerPlans.ReadValue(plan, setting.Subgroup, twin, ac) is not null)
                {
                    PowerPlans.WriteValue(plan, setting.Subgroup, twin, ac, value);
                }
            }
        }

        PowerPlans.ReapplyIfActive(plan);
        Log.Info($"Saved power plan {PowerPlans.Describe(plan)}: {values.Count} value(s) changed{(newName is null ? string.Empty : ", renamed")}.");
        AddActivity(Loc.F("Act_PlanSaved", PowerPlans.GetName(plan) ?? newName ?? plan.ToString()));
        RaiseStatusChanged();
    }

    /// <summary>
    /// Deletes a plan. Mode profiles that used it fall back to "don't change". Throws <see cref="Win32Exception"/>
    /// when Windows refuses (e.g. the plan is active).
    /// </summary>
    public void DeletePlan(Guid plan)
    {
        var name = PowerPlans.GetName(plan) ?? plan.ToString();
        PowerPlans.Delete(plan);
        Log.Info($"Deleted power plan {name} {{{plan}}}.");
        AddActivity(Loc.F("Act_PlanDeleted", name));

        // Both explicit choices of this plan and the "DockSense Performance" choice when this was that plan;
        // otherwise the next apply would silently create the plan again.
        static ModeProfile Release(ModeProfile profile, Guid plan, bool wasDockSensePlan) => profile.PowerPlan switch
        {
            { Kind: PowerPlanKind.Specific, PlanId: { } id } when id == plan => profile with { PowerPlan = PowerPlanChoice.Unmanaged },
            { Kind: PowerPlanKind.DockSensePerformance } when wasDockSensePlan => profile with { PowerPlan = PowerPlanChoice.Unmanaged },
            _ => profile,
        };

        UpdateConfig(c =>
        {
            var wasDockSensePlan = c.DockSensePerformancePlanId == plan;
            return c with
            {
                Handheld = Release(c.Handheld, plan, wasDockSensePlan),
                Docked = Release(c.Docked, plan, wasDockSensePlan),
                Egpu = Release(c.Egpu, plan, wasDockSensePlan),
                DockSensePerformancePlanId = wasDockSensePlan ? null : c.DockSensePerformancePlanId,
            };
        }, reapply: false);
        RaiseStatusChanged();
    }

    /// <summary>Starts a configured program right away (the "Test" button). Returns null or an error message.</summary>
    public static Task<string?> TestProgramAsync(ProgramLaunch program) => Task.Run(() => ProgramLauncher.Start(program));

    public void Dispose()
    {
        FullScreenExperience.Unsubscribe();
        _notifications?.Dispose();
        _rescanTimer.Stop();
        _settleTimer.Stop();
    }

    public static string ModeName(DockMode mode) => Loc.T($"Mode_{mode}");

    /// <summary>"8 GB", "512 MB" or "Auto".</summary>
    public static string FormatMemory(int megabytes) => megabytes switch
    {
        0 => Loc.T("Igpu_Auto"),
        < 1024 => Loc.F("Igpu_Mb", megabytes),
        _ => Loc.F("Igpu_Gb", megabytes / 1024.0),
    };

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
            ApplyProfile(observed, ApplyReason.ModeChange);
        }
        else if (force)
        {
            ApplyProfile(_stabilizer.Current, ApplyReason.SettingsChanged);
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

    private void ApplyProfile(DockMode mode, ApplyReason reason)
    {
        if (Paused)
        {
            Log.Info($"Paused: not applying the {mode} profile ({reason}).");
            return;
        }

        var profile = Config.ProfileFor(mode);
        ApplyPowerPlan(mode, profile, reason);
        ApplyFse(mode, profile, reason);
        ApplyIgpuMemory(mode, profile);
        StartPrograms(mode, reason);
        _lastApplied = mode;

        if (reason != ApplyReason.SettingsChanged && Snapshot is not null)
        {
            ModeApplied?.Invoke(new ModeApplied(mode, Snapshot, SafeGetActivePlanName(), reason == ApplyReason.Startup));
        }

        RaiseStatusChanged();
    }

    private void ApplyPowerPlan(DockMode mode, ModeProfile profile, ApplyReason reason)
    {
        try
        {
            var target = ResolvePlan(profile.PowerPlan);
            var active = PowerPlans.GetActive();
            var toActivate = _planController.OnModeApplied(target, active, PowerPlans.Balanced);
            if (toActivate is { } restore && !PowerPlans.Exists(restore))
            {
                // The plan to go back to was deleted (e.g. on the Power plans page) while this mode was active.
                Log.Warn($"The plan to restore {{{restore}}} no longer exists; using Balanced instead.");
                toActivate = active == PowerPlans.Balanced ? null : PowerPlans.Balanced;
            }

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

    // ------------------------------------------------------------------ Full screen experience

    private void ApplyFse(DockMode mode, ModeProfile profile, ApplyReason reason)
    {
        if (profile.Fse != FseStartup.Unchanged)
        {
            var wantFse = profile.Fse == FseStartup.FullScreenExperience;
            if (FseSettings.IsAvailable && FseSettings.SetStartupToFse(wantFse))
            {
                Log.Info($"{mode}: Full Screen Experience at sign-in -> {(wantFse ? "on" : "off")}");
                AddActivity(Loc.F("Act_Fse", Loc.T(wantFse ? "Common_On" : "Common_Off")));
            }
        }

        // Switching live while the user edits settings would yank the screen away; only real transitions do it.
        // The new mode's live setting decides first, then "exit when leaving" of the mode being left.
        if (reason != ApplyReason.SettingsChanged && FsePlanner.LiveTarget(Config, _lastApplied, mode) is not null)
        {
            _fseTransition = (_lastApplied, mode);
            _ = SwitchFseAsync(reason == ApplyReason.Startup ? StartupFseDelay : TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Brings the full screen experience in line with the latest mode transition. One switch runs at a time:
    /// <see cref="FullScreenExperience.Set"/> can wait on Windows' confirmation dialog, and if the mode changes
    /// meanwhile, the running switch reconciles with the newest transition when it returns.
    /// </summary>
    private async Task SwitchFseAsync(TimeSpan delay)
    {
        var request = ++_fseRequest;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay);
        }

        if (request != _fseRequest || _fseSwitching)
        {
            return; // A newer request takes over, or the running switch will pick this one up.
        }

        _fseSwitching = true;
        try
        {
            while (true)
            {
                var handled = _fseRequest;
                await SwitchFseOnceAsync();
                if (_fseRequest == handled)
                {
                    break; // Nothing changed while switching.
                }
            }
        }
        finally
        {
            _fseSwitching = false;
            RaiseStatusChanged();
        }
    }

    private async Task SwitchFseOnceAsync()
    {
        // Always the latest transition, with the profiles as they are now, so a switch that waited on a prompt
        // cannot leave a stale state.
        var (previous, mode) = _fseTransition;
        if (Paused || Mode != mode || FsePlanner.LiveTarget(Config, previous, mode) is not { } wantFse)
        {
            return;
        }

        if (!FullScreenExperience.CanSet)
        {
            Log.Info($"{mode}: cannot switch the full screen experience live on this device (not supported, or no home app set).");
            return;
        }

        if (FullScreenExperience.IsActive == wantFse)
        {
            return;
        }

        if (wantFse && FseSettings.RestartsOnEnter)
        {
            Log.Warn("Windows is set to restart the PC to enter the full screen experience; not switching live.");
            NotifyWarning(Loc.T("Act_FseRestartBlocked"));
            return;
        }

        Log.Info($"{mode}: {(wantFse ? "entering" : "leaving")} the full screen experience.");
        var result = await Task.Run(() => FullScreenExperience.Set(wantFse));
        if (result >= 0)
        {
            AddActivity(Loc.T(wantFse ? "Act_FseEntered" : "Act_FseLeft"));
        }
        else if (result == FullScreenExperience.E_ABORT)
        {
            Log.Info("The full screen experience switch was declined at the Windows prompt.");
            AddActivity(Loc.T("Act_FseDeclined"));
        }
        else
        {
            Log.Warn($"Switching the full screen experience failed: HRESULT 0x{result:X8}.");
            NotifyWarning(Loc.F("Act_FseFailed", $"0x{result:X8}"));
        }
    }

    // ------------------------------------------------------------------ iGPU memory

    private void InitializeIgpuMemory()
    {
        IgpuMemory = SafeReadIgpuMemory();
        if (IgpuMemory is not { } options)
        {
            return;
        }

        // The value stored in the firmware when this boot session is first seen is the one in effect now.
        var boot = CurrentBoot();
        var saved = StateStore.LoadIgpuMemory();
        if (saved?.Boot is { } savedBoot && savedBoot.IsSameBoot(boot))
        {
            _igpuBoot = saved;
        }
        else
        {
            _igpuBoot = new IgpuMemoryBootState(boot, options.CurrentRaw);
            StateStore.SaveIgpuMemory(_igpuBoot);
        }

        Log.Info($"iGPU memory: {options.ToMb(_igpuBoot.EffectiveRaw)} MB in effect, {options.ToMb(options.CurrentRaw)} MB stored in the firmware.");
    }

    private void ApplyIgpuMemory(DockMode mode, ModeProfile profile)
    {
        if (profile.IgpuMemoryMb is not { } target || IgpuMemory is null)
        {
            return;
        }

        // Re-read: Armoury Crate or another tool may have changed it since.
        IgpuMemory = SafeReadIgpuMemory() ?? IgpuMemory;
        if (IgpuMemory.RawToWrite(target) is not { } raw)
        {
            if (IgpuMemory.RawFor(target) is null)
            {
                Log.Warn($"{mode}: the firmware does not offer {target} MB of iGPU memory; not changing it.");
            }

            return;
        }

        try
        {
            if (!AsusAcpi.WriteIgpuMemory(raw))
            {
                Log.Warn($"{mode}: the firmware rejected iGPU memory {target} MB.");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{mode}: could not set iGPU memory", ex);
            return;
        }

        IgpuMemory = SafeReadIgpuMemory() ?? IgpuMemory with { CurrentRaw = raw };
        Log.Info($"{mode}: iGPU memory set to {target} MB; it applies after a restart.");
        AddActivity(Loc.F("Act_IgpuMemory", FormatMemory(target)));
        if (IgpuMemoryPendingMb is not null)
        {
            IgpuMemoryChanged?.Invoke(target);
        }
    }

    /// <summary>This boot: Windows' boot counter (readable without admin) plus the start time as a fallback.</summary>
    private static BootSession CurrentBoot()
    {
        uint? bootId = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
            bootId = key?.GetValue("BootId") is int id ? unchecked((uint)id) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Warn($"Could not read the boot counter: {ex.Message}");
        }

        return new BootSession(bootId, DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static IgpuMemoryOptions? SafeReadIgpuMemory()
    {
        try
        {
            return AsusAcpi.ReadIgpuMemory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read the iGPU memory setting: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ Programs

    private void StartPrograms(DockMode mode, ApplyReason reason)
    {
        if (reason == ApplyReason.SettingsChanged)
        {
            return;
        }

        var atStartup = reason == ApplyReason.Startup;
        var programs = ProgramPlanner.Select(Config, _lastApplied, mode, atStartup);
        if (programs.Count > 0)
        {
            _programBatches.Enqueue(new ProgramBatch(_lastApplied, mode, atStartup, programs));
            if (!_programsRunning)
            {
                _ = RunProgramBatchesAsync();
            }
        }
    }

    /// <summary>
    /// Runs queued batches one after another, each in order, off the UI thread (an elevated launch waits for the
    /// UAC prompt). One runner for all transitions, so a later batch's Leave programs never overtake the rest of
    /// an earlier batch that is still waiting on a prompt.
    /// </summary>
    private async Task RunProgramBatchesAsync()
    {
        _programsRunning = true;
        try
        {
            while (_programBatches.TryDequeue(out var batch))
            {
                foreach (var program in batch.Programs)
                {
                    var name = ProgramLauncher.DisplayName(program);
                    var error = await Task.Run(() => ProgramLauncher.Start(program));
                    if (error is null)
                    {
                        Log.Info($"Started {program.Path} {program.Arguments}".TrimEnd());
                        AddActivity(Loc.F("Act_ProgramStarted", name));
                        var mode = program.When == ProgramTrigger.Leave && batch.Previous is { } left ? left : batch.Current;
                        ProgramStarted?.Invoke(new ProgramStarted(name, mode, program.When, batch.AtStartup));
                    }
                    else
                    {
                        Log.Warn($"Could not start {program.Path}: {error}");
                        NotifyWarning(Loc.F("Act_ProgramFailed", name, error));
                    }
                }
            }
        }
        finally
        {
            _programsRunning = false;
            RaiseStatusChanged();
        }
    }

    // ------------------------------------------------------------------ Notifications from Windows

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
            NotifyWarning(Loc.T("Act_GaveUp"));
        }

        StateStore.Save(_planController.State);
        RaiseStatusChanged();
    }

    private void NotifyWarning(string message)
    {
        AddActivity(message);
        Warning?.Invoke(message);
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
