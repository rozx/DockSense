namespace DockSense.Core;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Look-and-feel and notification preferences.</summary>
public sealed record UiSettings
{
    /// <summary>"system" (follow Windows), "en" or "zh-Hans".</summary>
    public string Language { get; init; } = "system";

    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>Show a Windows notification when the mode changes.</summary>
    public bool NotifyOnModeChange { get; init; } = true;

    /// <summary>Show a Windows notification when DockSense starts a program configured for a mode.</summary>
    public bool NotifyOnProgramStart { get; init; } = true;

    /// <summary>Show a Windows notification for problems (e.g. another program keeps changing the power plan).</summary>
    public bool NotifyOnWarnings { get; init; } = true;
}
