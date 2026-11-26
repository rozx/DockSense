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

/// <summary>An iGPU memory size a mode's profile asks for, waiting for the user's go-ahead.</summary>
/// <param name="Mode">The mode whose profile asks for it.</param>
/// <param name="MemoryMb">The size in MB (0 = Auto).</param>
public sealed record IgpuMemoryOffer(DockMode Mode, int MemoryMb);

/// <summary>Programs to start for one profile application, with the transition that caused it.</summary>
/// <param name="Wait">How long to hold the batch back before its first program (see <see cref="SignInSession.ShellWait"/>).</param>
internal sealed record ProgramBatch(DockMode? Previous, DockMode Current, bool AtStartup, IReadOnlyList<ProgramLaunch> Programs, TimeSpan Wait);

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

    // After a restart the user asked for to get a mode's iGPU memory size: how long that mode may take to show up
    // before the mode the PC came back in gets its own size again. Long enough to connect an eGPU after signing in.
    private static readonly TimeSpan AwaitedModeWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AmdDriverRetry = TimeSpan.FromSeconds(30);

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
    private DockMode _fseMode;
    private readonly Queue<ProgramBatch> _programBatches = new();
    private bool _programsRunning;
    private IgpuMemoryBootState? _igpuBoot;
    private readonly DispatcherTimer _igpuWaitTimer = new() { Interval = AwaitedModeWait };
    private DockMode? _igpuAwaitedMode;
    private IgpuMemoryOffer? _igpuAsked;
    private bool _amdDriverReadPending;
    private bool _amdStoreUnanswered;
    private bool? _fseBeforeUnansweredStore;
    private bool _resumedInSameMode;
    private bool _disposed;

    public DockSenseService(ConfigStore configStore)
    {
        _configStore = configStore;
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); Evaluate(); };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); Evaluate(); };
        _igpuWaitTimer.Tick += (_, _) => OnAwaitedModeWaitOver();
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

    /// <summary>
    /// Raised when the mode's profile asks for an iGPU memory size but is set to ask first: nothing is stored until
    /// <see cref="AcceptIgpuMemory"/>. The offer stays open as <see cref="IgpuMemoryOffer"/>.
    /// </summary>
    public event Action<IgpuMemoryOffer>? IgpuMemoryAsked;

    public ObservableCollection<ActivityEntry> Activity { get; } = [];

    public DockMode Mode => _stabilizer.Current;

    /// <summary>
    /// The mode whose profile was applied last, or null if none was yet. While paused this can lag behind
    /// <see cref="Mode"/>: the mode keeps being detected, but its profile waits until switching is resumed.
    /// </summary>
    public DockMode? LastAppliedMode => _lastApplied;

    public DeviceSnapshot? Snapshot { get; private set; }

    public bool Paused { get; private set; }

    public DockSenseConfig Config { get; private set; } = new();

    public GameModeReport? GameMode { get; private set; }

    /// <summary>The firmware's iGPU memory options, or null when the device does not offer the setting.</summary>
    public IgpuMemoryOptions? IgpuMemory { get; private set; }

    /// <summary>
    /// True when the iGPU memory is set through the AMD graphics driver (Variable Graphics Memory) because the
    /// device has no ASUS firmware interface. Experimental, and the driver may restart the PC by itself when a size
    /// is stored: every change needs the user's go-ahead for a restart.
    /// </summary>
    public bool IgpuMemoryViaAmdDriver { get; private set; }

    /// <summary>iGPU memory in effect during this Windows session, in MB (0 = Auto); null if unknown.</summary>
    public int? IgpuMemoryEffectiveMb => IgpuMemory is { } options && _igpuBoot is { } boot ? options.ToMb(boot.EffectiveRaw) : null;

    /// <summary>iGPU memory that applies after the next restart, in MB, or null when nothing is pending.</summary>
    public int? IgpuMemoryPendingMb => IgpuMemory is { } options && _igpuBoot is { } boot && options.CurrentRaw != boot.EffectiveRaw
        ? options.ToMb(options.CurrentRaw)
        : null;

    /// <summary>
    /// The size the current mode's profile asks for and that waits for the user's go-ahead (the profile is set to
    /// ask first), or null. Not while paused: the profile is not being applied then.
    /// </summary>
    public IgpuMemoryOffer? IgpuMemoryOffer =>
        !Paused && _igpuAwaitedMode is null && IgpuMemory is { } options
        && IgpuMemoryPlanner.Decide(Config.ProfileFor(Mode), options, _igpuBoot?.EffectiveRaw, IgpuMemoryViaAmdDriver) is { Step: IgpuMemoryStep.Ask } decision
            ? new IgpuMemoryOffer(Mode, decision.MemoryMb)
            : null;

    /// <summary>True while an eGPU is connected. Some PCs do not start with one attached, which matters before a restart.</summary>
    public bool EgpuConnected => Snapshot is { } snapshot && ModeResolver.FindEgpu(snapshot, Config.Detection) is not null;

    /// <summary>
    /// The other modes it is worth restarting for: their iGPU memory size differs from the one in effect, so storing
    /// it before the restart saves the restart that would follow the mode change (<see cref="PrepareRestart"/>).
    /// </summary>
    public IReadOnlyList<IgpuRestartOption> IgpuRestartOptions => IgpuMemory is { } options && _igpuBoot is { } boot
        ? IgpuRestartPlanner.Options(Config, Mode, options, boot.EffectiveRaw)
        : [];

    /// <summary>True when config.json did not exist at startup (first run).</summary>
    public bool IsFirstRun { get; private set; }

    public string ConfigPath => _configStore.FilePath;

    /// <param name="resumeFrom">
    /// Set when this instance takes over from one that was just running (after an update): the mode whose profile
    /// that instance had applied last (<see cref="LastAppliedMode"/>). If the mode is still the same, nothing is
    /// applied again, so whatever the user or other software changed since (the power plan, for instance) stays.
    /// If it differs, that is an ordinary mode change, which waits while paused.
    /// </param>
    /// <param name="paused">Start with automatic switching paused: no profile is applied until it is resumed.</param>
    public void Start(DockMode? resumeFrom = null, bool paused = false)
    {
        Paused = paused;
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
        BeginAwaitedModeWait(mode);

        Log.Info($"Started{(resumeFrom is null ? string.Empty : " after an update")}. {Describe(Snapshot)} -> {mode}");
        AddActivity(Loc.F("Act_Started", ModeName(mode)));
        if (resumeFrom is not { } previous)
        {
            ApplyProfile(mode, ApplyReason.Startup);
        }
        else if (previous == mode)
        {
            // The plan state saved by the previous instance (owned or handed over) carries on as it was.
            _lastApplied = mode;
            _resumedInSameMode = true;
            Log.Info($"Still {mode}: the profile stays as the previous instance applied it.");
        }
        else if (Paused)
        {
            // The transition from the last applied profile is still pending; resuming applies it.
            _lastApplied = previous;
            Log.Info($"Paused: the {previous} profile is still the one applied; {mode} waits until switching is resumed.");
        }
        else
        {
            _lastApplied = previous;
            Log.Info($"The mode changed from {previous} to {mode} while DockSense was restarting.");
            AddActivity(Loc.F("Act_ModeChanged", ModeName(mode)));
            ApplyProfile(mode, ApplyReason.ModeChange);
        }

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
        _igpuWaitTimer.Stop();
        _disposed = true;
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
        ApplyIgpuMemory(mode, profile, reason);
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

            // At sign-in the value may already have been written before the UI loaded; that still counts as a change.
            var changedEarly = reason == ApplyReason.Startup && SignInFastPath.TakeFseChange(wantFse);
            if (FseSettings.IsAvailable && (FseSettings.SetStartupToFse(wantFse) || changedEarly))
            {
                Log.Info($"{mode}: Full Screen Experience at sign-in -> {(wantFse ? "on" : "off")}");
                AddActivity(Loc.F("Act_Fse", Loc.T(wantFse ? "Common_On" : "Common_Off")));
            }
        }

        // Switching live while the user edits settings would yank the screen away; only real transitions do it.
        if (reason != ApplyReason.SettingsChanged && FsePlanner.LiveTarget(Config, mode) is not null)
        {
            _fseMode = mode;

            // Right after sign-in the shell is still starting (and entering the full screen experience when Windows
            // boots into it): look at the live state only once it has settled.
            _ = SwitchFseAsync(reason == ApplyReason.Startup ? FsePlanner.StartupDelay(SignInSession.SinceSignIn) : TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Brings the full screen experience in line with the mode applied last. One switch runs at a time:
    /// <see cref="FullScreenExperience.Set"/> can wait on Windows' confirmation dialog, and if the mode changes
    /// meanwhile, the running switch reconciles with the newest mode when it returns.
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
        // Always the latest mode, with the profiles as they are now, so a switch that waited on a prompt cannot
        // leave a stale state.
        var mode = _fseMode;
        if (Paused || Mode != mode || FsePlanner.LiveTarget(Config, mode) is not { } wantFse)
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
        if (!AmdAdlx.Simulated && SafeReadIgpuMemory() is { } options)
        {
            AdoptIgpuMemory(options);
        }
        else
        {
            // No ASUS firmware interface. The AMD driver may offer the setting instead; it is asked in the
            // background, because its answer can take a while and must not hold up the start.
            _amdDriverReadPending = true;
            _ = ReadAmdDriverMemoryAsync();
        }
    }

    private void AdoptIgpuMemory(IgpuMemoryOptions options)
    {
        // The value stored when this boot session is first seen is the one in effect now.
        var boot = CurrentBoot();
        var saved = AmdAdlx.Simulated ? null : StateStore.LoadIgpuMemory(); // a simulation leaves the real state alone
        if (saved?.Boot is { } savedBoot && savedBoot.IsSameBoot(boot))
        {
            _igpuBoot = saved;
            if (IgpuMemoryViaAmdDriver && saved.StoredRaw is { } stored)
            {
                options = options with { CurrentRaw = stored, CurrentIsFixed = true }; // stored by DockSense earlier in this boot session
            }
        }
        else
        {
            _igpuBoot = new IgpuMemoryBootState(boot, options.CurrentRaw);
            if (!AmdAdlx.Simulated)
            {
                StateStore.SaveIgpuMemory(_igpuBoot);
            }
        }

        IgpuMemory = options;
        Log.Info($"iGPU memory: {options.ToMb(_igpuBoot.EffectiveRaw)} MB in effect, {options.ToMb(options.CurrentRaw)} MB stored {(IgpuMemoryViaAmdDriver ? "by the AMD driver" : "in the firmware")}.");
    }

    /// <summary>
    /// Asks the AMD driver for Variable Graphics Memory once the Windows shell is up (right after sign-in the
    /// driver's services may not answer yet), with one more try a little later. When the driver offers sizes, the
    /// iGPU memory setting appears and the current mode's size is offered, as it would have been at the start.
    /// </summary>
    private async Task ReadAmdDriverMemoryAsync()
    {
        try
        {
            await Task.Delay(SignInSession.ShellWait);
            for (var attempt = 1; attempt <= 2 && !_disposed; attempt++)
            {
                var note = string.Empty;
                var report = await Task.Run(() => AmdAdlx.ReadVariableGraphicsMemory(out note));
                if (_disposed || note == AmdAdlx.NoDriver)
                {
                    return;
                }

                if (report is not null)
                {
                    AdoptAmdDriverMemory(report);
                    return;
                }

                Log.Info($"AMD graphics memory could not be read (try {attempt}): {note}.");
                if (attempt == 1)
                {
                    await Task.Delay(AmdDriverRetry);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Reading the AMD graphics memory failed", ex);
        }
        finally
        {
            _amdDriverReadPending = false;
        }
    }

    private void AdoptAmdDriverMemory(AdlxMemoryReport report)
    {
        if (!report.Supported)
        {
            Log.Info($"iGPU memory: not adjustable on this device (no ASUS firmware interface; the AMD driver, ADLX {report.AdlxVersion}, does not offer Variable Graphics Memory).");
            return;
        }

        Log.Info($"AMD Variable Graphics Memory (ADLX {report.AdlxVersion}): current {report.Current?.ToString() ?? "-"}; default {report.Default?.ToString() ?? "-"}; options: {string.Join(" | ", report.Options)}");
        if (report.ToIgpuMemoryOptions() is not { } options)
        {
            Log.Warn("The AMD driver offers Variable Graphics Memory, but no sizes DockSense can use.");
            return;
        }

        IgpuMemoryViaAmdDriver = true;
        AdoptIgpuMemory(options);
        Log.Info("iGPU memory is set through the AMD driver on this device. This is experimental.");

        // What the start skipped while the driver had not answered yet. After an update nothing is applied again
        // while the mode is still the same; the Status page shows an open question all the same.
        _amdDriverReadPending = false;
        BeginAwaitedModeWait(Mode);
        if (!Paused && !_resumedInSameMode)
        {
            ApplyIgpuMemory(Mode, Config.ProfileFor(Mode), ApplyReason.Startup);
        }

        RaiseStatusChanged();
    }

    /// <summary>
    /// After a restart the user started from DockSense for a mode's iGPU memory size, the PC may come back up in
    /// another mode, because the dock or eGPU gets connected only after sign-in (some PCs do not start with an eGPU
    /// attached). The size the user restarted for is in effect then. Putting this mode's size back at once would
    /// undo that restart and ask for another one, so the startup apply leaves the iGPU memory alone and that mode
    /// gets a while to show up.
    /// </summary>
    private void BeginAwaitedModeWait(DockMode mode)
    {
        if (_amdDriverReadPending)
        {
            return; // Asked again once the AMD driver has told what it offers.
        }

        if (StateStore.LoadIgpuRestartTarget() is not { } target)
        {
            return;
        }

        var boot = _igpuBoot?.Boot ?? CurrentBoot();
        if (target.Boot.IsSameBoot(boot))
        {
            return; // DockSense restarted, Windows has not (yet): the note stays for the boot that follows.
        }

        var remaining = IgpuRestartPlanner.RemainingWait(target, DateTime.UtcNow, AwaitedModeWait);
        if (remaining <= TimeSpan.Zero || IgpuMemory is not { } options || _igpuBoot is not { } state
            || IgpuRestartPlanner.AwaitedMode(target, boot, mode, Config, options, state.EffectiveRaw) is not { } awaited)
        {
            StateStore.ClearIgpuRestartTarget();
            return;
        }

        // The record stays until the wait is over, now with its end: DockSense may be closed and started again
        // meanwhile, and that must neither end the wait nor start it over.
        if (target.WaitUntilUtc is null)
        {
            StateStore.SaveIgpuRestartTarget(target with { WaitUntilUtc = DateTime.UtcNow + remaining });
        }

        _igpuAwaitedMode = awaited;
        _igpuWaitTimer.Interval = remaining;
        _igpuWaitTimer.Start();
        Log.Info($"Restarted for {awaited}, now {mode}: the iGPU memory stays as it is until {awaited} starts, for up to {Math.Ceiling(remaining.TotalMinutes):0} more minute(s).");
        AddActivity(Loc.F("Act_IgpuAwaiting", ModeName(awaited)));
    }

    private void EndAwaitedModeWait()
    {
        _igpuWaitTimer.Stop();
        if (_igpuAwaitedMode is not null)
        {
            // Only the record of a wait that was running: one saved for a restart that is still to come stays.
            StateStore.ClearIgpuRestartTarget();
        }

        _igpuAwaitedMode = null;
    }

    private void OnAwaitedModeWaitOver()
    {
        if (_igpuAwaitedMode is not { } awaited)
        {
            _igpuWaitTimer.Stop();
            return;
        }

        EndAwaitedModeWait();
        Log.Info($"{awaited} did not start within {AwaitedModeWait.TotalMinutes:0} minutes of the restart for it.");
        if (!Paused)
        {
            // What the startup apply held back.
            ApplyIgpuMemory(Mode, Config.ProfileFor(Mode), ApplyReason.Startup);
        }

        RaiseStatusChanged();
    }

    private void ApplyIgpuMemory(DockMode mode, ModeProfile profile, ApplyReason reason)
    {
        if (reason != ApplyReason.Startup)
        {
            // The mode or the settings changed: the wait for the mode the PC was restarted for is over.
            EndAwaitedModeWait();
        }

        var asked = _igpuAsked;
        _igpuAsked = null;
        if (profile.IgpuMemoryMb is not { } target || IgpuMemory is null)
        {
            return;
        }

        if (_igpuAwaitedMode is { } awaited)
        {
            Log.Info($"{mode}: leaving the iGPU memory alone while waiting for {awaited}.");
            return;
        }

        // Re-read: Armoury Crate or another tool may have changed it since.
        IgpuMemory = SafeReadIgpuMemory() ?? IgpuMemory;
        if (IgpuMemory.RawFor(target) is null)
        {
            Log.Warn($"{mode}: the firmware does not offer {target} MB of iGPU memory; not changing it.");
            return;
        }

        var decision = IgpuMemoryPlanner.Decide(profile, IgpuMemory, _igpuBoot?.EffectiveRaw, IgpuMemoryViaAmdDriver);
        if (decision.Step == IgpuMemoryStep.Store)
        {
            if (StoreIgpuMemory(mode.ToString(), target, decision.Raw) && IgpuMemoryPendingMb is not null)
            {
                IgpuMemoryChanged?.Invoke(target);
            }
        }
        else if (decision.Step == IgpuMemoryStep.Ask)
        {
            // Saving any setting applies the profile again: the same question is not asked twice for that.
            _igpuAsked = new IgpuMemoryOffer(mode, target);
            if (reason != ApplyReason.SettingsChanged || _igpuAsked != asked)
            {
                Log.Info($"{mode}: the profile asks for {target} MB of iGPU memory; waiting for the user's go-ahead.");
                IgpuMemoryAsked?.Invoke(_igpuAsked);
            }
        }
    }

    /// <summary>
    /// Stores the size of <paramref name="offer"/> after the user agreed to it. Returns false when the offer no
    /// longer stands (an old notification: the mode or the profile changed since) or the size could not be stored.
    /// </summary>
    /// <param name="offer">The offer the user answered.</param>
    /// <param name="restartFollows">
    /// The caller restarts Windows right away. Otherwise a notification says that a restart is needed
    /// (<see cref="IgpuMemoryChanged"/>).
    /// </param>
    public bool AcceptIgpuMemory(IgpuMemoryOffer offer, bool restartFollows)
    {
        if (IgpuMemoryOffer != offer || IgpuMemory is null)
        {
            return false;
        }

        if (RefuseWhileAmdStoreUnanswered())
        {
            return false;
        }

        IgpuMemory = SafeReadIgpuMemory() ?? IgpuMemory;
        var decision = IgpuMemoryPlanner.Decide(Config.ProfileFor(offer.Mode), IgpuMemory, _igpuBoot?.EffectiveRaw, IgpuMemoryViaAmdDriver);
        if (restartFollows)
        {
            // Before the size is stored: the AMD driver may restart the PC by itself, and then nothing that
            // comes after the store runs. The next start needs to know which mode the restart was for.
            StateStore.SaveIgpuRestartTarget(new IgpuRestartTarget(offer.Mode, _igpuBoot?.Boot ?? CurrentBoot(), DateTime.UtcNow));
        }

        var stored = decision.Step == IgpuMemoryStep.None
            ? IgpuMemory.RawFor(offer.MemoryMb) is { } offered && IgpuMemory.IsStored(offered) // another tool stored it in the meantime
            : StoreIgpuMemory($"{offer.Mode} (agreed)", offer.MemoryMb, decision.Raw);
        if (!stored && _amdStoreUnanswered)
        {
            NotifyWarning(Loc.T("Act_IgpuAmdNoAnswer")); // the restart target stays: the request may yet go through
        }
        else if (!stored)
        {
            if (restartFollows)
            {
                StateStore.ClearIgpuRestartTarget(); // no restart follows
            }

            NotifyWarning(Loc.F("Act_IgpuStoreFailed", FormatMemory(offer.MemoryMb)));
        }
        else if (!restartFollows && IgpuMemoryPendingMb is not null)
        {
            IgpuMemoryChanged?.Invoke(offer.MemoryMb);
        }

        RaiseStatusChanged();
        return stored;
    }

    /// <summary>
    /// Stores <paramref name="mode"/>'s iGPU memory size in the firmware now, while the device is still in another
    /// mode, so that it is in effect after the restart the caller starts next. Returns false when the size could
    /// not be stored. That mode's "full screen experience at sign-in" setting goes in as well, so Windows comes
    /// back up the way the mode wants it. If the device comes back up in the old mode after all, the startup apply
    /// puts that mode's settings back, as after any other change.
    /// </summary>
    public bool PrepareRestart(DockMode mode)
    {
        if (Config.ProfileFor(mode).IgpuMemoryMb is not { } target || IgpuMemory is null)
        {
            return false;
        }

        if (RefuseWhileAmdStoreUnanswered())
        {
            return false;
        }

        var fresh = SafeReadIgpuMemory();
        IgpuMemory = fresh ?? IgpuMemory;
        if (IgpuMemory.RawFor(target) is not { } raw)
        {
            Log.Warn($"Restart for {mode}: the firmware does not offer {target} MB of iGPU memory.");
            NotifyWarning(Loc.F("Act_IgpuRestartFailed", ModeName(mode)));
            RaiseStatusChanged();
            return false;
        }

        // What the next start needs goes in before the size is stored: the AMD driver may restart the PC by itself,
        // and then nothing that comes after the store runs. It is taken back when the size cannot be stored.
        // First, the mode restarted for: if the PC comes back up before the dock or eGPU is connected, the size
        // stays for a while.
        StateStore.SaveIgpuRestartTarget(new IgpuRestartTarget(mode, _igpuBoot?.Boot ?? CurrentBoot(), DateTime.UtcNow));

        // Then the sign-in setting: Windows reads it when the shell starts after the restart, and DockSense itself
        // may be up too late.
        bool? fseBefore = null, fseSet = null;
        if (Config.ProfileFor(mode).Fse is var fse and not FseStartup.Unchanged && FseSettings.IsAvailable)
        {
            var wantFse = fse == FseStartup.FullScreenExperience;
            fseBefore = FseSettings.StartupToFse;
            if (FseSettings.SetStartupToFse(wantFse))
            {
                fseSet = wantFse;
                Log.Info($"Restart for {mode}: Full Screen Experience at sign-in -> {(wantFse ? "on" : "off")}");
            }
        }

        // "Already stored" only counts when the firmware was just read: after a failed read the value known here
        // may be out of date (Armoury Crate can change it), and the PC is about to restart on it.
        var stored = (fresh is not null && fresh.IsStored(raw)) || StoreIgpuMemory($"Restart for {mode}", target, raw);
        if (!stored && _amdStoreUnanswered)
        {
            // Nothing is taken back: the request is still with the driver and may yet go through. Should it fail
            // after all, the sign-in setting goes back to what it was, as on an immediate failure.
            var changed = fseSet is not null && fseBefore is not null && fseBefore != fseSet;
            _fseBeforeUnansweredStore = changed ? fseBefore : null;
            NotifyWarning(Loc.T("Act_IgpuAmdNoAnswer"));
        }
        else if (!stored)
        {
            StateStore.ClearIgpuRestartTarget();
            if (fseSet is { } set && fseBefore is { } before && before != set && FseSettings.SetStartupToFse(before))
            {
                Log.Info($"Restart for {mode}: no restart, so Full Screen Experience at sign-in is {(before ? "on" : "off")} again.");
            }

            NotifyWarning(Loc.F("Act_IgpuRestartFailed", ModeName(mode)));
        }
        else if (fseSet is { } wanted)
        {
            AddActivity(Loc.F("Act_Fse", Loc.T(wanted ? "Common_On" : "Common_Off")));
        }

        RaiseStatusChanged();
        return stored;
    }

    /// <summary>
    /// Called right before a restart the user asked for while the current mode's size waits for one ("Restart now").
    /// If the PC then comes back up before this mode's dock or eGPU is connected again, the size stays for a while
    /// (<see cref="BeginAwaitedModeWait"/>).
    /// </summary>
    public void NoteRestartForPendingSize()
    {
        if (IgpuMemoryPendingMb is { } pending && Config.ProfileFor(Mode).IgpuMemoryMb == pending)
        {
            StateStore.SaveIgpuRestartTarget(new IgpuRestartTarget(Mode, _igpuBoot?.Boot ?? CurrentBoot(), DateTime.UtcNow));
        }
    }

    /// <summary>
    /// Says again that a stored iGPU memory size waits for a restart (<see cref="IgpuMemoryChanged"/>), if one does.
    /// For a restart the user asked for that did not happen: the size prepared for it stays in the firmware.
    /// </summary>
    public void RemindIgpuRestartPending()
    {
        if (IgpuMemoryPendingMb is { } pending)
        {
            IgpuMemoryChanged?.Invoke(pending);
        }
    }

    /// <summary>Writes a raw value the firmware lists. <paramref name="context"/> says in the log who asked.</summary>
    private bool StoreIgpuMemory(string context, int megabytes, uint raw)
    {
        if (IgpuMemoryViaAmdDriver)
        {
            return StoreIgpuMemoryThroughAmdDriver(context, megabytes, raw);
        }

        try
        {
            if (!AsusAcpi.WriteIgpuMemory(raw))
            {
                Log.Warn($"{context}: the firmware rejected iGPU memory {megabytes} MB.");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{context}: could not set iGPU memory", ex);
            return false;
        }

        IgpuMemory = SafeReadIgpuMemory() ?? IgpuMemory! with { CurrentRaw = raw };
        Log.Info($"{context}: iGPU memory set to {megabytes} MB; it applies after a restart.");
        AddActivity(Loc.F("Act_IgpuMemory", FormatMemory(megabytes)));
        return true;
    }

    /// <summary>
    /// Experimental. The AMD driver may restart the PC by itself when it stores a size, in which case this call
    /// does not come back: every caller has the user's go-ahead for a restart.
    /// </summary>
    private bool StoreIgpuMemoryThroughAmdDriver(string context, int megabytes, uint raw)
    {
        if (_amdStoreUnanswered)
        {
            Log.Warn($"{context}: the AMD driver has not answered the previous request yet; not sending another one.");
            return false;
        }

        Log.Info($"{context}: asking the AMD driver to store iGPU memory {megabytes} MB. The driver may restart the PC by itself.");
        var stored = AmdAdlx.SetVariableGraphicsMemory(megabytes, out var note, out var late);
        if (stored is null && late is not null)
        {
            // Not a failure: the request is still with the driver and may yet go through (or restart the PC).
            // Callers keep what they prepared for a restart, and the outcome is picked up when it comes.
            Log.Warn($"{context}: {note}. It is not known yet whether iGPU memory {megabytes} MB was stored.");
            _amdStoreUnanswered = true;
            _ = FollowLateAmdStoreAsync(context, late, megabytes, raw);
            return false;
        }

        if (stored != true)
        {
            Log.Warn($"{context}: the AMD driver did not store iGPU memory {megabytes} MB: {note}.");
            AddActivity(Loc.F("Act_IgpuAmdFailed", note));
            return false;
        }

        NoteStoredThroughAmdDriver(context, megabytes, raw);
        return true;
    }

    /// <summary>
    /// While the AMD driver has not answered a store request, that request may still go through or restart the PC,
    /// and what was prepared for its restart belongs to it. Nothing else is prepared or stored until it answers;
    /// the user is told so. Returns true when a request is outstanding.
    /// </summary>
    private bool RefuseWhileAmdStoreUnanswered()
    {
        if (!_amdStoreUnanswered)
        {
            return false;
        }

        Log.Warn("The AMD driver has not answered the previous iGPU memory request yet; nothing else is stored or prepared until it does.");
        NotifyWarning(Loc.T("Act_IgpuAmdNoAnswer"));
        RaiseStatusChanged();
        return true;
    }

    private void NoteStoredThroughAmdDriver(string context, int megabytes, uint raw)
    {
        // The driver is not known to report a size that waits for a restart, so remember it here.
        IgpuMemory = IgpuMemory! with { CurrentRaw = raw, CurrentIsFixed = true };
        if (_igpuBoot is { } boot)
        {
            _igpuBoot = boot with { StoredRaw = raw };
            StateStore.SaveIgpuMemory(_igpuBoot);
        }

        Log.Info($"{context}: the AMD driver stored iGPU memory {megabytes} MB and has not restarted the PC by itself.");
        AddActivity(Loc.F("Act_IgpuMemory", FormatMemory(megabytes)));
    }

    /// <summary>
    /// The AMD driver answered a store request after DockSense had stopped waiting for it. If the size went in,
    /// it now waits for a restart like any other, and the notification offers one. If not, the restart that was
    /// prepared for it is off.
    /// </summary>
    private async Task FollowLateAmdStoreAsync(string context, Task<(bool Stored, string Note)> late, int megabytes, uint raw)
    {
        var (stored, note) = await late;
        _amdStoreUnanswered = false;
        var fseBefore = _fseBeforeUnansweredStore;
        _fseBeforeUnansweredStore = null;
        if (_disposed)
        {
            return;
        }

        if (stored)
        {
            NoteStoredThroughAmdDriver($"{context} (late answer)", megabytes, raw);
            if (IgpuMemoryPendingMb is not null)
            {
                IgpuMemoryChanged?.Invoke(megabytes);
            }
        }
        else
        {
            // What was prepared for the restart is taken back, as on an immediate failure.
            Log.Warn($"{context}: the AMD driver answered late and did not store iGPU memory {megabytes} MB: {note}.");
            StateStore.ClearIgpuRestartTarget();
            if (fseBefore is { } before && FseSettings.SetStartupToFse(before))
            {
                Log.Info($"{context}: no restart, so Full Screen Experience at sign-in is {(before ? "on" : "off")} again.");
            }

            NotifyWarning(Loc.F("Act_IgpuAmdFailed", note));
        }

        RaiseStatusChanged();
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

    /// <summary>
    /// The firmware's current answer. For the AMD driver it is what DockSense already knows: asking the driver
    /// takes too long for the UI thread, and DockSense keeps track of what it stored itself.
    /// </summary>
    private IgpuMemoryOptions? SafeReadIgpuMemory()
    {
        if (IgpuMemoryViaAmdDriver)
        {
            return IgpuMemory;
        }

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
            // Started by the sign-in task, DockSense is up before the Windows shell. Programs wait for the desktop,
            // as Windows' own startup apps do; the wait sits in the queue so later batches keep their order.
            var wait = atStartup ? SignInSession.ShellWait : TimeSpan.Zero;
            _programBatches.Enqueue(new ProgramBatch(_lastApplied, mode, atStartup, programs, wait));
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
                if (batch.Wait > TimeSpan.Zero)
                {
                    Log.Info($"Programs for {batch.Current} wait {batch.Wait.TotalSeconds:F0} s for the Windows shell.");
                    await Task.Delay(batch.Wait);
                }

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
            return DeviceProbe.TakeSnapshot();
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
