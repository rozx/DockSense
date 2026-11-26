using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.App.Services;
using DockSense.Core;
using Wpf.Ui.Controls;

namespace DockSense.App.ViewModels;

/// <summary>One choice of a list, as the views see it whatever the type of its value.</summary>
public interface IOption
{
    object? Value { get; }

    string Label { get; }

    /// <summary>The number someone would type for this choice (watts, gigabytes), or null when it has none.</summary>
    double? Number { get; }
}

public sealed record Option<T>(T Value, string Label, double? Number = null) : IOption
{
    object? IOption.Value => Value;
}

public sealed record HardwareItem(string Name, string Badge, bool IsExternal);

/// <summary>Shared view model for all pages (one app, one window, one source of truth).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/rozx/DockSense";

    /// <summary>How long a requested restart may take before DockSense concludes it was held up or cancelled.</summary>
    private static readonly TimeSpan RestartGrace = TimeSpan.FromSeconds(30);

    private readonly DockSenseService _service;
    private readonly UpdateService _updates;
    private bool _syncing;

    public MainViewModel(DockSenseService service, UpdateService updates)
    {
        _service = service;
        _updates = updates;
        Profiles = [new ModeProfileViewModel(DockMode.Handheld, this), new ModeProfileViewModel(DockMode.Docked, this), new ModeProfileViewModel(DockMode.Egpu, this)];
        Plans = new PlanListViewModel(service);
        _service.StatusChanged += (_, _) => Refresh();
        _updates.Changed += (_, _) => RefreshUpdates();
        Loc.Instance.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    // ------------------------------------------------------------------ Status page

    [ObservableProperty] public partial string ModeTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModeDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial ImageSource? ModeIcon { get; set; }
    [ObservableProperty] public partial Brush ModeAccent { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial string PlanName { get; set; } = string.Empty;
    [ObservableProperty] public partial string FseText { get; set; } = string.Empty;
    [ObservableProperty] public partial string FseNowText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuAvailable { get; set; }
    [ObservableProperty] public partial string IgpuText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuPending { get; set; }
    [ObservableProperty] public partial string IgpuPendingMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuOfferVisible { get; set; }
    [ObservableProperty] public partial string IgpuOfferTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string IgpuOfferMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuOfferChangeVisible { get; set; }
    [ObservableProperty] public partial bool TdpVisible { get; set; }
    [ObservableProperty] public partial string TdpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PowerText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PauseButtonText { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity ThrottleSeverity { get; set; }
    [ObservableProperty] public partial string ThrottleTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ThrottleMessage { get; set; } = string.Empty;

    [ObservableProperty] public partial bool UpdateVisible { get; set; }
    [ObservableProperty] public partial string UpdateTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpdateMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpdateActionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool UpdateActionVisible { get; set; }

    public ObservableCollection<HardwareItem> Adapters { get; } = [];

    public ObservableCollection<HardwareItem> Displays { get; } = [];

    public ObservableCollection<ActivityEntry> Activity => _service.Activity;

    // ------------------------------------------------------------------ Profiles page

    public IReadOnlyList<ModeProfileViewModel> Profiles { get; }

    // ------------------------------------------------------------------ Power plans page

    public PlanListViewModel Plans { get; }

    // ------------------------------------------------------------------ Detection page

    [ObservableProperty] public partial double SettleSeconds { get; set; }
    [ObservableProperty] public partial string SettleText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool DockedRequiresAc { get; set; }

    public ObservableCollection<AdapterOverrideViewModel> AdapterOverrides { get; } = [];

    // ------------------------------------------------------------------ Settings page

    [ObservableProperty] public partial IReadOnlyList<Option<string>> LanguageOptions { get; set; } = [];
    [ObservableProperty] public partial string SelectedLanguage { get; set; } = Loc.SystemLanguage;
    [ObservableProperty] public partial IReadOnlyList<Option<AppTheme>> ThemeOptions { get; set; } = [];
    [ObservableProperty] public partial AppTheme SelectedTheme { get; set; }
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool StartupNoteVisible { get; set; }
    [ObservableProperty] public partial string StartupNote { get; set; } = string.Empty;
    [ObservableProperty] public partial bool NotifyOnModeChange { get; set; }
    [ObservableProperty] public partial bool NotifyOnStartup { get; set; }
    [ObservableProperty] public partial bool NotifyOnProgramStart { get; set; }
    [ObservableProperty] public partial bool NotifyOnWarnings { get; set; }
    [ObservableProperty] public partial bool ReassertPowerPlan { get; set; }
    [ObservableProperty] public partial bool TdpHelperInstalled { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<UpdateMode>> UpdateModeOptions { get; set; } = [];
    [ObservableProperty] public partial UpdateMode SelectedUpdateMode { get; set; }
    [ObservableProperty] public partial string UpdateStatusText { get; set; } = string.Empty;

    // ------------------------------------------------------------------ About page

    public string VersionText => Loc.F("About_Version",
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    [ObservableProperty] public partial string CopyReportText { get; set; } = string.Empty;

    // ------------------------------------------------------------------ Commands

    [RelayCommand]
    private void TogglePause() => _service.SetPaused(!_service.Paused);

    [RelayCommand]
    private void Recheck() => _service.ScheduleRescan("requested from the window");

    [RelayCommand]
    private static void OpenUrl(string url) => Open(url);

    [RelayCommand]
    private void OpenConfig() => Open(_service.ConfigPath);

    [RelayCommand]
    private async Task Restart()
    {
        if (await Dialogs.ConfirmAsync(Loc.T("Restart_Title"), RestartMessage(storesFirst: false), Loc.T("Restart_Confirm")))
        {
            RestartWindows();
        }
    }

    /// <summary>
    /// Restarts Windows for the iGPU memory size that waits for it. Every restart the user asks for goes through
    /// here, apart from "Restart for &lt;mode&gt;", which prepares its own.
    /// </summary>
    internal bool RestartWindows()
    {
        _service.NoteRestartForPendingSize();
        return SystemPower.Restart();
    }

    /// <summary>
    /// The restart confirmation, with a reminder while an eGPU is connected (some PCs do not start with one
    /// attached) and, when a size is stored through the AMD driver first, the note that this is experimental.
    /// </summary>
    private string RestartMessage(bool storesFirst)
    {
        var message = Loc.T("Restart_Message");
        if (storesFirst && _service.IgpuMemoryViaAmdDriver)
        {
            message += $"\n\n{Loc.T("Restart_AmdHint")}";
        }

        return _service.EgpuConnected ? $"{message}\n\n{Loc.T("Restart_EgpuHint")}" : message;
    }

    /// <summary>
    /// The Status card's "Change": stores the size the current mode asks for; the restart comes later. Through the
    /// AMD driver there is no "later" to promise, so that goes the way of "Change and restart".
    /// </summary>
    [RelayCommand]
    private Task ChangeIgpuMemory()
    {
        if (_service.IgpuMemoryOffer is not { } offer)
        {
            return Task.CompletedTask;
        }

        return _service.IgpuMemoryViaAmdDriver ? ChangeIgpuMemoryAndRestart() : AcceptIgpuOfferAsync(offer, restart: false);
    }

    /// <summary>The Status card's "Change and restart": asks first, as every restart from the window does.</summary>
    [RelayCommand]
    private async Task ChangeIgpuMemoryAndRestart()
    {
        if (_service.IgpuMemoryOffer is { } offer
            && await Dialogs.ConfirmAsync(Loc.T("Restart_Title"), RestartMessage(storesFirst: true), Loc.T("Restart_Confirm")))
        {
            await AcceptIgpuOfferAsync(offer, restart: true);
        }
    }

    /// <summary>
    /// Stores the iGPU memory size the user agreed to and, when asked, restarts Windows for it. Does nothing when
    /// the offer no longer stands or the size could not be stored (the service says so).
    /// </summary>
    internal async Task AcceptIgpuOfferAsync(IgpuMemoryOffer offer, bool restart)
    {
        if (!_service.AcceptIgpuMemory(offer, restartFollows: restart) || !restart)
        {
            return;
        }

        if (RestartWindows())
        {
            // Windows may still not restart: an app with unsaved work holds it up, and the user can cancel there.
            await Task.Delay(RestartGrace);
        }

        // Still running: the size is stored, and the notification offers the restart again.
        _service.RemindIgpuRestartPending();
    }

    /// <summary>
    /// "Restart for &lt;mode&gt;": stores that mode's iGPU memory size now and restarts, so the user can switch
    /// modes while the PC is restarting instead of restarting once more afterwards. Needs the window (it asks first).
    /// </summary>
    [RelayCommand]
    private async Task RestartFor(DockMode mode)
    {
        if (_service.IgpuRestartOptions.FirstOrDefault(o => o.Mode == mode) is not { } option)
        {
            return; // No longer on offer, e.g. the mode changed while the menu was open.
        }

        var name = DockSenseService.ModeName(mode);
        var message = Loc.F("RestartFor_Message", name, DockSenseService.FormatMemory(option.MemoryMb));
        if (mode == DockMode.Egpu && !_service.EgpuConnected)
        {
            // The message says to plug in while the PC restarts, which is exactly what some PCs cannot take.
            message += $"\n\n{Loc.T("RestartFor_EgpuHint")}";
        }

        if (_service.EgpuConnected)
        {
            // Restarting for another mode with an eGPU still attached: the PC may not start that way.
            message += $"\n\n{Loc.T("Toast_EgpuHint")}";
        }

        if (_service.IgpuMemoryViaAmdDriver)
        {
            message += $"\n\n{Loc.T("Restart_AmdHint")}";
        }

        if (!await Dialogs.ConfirmAsync(Loc.F("RestartFor_Title", name), message, Loc.T("Restart_Confirm"))
            || !_service.PrepareRestart(mode))
        {
            return;
        }

        if (SystemPower.Restart())
        {
            // Windows may still not restart: an app with unsaved work holds it up, and the user can cancel there.
            await Task.Delay(RestartGrace);
        }

        // Still running, so the restart did not happen (yet), but the size for the other mode is stored and takes
        // effect at whatever restart comes next. The notification says so and offers the restart again.
        _service.RemindIgpuRestartPending();
    }

    /// <summary>The "Remove" button of the TDP helper card under Settings.</summary>
    [RelayCommand]
    private async Task RemoveTdpHelper()
    {
        if (await Dialogs.ConfirmAsync(Loc.T("Tdp_RemoveTitle"), Loc.T("Tdp_RemoveMessage"), Loc.T("Tdp_RemoveConfirm")))
        {
            await _service.RemoveTdpHelperAsync();
        }
    }

    [RelayCommand]
    private static void OpenLog() => Open(Log.FilePath);

    [RelayCommand]
    private Task CheckForUpdates() => _updates.CheckAsync(manual: true);

    /// <summary>Installs the update, or opens the release page when DockSense cannot install it itself.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (_updates.CanInstall)
        {
            await _updates.InstallAsync();
        }
        else
        {
            OpenReleasePage();
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => Open(_updates.Offer?.PageUrl ?? UpdatePlanner.ReleasesUrl);

    [RelayCommand]
    private async Task CopyReport()
    {
        try
        {
            // Not on the UI thread: the report asks the AMD driver among others, which can take seconds.
            var config = _service.Config;
            Clipboard.SetText(await Task.Run(() => ProbeReport.Build(config)));
            CopyReportText = Loc.T("About_Copied");
            await Task.Delay(2000);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"Could not copy the report: {ex.Message}");
        }

        CopyReportText = Loc.T("About_CopyReport");
    }

    // ------------------------------------------------------------------ Sync from the service

    private void Refresh()
    {
        _syncing = true;
        try
        {
            var plans = PowerPlans.ListAll();
            RefreshStatus();
            RefreshProfiles(plans);
            Plans.Refresh(plans, SafeActivePlan());
            RefreshDetection();
            RefreshSettings();
            RefreshUpdates();
            OnPropertyChanged(nameof(VersionText));
            CopyReportText = Loc.T("About_CopyReport");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshStatus()
    {
        var mode = _service.Mode;
        var snapshot = _service.Snapshot;
        IsPaused = _service.Paused;
        ModeTitle = Loc.T($"ModeTitle_{mode}");
        ModeDetail = _service.Paused ? Loc.T("Status_PausedDetail") : snapshot is null ? string.Empty : mode switch
        {
            DockMode.Egpu => Loc.F("ModeDetail_Egpu", ModeResolver.FindEgpu(snapshot, _service.Config.Detection)?.Name ?? Loc.T("Mode_Egpu")),
            DockMode.Docked => Loc.F("ModeDetail_Docked", snapshot.Displays.Count(d => !d.IsInternal)),
            _ => Loc.T("ModeDetail_Handheld"),
        };
        ModeIcon = IconAssets.Image(IconAssets.NameFor(mode, _service.Paused));
        ModeAccent = AccentFor(_service.Paused ? null : mode);
        PauseButtonText = Loc.T(_service.Paused ? "Dash_Resume" : "Dash_Pause");

        PlanName = DockSenseService.SafeGetActivePlanName() ?? Loc.T("Common_Unknown");
        FseText = FseSettings.StartupToFse switch
        {
            true => Loc.T("Common_On"),
            false => Loc.T("Common_Off"),
            null => Loc.T("Common_NotAvailable"),
        };
        FseNowText = FullScreenExperience.IsActive switch
        {
            true => Loc.T("Fse_Active"),
            false => Loc.T("Fse_Inactive"),
            null => Loc.T("Common_NotAvailable"),
        };
        PowerText = Loc.T(snapshot?.OnAcPower == false ? "Power_Battery" : "Power_Ac");
        TdpVisible = _service.TdpInForceWatts is not null;
        TdpText = _service.TdpInForceWatts is { } tdp
            ? Loc.F(_service.TdpInForceUnconfirmed ? "Tdp_WattsUnconfirmed" : "Tdp_Watts", tdp)
            : string.Empty;

        IgpuAvailable = _service.IgpuMemory is not null;
        var effective = _service.IgpuMemoryEffectiveMb;
        var pending = _service.IgpuMemoryPendingMb;
        IgpuText = effective is { } now ? DockSenseService.FormatMemory(now) : Loc.T("Common_Unknown");
        IgpuPending = pending is not null;
        IgpuPendingMessage = pending is { } next ? Loc.F("Dash_IgpuPendingMessage", DockSenseService.FormatMemory(next)) : string.Empty;
        var offer = _service.IgpuMemoryOffer;
        IgpuOfferVisible = offer is not null;
        IgpuOfferTitle = offer is null ? string.Empty : Loc.F("Dash_IgpuOfferTitle", DockSenseService.FormatMemory(offer.MemoryMb));
        var viaAmdDriver = _service.IgpuMemoryViaAmdDriver;
        IgpuOfferMessage = offer is null
            ? string.Empty
            : Loc.F(viaAmdDriver ? "Dash_IgpuOfferMessageAmd" : "Dash_IgpuOfferMessage", DockSenseService.ModeName(offer.Mode));
        IgpuOfferChangeVisible = !viaAmdDriver;

        var gameMode = _service.GameMode;
        if (gameMode is null || !gameMode.GameModeEnabled || gameMode.Overrides.Count == 0)
        {
            (ThrottleSeverity, ThrottleTitle, ThrottleMessage) =
                (InfoBarSeverity.Informational, Loc.T("Dash_ThrottleNoneTitle"), Loc.T("Dash_ThrottleNoneMessage"));
        }
        else if (gameMode.ThrottlesActivePlan)
        {
            (ThrottleSeverity, ThrottleTitle, ThrottleMessage) =
                (InfoBarSeverity.Warning, Loc.T("Dash_ThrottleWarnTitle"), Loc.T("Dash_ThrottleWarnMessage"));
        }
        else
        {
            (ThrottleSeverity, ThrottleTitle, ThrottleMessage) =
                (InfoBarSeverity.Success, Loc.T("Dash_ThrottleOkTitle"), Loc.T("Dash_ThrottleOkMessage"));
        }

        Replace(Adapters, (snapshot?.Gpus ?? []).Select(g =>
            new HardwareItem(g.Name, Loc.T(g.IsExternal ? "Badge_External" : "Badge_BuiltIn"), g.IsExternal)));
        Replace(Displays, (snapshot?.Displays ?? []).Select(d =>
            new HardwareItem(string.IsNullOrEmpty(d.Name) ? Loc.T("Dash_Displays") : d.Name, Loc.T(d.IsInternal ? "Badge_BuiltIn" : "Badge_External"), !d.IsInternal)));
    }

    private void RefreshProfiles(IReadOnlyList<PlanInfo> plans)
    {
        var planOptions = BuildPlanOptions(plans);
        var fseOptions = new List<Option<FseStartup>>
        {
            new(FseStartup.Unchanged, Loc.T("Fse_Unchanged")),
            new(FseStartup.Desktop, Loc.T("Fse_Desktop")),
            new(FseStartup.FullScreenExperience, Loc.T("Fse_Fse")),
        };
        var whenOptions = new List<Option<ProgramTrigger>>
        {
            new(ProgramTrigger.Enter, Loc.T("Prog_WhenEnter")),
            new(ProgramTrigger.EnterOrStartup, Loc.T("Prog_WhenEnterOrStartup")),
            new(ProgramTrigger.Leave, Loc.T("Prog_WhenLeave")),
        };
        var fseLiveSupported = FullScreenExperience.IsSupported;
        var igpu = _service.IgpuMemory;
        var tdpDevice = _service.TdpDevice;
        var tdpReadiness = tdpDevice is null ? TdpReadiness.Ready : _service.TdpReadiness;

        foreach (var profile in Profiles)
        {
            var config = _service.Config.ProfileFor(profile.Mode);
            profile.Title = Loc.T($"ModeTitle_{profile.Mode}");
            profile.Description = Loc.T($"ModeDesc_{profile.Mode}");
            profile.IsCurrent = _service.Mode == profile.Mode;
            if (!profile.PlanOptions.SequenceEqual(planOptions))
            {
                profile.PlanOptions = planOptions;
            }

            profile.SelectedPlan = PlanKey(config.PowerPlan);
            if (!profile.FseOptions.SequenceEqual(fseOptions))
            {
                profile.FseOptions = fseOptions;
            }

            profile.SelectedFse = config.Fse;
            profile.FseLiveSupported = fseLiveSupported;
            profile.FseLive = config.FseApplyImmediately;
            profile.FseLiveDescription = Loc.T(fseLiveSupported ? "Prof_FseLiveDesc" : "Prof_FseLiveUnsupported");

            profile.IgpuAvailable = igpu is not null;
            var igpuOptions = BuildIgpuOptions(igpu, config.IgpuMemoryMb);
            if (!profile.IgpuOptions.SequenceEqual(igpuOptions))
            {
                profile.IgpuOptions = igpuOptions;
            }

            profile.SelectedIgpu = config.IgpuMemoryMb ?? ModeProfileViewModel.IgpuUnchanged;
            // Through the AMD driver every change asks first, whatever the profile says.
            profile.IgpuExperimental = _service.IgpuMemoryViaAmdDriver;
            profile.IgpuAsk = profile.IgpuExperimental || config.IgpuMemoryApply == IgpuMemoryApply.Ask;
            profile.IgpuDescription = Loc.T(profile.IgpuExperimental ? "Prof_IgpuDescAmd" : "Prof_IgpuDesc");
            profile.IgpuAskDescription = Loc.T(profile.IgpuExperimental ? "Prof_IgpuAskDescAmd" : "Prof_IgpuAskDesc");

            // The saved value is taken over whether or not this device can use it: saving a profile writes
            // back what is here, and a TDP set up for another device must not be lost by editing something else.
            profile.TdpAvailable = tdpDevice is not null;
            profile.SelectedTdp = config.TdpWatts ?? ModeProfileViewModel.TdpUnchanged;
            profile.TdpEnabled = config.TdpWatts is not null;
            if (tdpDevice is not null)
            {
                var tdpOptions = BuildTdpOptions(tdpDevice, config.TdpWatts);
                if (!profile.TdpOptions.SequenceEqual(tdpOptions))
                {
                    profile.TdpOptions = tdpOptions;
                }

                // Switched off, the slider keeps the last value; never used before, it starts at the highest value
                // the manufacturer allows on either power source, so that switching the override on never asks
                // about going above the maximum before a value was even chosen.
                profile.TdpWatts = config.TdpWatts ?? (tdpDevice.Offers(profile.TdpWatts) ? profile.TdpWatts : tdpDevice.WarnAboveWatts);
                profile.TdpDescription = Loc.F("Prof_TdpDesc", tdpDevice.MinWatts, ManufacturerMaxText(tdpDevice));

                // What keeps the value from being applied, with the way out where there is one.
                var blocked = config.TdpWatts is not null && tdpReadiness != TdpReadiness.Ready;
                var unconfirmed = config.TdpWatts is { } saved && tdpDevice.Offers(saved)
                    && !tdpDevice.IsConfirmed(saved, _service.Config.TdpConfirmedBoard, _service.Config.TdpConfirmedWatts);
                profile.TdpNote = blocked ? Loc.T(tdpReadiness switch
                {
                    TdpReadiness.DriverMissing => "Tdp_NoteDriverMissing",
                    TdpReadiness.UnprotectedFolder => "Tdp_NoteUnprotected",
                    _ => "Tdp_NoteHelperMissing",
                })
                    : unconfirmed ? Loc.F("Tdp_NoteUnconfirmed", config.TdpWatts)
                    : string.Empty;
                profile.TdpNoteAction = !blocked ? (unconfirmed ? Loc.T("Tdp_Confirm") : string.Empty) : tdpReadiness switch
                {
                    TdpReadiness.DriverMissing => Loc.T("Tdp_GetDriver"),
                    TdpReadiness.HelperMissing => Loc.T("Tdp_SetUp"),
                    _ => string.Empty,
                };
                profile.TdpWarning = config.TdpWatts is { } watts && tdpDevice.Offers(watts) && tdpDevice.ExceedsManufacturerMax(watts)
                    ? Loc.F("Tdp_Warning", watts, ManufacturerMaxText(tdpDevice))
                    : string.Empty;
            }

            if (!profile.WhenOptions.SequenceEqual(whenOptions))
            {
                profile.WhenOptions = whenOptions;
            }

            profile.SyncPrograms(config.Programs);
        }
    }

    private static List<Option<int>> BuildIgpuOptions(IgpuMemoryOptions? igpu, int? configured)
    {
        var options = new List<Option<int>> { new(ModeProfileViewModel.IgpuUnchanged, Loc.T("Igpu_Unchanged")) };
        if (igpu is not null)
        {
            options.AddRange(igpu.SizesMb.Select(mb => new Option<int>(mb, DockSenseService.FormatMemory(mb))));
        }

        // A size from another device's config stays visible instead of silently changing.
        if (configured is { } mb && options.All(o => o.Value != mb))
        {
            options.Add(new Option<int>(mb, Loc.F("Igpu_NotOffered", DockSenseService.FormatMemory(mb))));
        }

        return options;
    }

    private static List<Option<int>> BuildTdpOptions(TdpDevice device, int? configured)
    {
        var options = new List<Option<int>>();
        for (var watts = device.MinWatts; watts <= device.CeilingWatts; watts++)
        {
            options.Add(new Option<int>(watts, Loc.F("Tdp_Watts", watts), watts));
        }

        // A value from another device's config stays visible instead of silently changing.
        if (configured is { } value && !device.Offers(value))
        {
            options.Add(new Option<int>(value, Loc.F("Tdp_NotOffered", value)));
        }

        return options;
    }

    /// <summary>"35 W", or "30 W plugged in, 25 W on battery" where the two differ.</summary>
    private static string ManufacturerMaxText(TdpDevice device) => device.PluggedIn.Max == device.OnBattery.Max
        ? Loc.F("Tdp_Watts", device.PluggedIn.Max)
        : Loc.F("Tdp_MaxBySource", device.PluggedIn.Max, device.OnBattery.Max);

    /// <summary>
    /// A new TDP choice in a profile. Above the manufacturer's maximum it asks first, and the first value ever
    /// chosen sets up the elevated helper (Windows asks for approval). Saying no puts the old choice back.
    /// </summary>
    internal async void ChangeTdp(ModeProfileViewModel profile, int previous, int value)
    {
        if (_syncing || _service.TdpDevice is not { } device)
        {
            return;
        }

        if (value != ModeProfileViewModel.TdpUnchanged && device.Offers(value))
        {
            // The question names the watts, so the answer covers that value and anything lower. Going higher
            // asks again, and so does a value kept from another device, which nobody confirmed for this one.
            // That includes choosing the same value again after the note under the setting asked for it.
            // What was agreed to is kept with the settings, per board: a number in a profile says nothing about
            // that, it may have come from another device.
            var agreed = false;
            if (!device.IsConfirmed(value, _service.Config.TdpConfirmedBoard, _service.Config.TdpConfirmedWatts))
            {
                if (!await ConfirmAboveMaximumAsync(device, value))
                {
                    SetTdpSelection(profile, previous);
                    return;
                }

                agreed = true;
            }

            // The driver may have been installed since this was last looked at. Nothing is sent from here: the
            // value being replaced must not go out, and the new one is applied when the profile is saved below.
            _service.RefreshTdpReadiness(apply: false);
            if (_service.TdpReadiness == TdpReadiness.HelperMissing
                && !(await Dialogs.ConfirmAsync(Loc.T("Tdp_SetupTitle"), Loc.T("Tdp_SetupMessage"), Loc.T("Tdp_SetupConfirm"))
                    && await _service.SetUpTdpHelperAsync(apply: false)))
            {
                SetTdpSelection(profile, previous);
                return;
            }

            // Kept only now, with the value it was given for: backing out of the helper setup takes the
            // agreement back as well.
            if (agreed)
            {
                SaveTdpConfirmation(device, value, apply: false); // The profile saved below applies it.
            }
        }

        // The window may have been refreshed from the saved settings while a dialog was open.
        SetTdpSelection(profile, value);
        SaveProfile(profile);
    }

    /// <summary>Asks whether <paramref name="watts"/>, above the manufacturer's maximum, is really wanted.</summary>
    private static Task<bool> ConfirmAboveMaximumAsync(TdpDevice device, int watts) =>
        Dialogs.ConfirmAsync(Loc.T("Tdp_WarningTitle"), Loc.F("Tdp_WarningMessage", watts, ManufacturerMaxText(device)), Loc.T("Tdp_WarningConfirm"));

    /// <summary>Records the agreement to <paramref name="watts"/> and lower values, in every profile, on this model.</summary>
    private void SaveTdpConfirmation(TdpDevice device, int watts, bool apply) =>
        _service.UpdateConfig(c => c with
        {
            TdpConfirmedBoard = device.Board,

            // Never lowered for the same model: dialogs overlap (the helper setup takes a while, and the other
            // profiles stay editable meanwhile), and an older answer must not take back a newer, higher one.
            TdpConfirmedWatts = string.Equals(c.TdpConfirmedBoard, device.Board, StringComparison.OrdinalIgnoreCase)
                ? Math.Max(c.TdpConfirmedWatts, watts)
                : watts,
        }, reapply: apply);

    /// <summary>
    /// The button next to a profile's "not applied" note: get the driver, set up the helper, or confirm a saved
    /// value above the manufacturer's maximum.
    /// </summary>
    internal async Task ResolveTdpNote(ModeProfileViewModel profile)
    {
        // Look again first: the note may be about something the user has put right meanwhile (the driver
        // installed after following the link). Then the note goes away, or its button offers the next step.
        _service.RefreshTdpReadiness();
        switch (_service.TdpReadiness)
        {
            case TdpReadiness.DriverMissing:
                Open(TdpHelper.PawnIoUrl);
                break;

            case TdpReadiness.HelperMissing:
                if (await Dialogs.ConfirmAsync(Loc.T("Tdp_SetupTitle"), Loc.T("Tdp_SetupMessage"), Loc.T("Tdp_SetupConfirm")))
                {
                    await _service.SetUpTdpHelperAsync();
                }

                break;

            case TdpReadiness.Ready:
                // The saved value is what the slider shows already, so choosing it again changes nothing: the
                // question is asked from here. Answered for what is saved now, not for what was on screen.
                if (_service.TdpDevice is { } device
                    && _service.Config.ProfileFor(profile.Mode).TdpWatts is { } saved
                    && device.Offers(saved)
                    && !device.IsConfirmed(saved, _service.Config.TdpConfirmedBoard, _service.Config.TdpConfirmedWatts)
                    && await ConfirmAboveMaximumAsync(device, saved)
                    && _service.Config.ProfileFor(profile.Mode).TdpWatts == saved)
                {
                    SaveTdpConfirmation(device, saved, apply: true);
                }

                break;
        }
    }

    /// <summary>True while the view models are being filled from the saved settings: nothing is to be saved then.</summary>
    internal bool IsSyncing => _syncing;

    /// <summary>Shows a TDP choice (<see cref="ModeProfileViewModel.TdpUnchanged"/> = off) without acting on it.</summary>
    private void SetTdpSelection(ModeProfileViewModel profile, int value)
    {
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            profile.SelectedTdp = value;
            profile.TdpEnabled = value != ModeProfileViewModel.TdpUnchanged;
            if (profile.TdpEnabled)
            {
                profile.TdpWatts = value;
            }
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    private List<Option<string>> BuildPlanOptions(IReadOnlyList<PlanInfo> plans)
    {
        var options = new List<Option<string>>
        {
            new("unmanaged", Loc.T("Plan_Unmanaged")),
            new("docksense", Loc.T("Plan_DockSense")),
        };

        // All plans, including hidden High Performance-type ones (e.g. created on the Power plans page).
        var ownPlan = _service.Config.DockSensePerformancePlanId;
        options.AddRange(plans
            .Where(p => p.Id != ownPlan)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new Option<string>(p.Id.ToString(), p.Name)));

        // Keep plans that are configured but no longer exist, so the choice stays visible.
        foreach (var profile in Profiles)
        {
            var choice = _service.Config.ProfileFor(profile.Mode).PowerPlan;
            if (choice is { Kind: PowerPlanKind.Specific, PlanId: { } id } && options.All(o => o.Value != id.ToString()))
            {
                options.Add(new Option<string>(id.ToString(), PowerPlans.GetName(id) ?? id.ToString()));
            }
        }

        return options;
    }

    private void RefreshDetection()
    {
        var detection = _service.Config.Detection;
        SettleSeconds = detection.SettleSeconds;
        SettleText = Loc.F("Det_Seconds", detection.SettleSeconds);
        DockedRequiresAc = detection.DockedRequiresAcPower;

        IReadOnlyList<GpuDetails> adapters;
        try
        {
            adapters = GpuProbe.GetAllAdapterDetails();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            adapters = [];
        }

        var overrideOptions = new List<Option<string>>
        {
            new(AdapterOverrideViewModel.Auto, Loc.T("Override_Auto")),
            new(AdapterOverrideViewModel.ForceEgpu, Loc.T("Override_Egpu")),
            new(AdapterOverrideViewModel.Ignore, Loc.T("Override_Ignore")),
        };

        // Keep adapters that went away (e.g. unplugged eGPU) if they have an override, so it can still be edited.
        var ids = adapters.Select(a => a.InstanceId)
            .Concat(detection.ForceEgpuInstanceIds)
            .Concat(detection.IgnoreGpuInstanceIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!ids.SequenceEqual(AdapterOverrides.Select(a => a.InstanceId), StringComparer.OrdinalIgnoreCase))
        {
            AdapterOverrides.Clear();
            foreach (var id in ids)
            {
                AdapterOverrides.Add(new AdapterOverrideViewModel(id, this));
            }
        }

        foreach (var item in AdapterOverrides)
        {
            var details = adapters.FirstOrDefault(a => string.Equals(a.InstanceId, item.InstanceId, StringComparison.OrdinalIgnoreCase));
            item.Name = details?.Name ?? item.InstanceId;
            item.DetectedText = details is null ? string.Empty
                : Loc.T(details.ToGpuInfo().IsExternal ? "Det_DetectedExternal" : "Det_DetectedBuiltIn");
            if (!item.Options.SequenceEqual(overrideOptions))
            {
                item.Options = overrideOptions;
            }

            item.Selected = detection.ForceEgpuInstanceIds.Contains(item.InstanceId, StringComparer.OrdinalIgnoreCase) ? AdapterOverrideViewModel.ForceEgpu
                : detection.IgnoreGpuInstanceIds.Contains(item.InstanceId, StringComparer.OrdinalIgnoreCase) ? AdapterOverrideViewModel.Ignore
                : AdapterOverrideViewModel.Auto;
        }
    }

    private void RefreshSettings()
    {
        var ui = _service.Config.Ui;
        List<Option<string>> languages =
        [
            new(Loc.SystemLanguage, Loc.T("Lang_System")),
            new(Loc.English, "English"),
            new(Loc.SimplifiedChinese, "简体中文"),
        ];
        if (!LanguageOptions.SequenceEqual(languages))
        {
            LanguageOptions = languages;
        }

        SelectedLanguage = ui.Language;
        List<Option<AppTheme>> themes =
        [
            new(AppTheme.System, Loc.T("Theme_System")),
            new(AppTheme.Light, Loc.T("Theme_Light")),
            new(AppTheme.Dark, Loc.T("Theme_Dark")),
        ];
        if (!ThemeOptions.SequenceEqual(themes))
        {
            ThemeOptions = themes;
        }

        SelectedTheme = ui.Theme;
        RefreshStartup();
        NotifyOnModeChange = ui.NotifyOnModeChange;
        NotifyOnStartup = ui.NotifyOnStartup;
        NotifyOnProgramStart = ui.NotifyOnProgramStart;
        NotifyOnWarnings = ui.NotifyOnWarnings;
        ReassertPowerPlan = _service.Config.ReassertPowerPlan;

        TdpHelperInstalled = _service.TdpHelperInstalled;

        List<Option<UpdateMode>> updateModes =
        [
            new(UpdateMode.Off, Loc.T("UpdateMode_Off")),
            new(UpdateMode.Notify, Loc.T("UpdateMode_Notify")),
            new(UpdateMode.Automatic, Loc.T("UpdateMode_Automatic")),
        ];
        if (!UpdateModeOptions.SequenceEqual(updateModes))
        {
            UpdateModeOptions = updateModes;
        }

        SelectedUpdateMode = _service.Config.Updates;
    }

    /// <summary>
    /// The switch shows what really happens at sign-in, not just whether DockSense once wrote its entry. When the
    /// two differ, a note under the switch says why.
    /// </summary>
    private void RefreshStartup()
    {
        var status = AutoStart.Status;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            StartWithWindows = status.State == StartupState.On;
        }
        finally
        {
            _syncing = wasSyncing;
        }

        StartupNote = status switch
        {
            { State: StartupState.DisabledByWindows } => Loc.T("Set_StartupDisabledByWindows"),
            { State: StartupState.OtherCopy } => Loc.F("Set_StartupOtherCopy", status.OtherCommand),
            { WaitsForDesktop: true } => Loc.T("Set_StartupWaitsForDesktop"),
            _ when AutoStart.StartsLate(status) => Loc.T("Set_StartupLate"),
            _ => string.Empty,
        };
        StartupNoteVisible = StartupNote.Length > 0;
    }

    /// <summary>The update card on the Status page and the status line under Settings.</summary>
    private void RefreshUpdates()
    {
        var stage = _updates.Stage;
        var offer = _updates.Offer;
        var version = offer is null ? string.Empty : ReleaseVersion.Format(offer.Version);

        UpdateVisible = offer is not null;
        UpdateTitle = Loc.F(stage is UpdateStage.Ready or UpdateStage.Installing ? "Upd_ReadyTitle" : "Upd_AvailableTitle", version);
        UpdateActionVisible = stage is UpdateStage.Available or UpdateStage.Ready;
        UpdateActionText = Loc.T(_updates.CanInstall ? "Upd_Install" : "Upd_OpenPage");
        UpdateMessage = _updates.Error ?? stage switch
        {
            UpdateStage.Downloading => Loc.F("Upd_Downloading", _updates.DownloadPercent),
            UpdateStage.Installing => Loc.T("Upd_Installing"),
            _ when !_updates.CanInstall => Loc.F("Upd_ManualMessage", AppInfo.VersionText),
            _ when _updates.InstallsAtNextStart => Loc.F("Upd_AutomaticMessage", AppInfo.VersionText),
            UpdateStage.Ready => Loc.T(_updates.NeedsElevation ? "Upd_ReadyElevated" : "Upd_ReadyMessage"),
            _ => Loc.F(_updates.NeedsElevation ? "Upd_AvailableElevated" : "Upd_AvailableMessage", AppInfo.VersionText),
        };

        UpdateStatusText = _updates.Error ?? stage switch
        {
            UpdateStage.Checking => Loc.T("Upd_StatusChecking"),
            _ when offer is not null => Loc.F("Upd_StatusAvailable", version),
            _ when _updates.LastCheck is { } checkedAt => Loc.F("Upd_StatusLatest", AppInfo.VersionText, checkedAt.LocalDateTime),
            _ => Loc.F("Upd_StatusNever", AppInfo.VersionText),
        };
    }

    // ------------------------------------------------------------------ Save back to the config

    internal void SaveProfile(ModeProfileViewModel profile)
    {
        if (_syncing || profile.SelectedPlan is null)
        {
            return;
        }

        var updated = _service.Config.ProfileFor(profile.Mode) with
        {
            PowerPlan = ParsePlanKey(profile.SelectedPlan),
            Fse = profile.SelectedFse,
            FseApplyImmediately = profile.FseLive,
            IgpuMemoryMb = profile.SelectedIgpu == ModeProfileViewModel.IgpuUnchanged ? null : profile.SelectedIgpu,
            // The switch is locked on while the size goes through the AMD driver: keep what the profile said.
            IgpuMemoryApply = profile.IgpuExperimental ? _service.Config.ProfileFor(profile.Mode).IgpuMemoryApply
                : profile.IgpuAsk ? IgpuMemoryApply.Ask : IgpuMemoryApply.Automatic,
            TdpWatts = profile.SelectedTdp == ModeProfileViewModel.TdpUnchanged ? null : profile.SelectedTdp,
            Programs = profile.Programs.Select(p => p.ToModel()).ToList(),
        };

        _service.UpdateConfig(config => profile.Mode switch
        {
            DockMode.Handheld => config with { Handheld = updated },
            DockMode.Docked => config with { Docked = updated },
            _ => config with { Egpu = updated },
        }, reapply: true);
    }

    internal void SaveAdapterOverride(AdapterOverrideViewModel adapter)
    {
        if (_syncing)
        {
            return;
        }

        _service.UpdateConfig(config =>
        {
            var force = config.Detection.ForceEgpuInstanceIds.Where(id => !Same(id, adapter.InstanceId)).ToList();
            var ignore = config.Detection.IgnoreGpuInstanceIds.Where(id => !Same(id, adapter.InstanceId)).ToList();
            if (adapter.Selected == AdapterOverrideViewModel.ForceEgpu)
            {
                force.Add(adapter.InstanceId);
            }
            else if (adapter.Selected == AdapterOverrideViewModel.Ignore)
            {
                ignore.Add(adapter.InstanceId);
            }

            return config with { Detection = config.Detection with { ForceEgpuInstanceIds = force, IgnoreGpuInstanceIds = ignore } };
        }, reapply: true);

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSettleSecondsChanged(double value)
    {
        SettleText = Loc.F("Det_Seconds", Math.Round(value));
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Detection = c.Detection with { SettleSeconds = Math.Round(value) } }, reapply: false);
        }
    }

    partial void OnDockedRequiresAcChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Detection = c.Detection with { DockedRequiresAcPower = value } }, reapply: true);
        }
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        if (!_syncing && value is not null)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { Language = value } }, reapply: false);
            Loc.Instance.SetLanguage(value);
        }
    }

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { Theme = value } }, reapply: false);
            App.ApplyTheme(value);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_syncing)
        {
            AutoStart.SetEnabled(value);
            RefreshStartup();
        }
    }

    partial void OnNotifyOnModeChangeChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { NotifyOnModeChange = value } }, reapply: false);
        }
    }

    partial void OnNotifyOnStartupChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { NotifyOnStartup = value } }, reapply: false);
        }
    }

    partial void OnNotifyOnProgramStartChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { NotifyOnProgramStart = value } }, reapply: false);
        }
    }

    partial void OnNotifyOnWarningsChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Ui = c.Ui with { NotifyOnWarnings = value } }, reapply: false);
        }
    }

    partial void OnReassertPowerPlanChanged(bool value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { ReassertPowerPlan = value }, reapply: false);
        }
    }

    partial void OnSelectedUpdateModeChanged(UpdateMode value)
    {
        if (!_syncing)
        {
            _service.UpdateConfig(c => c with { Updates = value }, reapply: false);
            if (value != UpdateMode.Off)
            {
                // Turning updates on (or to automatic) takes effect now, not at tomorrow's check.
                _ = _updates.CheckAsync(manual: false);
            }
        }
    }

    // ------------------------------------------------------------------ Helpers

    public static Brush AccentFor(DockMode? mode)
    {
        // Used only for small status dots, so the UI stays neutral.
        var color = mode switch
        {
            DockMode.Handheld => Color.FromRgb(0x4A, 0xDE, 0x80),
            DockMode.Docked => Color.FromRgb(0x60, 0xA5, 0xFA),
            DockMode.Egpu => Color.FromRgb(0xA7, 0x8B, 0xFA),
            _ => Color.FromRgb(0xA1, 0xA1, 0xAA),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string PlanKey(PowerPlanChoice choice) => choice.Kind switch
    {
        PowerPlanKind.DockSensePerformance => "docksense",
        PowerPlanKind.Specific when choice.PlanId is { } id => id.ToString(),
        _ => "unmanaged",
    };

    private static PowerPlanChoice ParsePlanKey(string key) => key switch
    {
        "docksense" => PowerPlanChoice.DockSensePerformance,
        _ when Guid.TryParse(key, out var id) => PowerPlanChoice.Specific(id),
        _ => PowerPlanChoice.Unmanaged,
    };

    private static Guid? SafeActivePlan()
    {
        try
        {
            return PowerPlans.GetActive();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list))
        {
            return;
        }

        target.Clear();
        foreach (var item in list)
        {
            target.Add(item);
        }
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }
}

public sealed partial class ModeProfileViewModel(DockMode mode, MainViewModel owner) : ObservableObject
{
    public DockMode Mode { get; } = mode;

    public ImageSource Icon { get; } = IconAssets.Image(IconAssets.NameFor(mode));

    public Brush Accent { get; } = MainViewModel.AccentFor(mode);

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<string>> PlanOptions { get; set; } = [];
    [ObservableProperty] public partial string? SelectedPlan { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<FseStartup>> FseOptions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FseLiveEnabled))]
    public partial FseStartup SelectedFse { get; set; }

    [ObservableProperty] public partial bool FseLive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FseLiveEnabled))]
    public partial bool FseLiveSupported { get; set; }

    [ObservableProperty] public partial string FseLiveDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuAvailable { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<int>> IgpuOptions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IgpuAskEnabled))]
    public partial int SelectedIgpu { get; set; } = IgpuUnchanged;

    [ObservableProperty] public partial bool IgpuAsk { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IgpuAskEnabled))]
    public partial bool IgpuExperimental { get; set; }

    [ObservableProperty] public partial string IgpuDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial string IgpuAskDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial bool TdpAvailable { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<int>> TdpOptions { get; set; } = [];
    /// <summary>The profile's TDP in watts, or <see cref="TdpUnchanged"/>. The switch and the slider feed it.</summary>
    [ObservableProperty] public partial int SelectedTdp { get; set; } = TdpUnchanged;

    /// <summary>The switch: off leaves the TDP to the vendor software.</summary>
    [ObservableProperty] public partial bool TdpEnabled { get; set; }

    /// <summary>The slider's value. It keeps its place while the switch is off.</summary>
    [ObservableProperty] public partial int TdpWatts { get; set; }

    [ObservableProperty] public partial string TdpDescription { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TdpNoteVisible))]
    public partial string TdpNote { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TdpNoteActionVisible))]
    public partial string TdpNoteAction { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TdpWarningVisible))]
    public partial string TdpWarning { get; set; } = string.Empty;

    [ObservableProperty] public partial IReadOnlyList<Option<ProgramTrigger>> WhenOptions { get; set; } = [];

    /// <summary>"Don't change" in the iGPU memory list.</summary>
    public const int IgpuUnchanged = -1;

    /// <summary>Asking first needs a size to ask about.</summary>
    public bool IgpuAskEnabled => SelectedIgpu != IgpuUnchanged && !IgpuExperimental;
    /// <summary><see cref="SelectedTdp"/> when the override is off.</summary>
    public const int TdpUnchanged = -1;

    public bool TdpNoteVisible => TdpNote.Length > 0;

    public bool TdpNoteActionVisible => TdpNoteAction.Length > 0;

    public bool TdpWarningVisible => TdpWarning.Length > 0;

    [RelayCommand]
    private Task ResolveTdpNote() => owner.ResolveTdpNote(this);

    /// <summary>The live switch needs a target (desktop or FSE) and the Windows API.</summary>
    public bool FseLiveEnabled => FseLiveSupported && SelectedFse != FseStartup.Unchanged;

    public System.Collections.ObjectModel.ObservableCollection<ProgramItemViewModel> Programs { get; } = [];

    public bool HasPrograms => Programs.Count > 0;

    [RelayCommand]
    private void AddProgram()
    {
        Programs.Add(new ProgramItemViewModel(this, new ProgramLaunch()));
        OnPropertyChanged(nameof(HasPrograms));
        owner.SaveProfile(this);
    }

    internal void RemoveProgram(ProgramItemViewModel program)
    {
        Programs.Remove(program);
        OnPropertyChanged(nameof(HasPrograms));
        owner.SaveProfile(this);
    }

    internal void ProgramsChanged() => owner.SaveProfile(this);

    /// <summary>Rebuilds the list only when the saved config differs, so typing and focus are not disturbed.</summary>
    internal void SyncPrograms(IReadOnlyList<ProgramLaunch> programs)
    {
        if (Programs.Select(p => p.ToModel()).SequenceEqual(programs.Select(p => p with { Path = p.Path.Trim() })))
        {
            return;
        }

        Programs.Clear();
        foreach (var program in programs)
        {
            Programs.Add(new ProgramItemViewModel(this, program));
        }

        OnPropertyChanged(nameof(HasPrograms));
    }

    partial void OnSelectedPlanChanged(string? value) => owner.SaveProfile(this);

    partial void OnSelectedFseChanged(FseStartup value) => owner.SaveProfile(this);

    partial void OnFseLiveChanged(bool value) => owner.SaveProfile(this);

    partial void OnSelectedIgpuChanged(int value) => owner.SaveProfile(this);

    partial void OnIgpuAskChanged(bool value) => owner.SaveProfile(this);
    partial void OnSelectedTdpChanged(int oldValue, int newValue) => owner.ChangeTdp(this, oldValue, newValue);

    partial void OnTdpEnabledChanged(bool value)
    {
        if (!owner.IsSyncing)
        {
            SelectedTdp = value ? TdpWatts : TdpUnchanged;
        }
    }

    partial void OnTdpWattsChanged(int value)
    {
        if (!owner.IsSyncing && TdpEnabled)
        {
            SelectedTdp = value;
        }
    }
}

public sealed partial class AdapterOverrideViewModel(string instanceId, MainViewModel owner) : ObservableObject
{
    public const string Auto = "auto";
    public const string ForceEgpu = "egpu";
    public const string Ignore = "ignore";

    public string InstanceId { get; } = instanceId;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string DetectedText { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<Option<string>> Options { get; set; } = [];
    [ObservableProperty] public partial string Selected { get; set; } = Auto;

    partial void OnSelectedChanged(string value) => owner.SaveAdapterOverride(this);
}
