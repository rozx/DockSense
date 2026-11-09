using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DockSense.App.Localization;
using DockSense.App.Platform;
using DockSense.App.Services;
using DockSense.Core;

namespace DockSense.App.ViewModels;

/// <summary>The Power plans page: create, edit and delete Windows power plans.</summary>
public sealed partial class PlanListViewModel(DockSenseService service) : ObservableObject
{
    internal DockSenseService Service { get; } = service;

    public ObservableCollection<PlanItemViewModel> Items { get; } = [];

    [ObservableProperty] public partial IReadOnlyList<Option<Guid>> BaseOptions { get; set; } = [];
    [ObservableProperty] public partial Guid NewPlanBase { get; set; } = PowerSettingCatalog.HighPerformancePlan;
    [ObservableProperty] public partial string NewPlanName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ErrorText { get; set; } = string.Empty;

    [RelayCommand]
    private void Create()
    {
        var name = string.IsNullOrWhiteSpace(NewPlanName) ? Loc.T("Plans_DefaultName") : NewPlanName.Trim();
        try
        {
            var plan = Service.CreatePlan(name, NewPlanBase);
            NewPlanName = string.Empty;
            ErrorText = string.Empty;
            if (Items.FirstOrDefault(i => i.Id == plan) is { } item)
            {
                item.IsExpanded = true;
            }
        }
        catch (Win32Exception ex)
        {
            ErrorText = Loc.F("Plans_Error", ex.Message);
        }
    }

    /// <summary>Syncs the list with Windows without touching plans that are being edited.</summary>
    internal void Refresh(IReadOnlyList<PlanInfo> plans, Guid? activePlan)
    {
        var config = Service.Config;
        var throttles = Service.GameMode?.ThrottlesBalancedPlans == true;
        var sorted = plans.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (sorted.All(p => p.Id != Items[i].Id))
            {
                Items.RemoveAt(i);
            }
        }

        for (var index = 0; index < sorted.Count; index++)
        {
            var plan = sorted[index];
            var item = Items.FirstOrDefault(i => i.Id == plan.Id);
            if (item is null)
            {
                item = new PlanItemViewModel(plan.Id, this);
                Items.Insert(index, item);
            }
            else if (Items.IndexOf(item) != index)
            {
                Items.Move(Items.IndexOf(item), index);
            }

            var usedBy = Enum.GetValues<DockMode>().Where(mode => Uses(config, config.ProfileFor(mode).PowerPlan, plan.Id)).ToList();
            item.Update(
                plan.Name,
                isActive: plan.Id == activePlan,
                typeText: plan.Personality switch
                {
                    0 => Loc.T("PlanType_PowerSaver"),
                    1 => Loc.T("PlanType_HighPerformance"),
                    2 => Loc.T("PlanType_Balanced"),
                    _ => string.Empty,
                },
                usedByText: usedBy.Count == 0 ? string.Empty : Loc.F("Plans_UsedBy", string.Join(Loc.T("Common_ListSeparator"), usedBy.Select(DockSenseService.ModeName))),
                isThrottled: throttles && plan.Personality == 2);
        }

        var options = sorted
            .Select(p => new Option<Guid>(p.Id, p.Id == PowerSettingCatalog.HighPerformancePlan ? Loc.F("Plans_BaseRecommended", p.Name) : p.Name))
            .ToList();
        if (!BaseOptions.SequenceEqual(options))
        {
            var selected = NewPlanBase;
            BaseOptions = options;
            NewPlanBase = options.Any(o => o.Value == selected) ? selected
                : options.FirstOrDefault(o => o.Value == PowerSettingCatalog.BalancedPlan)?.Value ?? options.FirstOrDefault()?.Value ?? Guid.Empty;
        }

        static bool Uses(DockSenseConfig config, PowerPlanChoice choice, Guid plan) => choice.Kind switch
        {
            PowerPlanKind.Specific => choice.PlanId == plan,
            PowerPlanKind.DockSensePerformance => config.DockSensePerformancePlanId == plan,
            _ => false,
        };
    }
}

/// <summary>One plan in the list, with its editor (loaded when the card is expanded).</summary>
public sealed partial class PlanItemViewModel(Guid id, PlanListViewModel owner) : ObservableObject
{
    public Guid Id { get; } = id;

    public bool IsBuiltIn { get; } = PowerSettingCatalog.BuiltInPlans.Contains(id);

    public ObservableCollection<PlanSettingViewModel> Settings { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial string TypeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UsedByText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsThrottled { get; set; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    public partial bool CanDelete { get; set; }

    public bool HasUsedBy => UsedByText.Length > 0;

    public bool HasType => TypeText.Length > 0;

    internal void Update(string name, bool isActive, string typeText, string usedByText, bool isThrottled)
    {
        Name = name;
        IsActive = isActive;
        TypeText = typeText;
        UsedByText = usedByText;
        IsThrottled = isThrottled;
        CanDelete = !IsBuiltIn && !isActive;
        OnPropertyChanged(nameof(HasUsedBy));
        OnPropertyChanged(nameof(HasType));
        foreach (var setting in Settings)
        {
            setting.RefreshLabels();
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            Load();
        }
    }

    [RelayCommand]
    private void Revert() => Load();

    [RelayCommand]
    private void Save()
    {
        var newName = EditName.Trim();
        var rename = !IsBuiltIn && newName.Length > 0 && newName != Name ? newName : null;
        var changes = Settings.SelectMany(s => s.Changes()).ToList();
        if (rename is null && changes.Count == 0)
        {
            StatusText = Loc.T("Plans_NoChanges");
            return;
        }

        try
        {
            owner.Service.SavePlan(Id, rename, changes);
            Load();
            StatusText = Loc.T("Plans_Saved");
        }
        catch (Win32Exception ex)
        {
            StatusText = Loc.F("Plans_Error", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        var message = HasUsedBy ? Loc.F("Plans_DeleteUsedMessage", Name, UsedByText) : Loc.F("Plans_DeleteMessage", Name);
        if (!await Dialogs.ConfirmAsync(Loc.T("Plans_DeleteTitle"), message, Loc.T("Plans_Delete")))
        {
            return;
        }

        try
        {
            owner.Service.DeletePlan(Id);
        }
        catch (Win32Exception ex)
        {
            StatusText = Loc.F("Plans_Error", ex.Message);
        }
    }

    private void Load()
    {
        EditName = Name;
        StatusText = string.Empty;
        Settings.Clear();
        foreach (var definition in PowerSettingCatalog.Settings)
        {
            var ac = PowerPlans.ReadValue(Id, definition.Subgroup, definition.Setting, ac: true);
            var dc = PowerPlans.ReadValue(Id, definition.Subgroup, definition.Setting, ac: false);
            if (ac is { } acValue && dc is { } dcValue)
            {
                Settings.Add(new PlanSettingViewModel(definition, acValue, dcValue));
            }
        }
    }
}

/// <summary>One editable setting: a plugged-in and an on-battery value.</summary>
public sealed partial class PlanSettingViewModel : ObservableObject
{
    private readonly uint _originalAc;
    private readonly uint _originalDc;

    public PlanSettingViewModel(PowerSettingDefinition definition, uint ac, uint dc)
    {
        Definition = definition;
        _originalAc = ac;
        _originalDc = dc;
        AcNumber = definition.ToDisplay(ac);
        DcNumber = definition.ToDisplay(dc);
        AcChoice = ac;
        DcChoice = dc;
        RefreshLabels();
    }

    public PowerSettingDefinition Definition { get; }

    public bool IsChoice => Definition.Kind == PowerSettingKind.Choice;

    public bool IsNumber => !IsChoice;

    public double Maximum => Definition.Maximum;

    [ObservableProperty] public partial string Label { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string Unit { get; set; } = string.Empty;
    [ObservableProperty] public partial IReadOnlyList<Option<uint>> Choices { get; set; } = [];
    [ObservableProperty] public partial double? AcNumber { get; set; }
    [ObservableProperty] public partial double? DcNumber { get; set; }
    [ObservableProperty] public partial uint AcChoice { get; set; }
    [ObservableProperty] public partial uint DcChoice { get; set; }

    internal void RefreshLabels()
    {
        Label = Loc.T($"PS_{Definition.Key}");
        Description = Loc.T($"PS_{Definition.Key}_Desc");
        Unit = Definition.Kind switch
        {
            PowerSettingKind.Percent => "%",
            PowerSettingKind.Megahertz => "MHz",
            PowerSettingKind.Minutes => Loc.T("Unit_Minutes"),
            _ => string.Empty,
        };

        if (IsChoice)
        {
            // Keep a value Windows reports that is not in the list (e.g. from a newer Windows build) selectable.
            var values = Definition.Choices.Union([_originalAc, _originalDc]).Order();
            var choices = values.Select(v => new Option<uint>(v, Loc.Instance[$"PS_{Definition.Key}_{v}"] is var text && text != $"PS_{Definition.Key}_{v}" ? text : v.ToString())).ToList();
            if (!Choices.SequenceEqual(choices))
            {
                var (ac, dc) = (AcChoice, DcChoice);
                Choices = choices;
                (AcChoice, DcChoice) = (ac, dc);
            }
        }
    }

    /// <summary>The values that differ from what Windows has, as raw values ready to write.</summary>
    internal IEnumerable<(PowerSettingDefinition Setting, bool Ac, uint Value)> Changes()
    {
        if (IsChoice)
        {
            if (AcChoice != _originalAc && Definition.IsValid(AcChoice))
            {
                yield return (Definition, true, AcChoice);
            }

            if (DcChoice != _originalDc && Definition.IsValid(DcChoice))
            {
                yield return (Definition, false, DcChoice);
            }

            yield break;
        }

        if (AcNumber is { } ac && ac != Definition.ToDisplay(_originalAc))
        {
            yield return (Definition, true, Definition.ToRaw(ac));
        }

        if (DcNumber is { } dc && dc != Definition.ToDisplay(_originalDc))
        {
            yield return (Definition, false, Definition.ToRaw(dc));
        }
    }
}
