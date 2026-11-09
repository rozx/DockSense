using System.Windows;
using DockSense.App.ViewModels;
using DockSense.App.Views.Pages;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;

namespace DockSense.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        RootNavigation.SetPageProviderService(new PageProvider(viewModel));
        Services.Dialogs.Host = DialogHost;

        // The NavigationView selects an item on its own while loading; open the Status page once the window has
        // rendered and everything is idle, so ours is the last navigation.
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(
            () => RootNavigation.Navigate(typeof(DashboardPage)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>One instance per page, all sharing the main view model.</summary>
    private sealed class PageProvider(MainViewModel viewModel) : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, FrameworkElement> _pages = [];

        public object? GetPage(Type pageType)
        {
            if (!_pages.TryGetValue(pageType, out var page))
            {
                page = (FrameworkElement)Activator.CreateInstance(pageType)!;
                page.DataContext = viewModel;
                _pages[pageType] = page;
            }

            return page;
        }
    }
}
