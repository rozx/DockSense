using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.Core;

namespace DockSense.App.ViewModels;

/// <summary>One "run a program" entry of a mode profile. Every change is saved right away.</summary>
public sealed partial class ProgramItemViewModel : ObservableObject
{
    private readonly ModeProfileViewModel _owner;
    private bool _loading;

    public ProgramItemViewModel(ModeProfileViewModel owner, ProgramLaunch program)
    {
        _owner = owner;
        Load(program);
    }

    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string Path { get; set; } = string.Empty;
    [ObservableProperty] public partial string Arguments { get; set; } = string.Empty;
    [ObservableProperty] public partial ProgramTrigger When { get; set; }
    [ObservableProperty] public partial bool Hidden { get; set; }
    [ObservableProperty] public partial bool RunAsAdministrator { get; set; }
    [ObservableProperty] public partial string TestStatus { get; set; } = string.Empty;

    public ProgramLaunch ToModel() => new()
    {
        Enabled = Enabled,
        Path = Path.Trim(),
        Arguments = Arguments,
        When = When,
        Hidden = Hidden,
        RunAsAdministrator = RunAsAdministrator,
    };

    internal void Load(ProgramLaunch program)
    {
        _loading = true;
        try
        {
            Enabled = program.Enabled;
            Path = program.Path;
            Arguments = program.Arguments;
            When = program.When;
            Hidden = program.Hidden;
            RunAsAdministrator = program.RunAsAdministrator;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnEnabledChanged(bool value) => Changed();

    partial void OnPathChanged(string value) => Changed();

    partial void OnArgumentsChanged(string value) => Changed();

    partial void OnWhenChanged(ProgramTrigger value) => Changed();

    partial void OnHiddenChanged(bool value) => Changed();

    partial void OnRunAsAdministratorChanged(bool value) => Changed();

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Prog_BrowseTitle"),
            Filter = $"{Loc.T("Prog_FilterPrograms")}|*.exe;*.bat;*.cmd;*.ps1;*.lnk;*.url|{Loc.T("Prog_FilterAll")}|*.*",
            CheckFileExists = true,
        };

        var current = Environment.ExpandEnvironmentVariables(Path.Trim().Trim('"'));
        if (System.IO.Path.IsPathFullyQualified(current) && System.IO.Path.GetDirectoryName(current) is { } directory && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog() == true)
        {
            Path = dialog.FileName;
        }
    }

    [RelayCommand]
    private async Task Test()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            return;
        }

        TestStatus = Loc.T("Prog_Starting");
        var error = await DockSenseService.TestProgramAsync(ToModel());
        TestStatus = error is null ? Loc.T("Prog_Started") : Loc.F("Prog_Failed", error);
        if (error is null)
        {
            Log.Info($"Test-started {ProgramLauncher.DisplayName(ToModel())}.");
        }
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveProgram(this);

    private void Changed()
    {
        if (!_loading)
        {
            TestStatus = string.Empty;
            _owner.ProgramsChanged();
        }
    }
}
