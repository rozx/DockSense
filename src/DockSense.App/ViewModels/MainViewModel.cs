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

public sealed record Option<T>(T Value, string Label);

public sealed record HardwareItem(string Name, string Badge, bool IsExternal);

/// <summary>Shared view model for all pages (one app, one window, one source of truth).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/rozx/DockSense";

    private readonly DockSenseService _service;
    private bool _syncing;

    public MainViewModel(DockSenseService service)
    {
        _service = service;
        Profiles = [new ModeProfileViewModel(DockMode.Handheld, this), new ModeProfileViewModel(DockMode.Docked, this), new ModeProfileViewModel(DockMode.Egpu, this)];
        Plans = new PlanListViewModel(service);
        _service.StatusChanged += (_, _) => Refresh();
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
    [ObservableProperty] public partial string PowerText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PauseButtonText { get; set; } = string.Empty;
    [ObservableProperty] public partial InfoBarSeverity ThrottleSeverity { get; set; }
    [ObservableProperty] public partial string ThrottleTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ThrottleMessage { get; set; } = string.Empty;

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
    [ObservableProperty] public partial bool NotifyOnModeChange { get; set; }
    [ObservableProperty] public partial bool NotifyOnStartup { get; set; }
    [ObservableProperty] public partial bool NotifyOnProgramStart { get; set; }
    [ObservableProperty] public partial bool NotifyOnWarnings { get; set; }
    [ObservableProperty] public partial bool ReassertPowerPlan { get; set; }

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
    private static async Task Restart()
    {
        if (!await Dialogs.ConfirmAsync(Loc.T("Restart_Title"), Loc.T("Restart_Message"), Loc.T("Restart_Confirm")))
        {
            return;
        }

        try
        {
            // No /f: Windows still asks apps with unsaved work before restarting.
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"Could not restart: {ex.Message}");
        }
    }

    [RelayCommand]
    private static void OpenLog() => Open(Log.FilePath);

    [RelayCommand]
    private async Task CopyReport()
    {
        try
        {
            Clipboard.SetText(ProbeReport.Build(_service.Config));
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

        IgpuAvailable = _service.IgpuMemory is not null;
        var effective = _service.IgpuMemoryEffectiveMb;
        var pending = _service.IgpuMemoryPendingMb;
        IgpuText = effective is { } now ? DockSenseService.FormatMemory(now) : Loc.T("Common_Unknown");
        IgpuPending = pending is not null;
        IgpuPendingMessage = pending is { } next ? Loc.F("Dash_IgpuPendingMessage", DockSenseService.FormatMemory(next)) : string.Empty;

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
            profile.FseExitOnLeave = config.FseExitOnLeave;
            profile.FseLiveDescription = Loc.T(fseLiveSupported ? "Prof_FseLiveDesc" : "Prof_FseLiveUnsupported");

            profile.IgpuAvailable = igpu is not null;
            var igpuOptions = BuildIgpuOptions(igpu, config.IgpuMemoryMb);
            if (!profile.IgpuOptions.SequenceEqual(igpuOptions))
            {
                profile.IgpuOptions = igpuOptions;
            }

            profile.SelectedIgpu = config.IgpuMemoryMb ?? ModeProfileViewModel.IgpuUnchanged;

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
        StartWithWindows = AutoStart.IsEnabled;
        NotifyOnModeChange = ui.NotifyOnModeChange;
        NotifyOnStartup = ui.NotifyOnStartup;
        NotifyOnProgramStart = ui.NotifyOnProgramStart;
        NotifyOnWarnings = ui.NotifyOnWarnings;
        ReassertPowerPlan = _service.Config.ReassertPowerPlan;
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
            FseExitOnLeave = profile.FseExitOnLeave,
            IgpuMemoryMb = profile.SelectedIgpu == ModeProfileViewModel.IgpuUnchanged ? null : profile.SelectedIgpu,
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
    [ObservableProperty] public partial bool FseExitOnLeave { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FseLiveEnabled))]
    public partial bool FseLiveSupported { get; set; }

    [ObservableProperty] public partial string FseLiveDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IgpuAvailable { get; set; }
    [ObservableProperty] public partial IReadOnlyList<Option<int>> IgpuOptions { get; set; } = [];
    [ObservableProperty] public partial int SelectedIgpu { get; set; } = IgpuUnchanged;
    [ObservableProperty] public partial IReadOnlyList<Option<ProgramTrigger>> WhenOptions { get; set; } = [];

    /// <summary>"Don't change" in the iGPU memory list.</summary>
    public const int IgpuUnchanged = -1;

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

    partial void OnFseExitOnLeaveChanged(bool value) => owner.SaveProfile(this);

    partial void OnSelectedIgpuChanged(int value) => owner.SaveProfile(this);
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
