using Microsoft.UI.Xaml.Controls;
using System.Reflection;

namespace BatteryMonitor.UI.Views
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();
            VersionText.Text = "版本 " + Assembly.GetExecutingAssembly().GetName().Version;
        }
    }
}
