using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using System;

namespace BatteryMonitor.UI
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 尺寸在 Activate 之后设（App.OnLaunched），构造期 AppWindow 尚未就绪，Resize 会无效
            Nav.SelectedItem = Nav.MenuItems[0];
            ContentFrame.Navigate(typeof(Views.DevicesPage));
        }

        public IntPtr GetWindowHandle()
        {
            return Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem is not NavigationViewItem item || item.Tag is not string tag) return;
            Type page = tag switch
            {
                "stats" => typeof(Views.StatsPage),
                "history" => typeof(Views.HistoryPage),
                "settings" => typeof(Views.SettingsPage),
                "about" => typeof(Views.AboutPage),
                _ => typeof(Views.DevicesPage),
            };
            ContentFrame.Navigate(page);
            ContentFrame.BackStack.Clear();
        }
    }
}
