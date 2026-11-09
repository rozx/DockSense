using System.Windows.Controls;
using DockSense.App.Localization;
using Wpf.Ui.Controls;

namespace DockSense.App.Services;

/// <summary>In-window confirmation dialogs (WPF-UI ContentDialog), hosted by the main window.</summary>
public static class Dialogs
{
    /// <summary>Set by the main window once it exists.</summary>
    public static ContentDialogHost? Host { get; set; }

    /// <summary>Asks the user to confirm. Returns false when cancelled or when no window is open.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        if (Host is null)
        {
            return false;
        }

        var dialog = new ContentDialog(Host)
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirmText,
            CloseButtonText = Loc.T("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
