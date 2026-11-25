using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Linq;

namespace BatteryMonitor.UI.Views
{
    public sealed partial class HistoryPage : Page
    {
        public HistoryPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += (s, e) => Reload();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Reload(); }

        private void Reload()
        {
            var pts = BatteryMonitor.History.Snapshot();
            var bt = BatteryMonitor.History.BtSnapshot();
            string path = BatteryMonitor.History.FilePath;
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;

            SubtitleText.Text = "共 " + pts.Count + " 条记录 · 覆盖 " + bt.Select(p => p.Id).Distinct().Count()
                + " 台设备 · " + DateTime.Now.ToString("HH:mm:ss") + " 更新";
            KpiSize.Text = (bytes / 1024.0).ToString("0") + " KB";
            KpiSizeSub.Text = Path.GetFileName(path);
            KpiCount.Text = pts.Count.ToString();
            KpiDevices.Text = bt.Select(p => p.Id).Distinct().Count().ToString();
            if (pts.Count > 0)
            {
                var first = pts.Min(p => p.Time);
                KpiEarliest.Text = first.ToString("MM-dd");
                KpiEarliestSub.Text = first.ToString("HH:mm");
            }
            else { KpiEarliest.Text = "—"; KpiEarliestSub.Text = "无数据"; }

            DeviceRows.Children.Clear();
            foreach (var g in bt.GroupBy(p => p.Id).OrderByDescending(g => g.Count()))
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = g.First().Label,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                var count = new TextBlock
                {
                    Text = g.Count() + " 条  " + g.Min(p => p.Time).ToString("MM-dd HH:mm")
                        + " ~ " + g.Max(p => p.Time).ToString("MM-dd HH:mm"),
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                var del = new Button { Content = "删除", VerticalAlignment = VerticalAlignment.Center };
                del.Click += (s, e) => DeleteDevice(g.Key);

                row.Children.Add(chk);
                row.Children.Add(name); Grid.SetColumn(name, 1);
                row.Children.Add(count); Grid.SetColumn(count, 2);
                row.Children.Add(del); Grid.SetColumn(del, 3);
                DeviceRows.Children.Add(row);
            }
        }

        private void DeleteDevice(string id)
        {
            var dlg = new ContentDialog
            {
                Title = "删除设备历史",
                Content = "确定删除该设备的全部记录？此操作不可恢复。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            dlg.PrimaryButtonClick += async (s, e) =>
            {
                // 直接改写 bt csv：过滤掉该 id 的行
                string path = BatteryMonitor.History.BtFilePath;
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path).Where(l => !l.Contains("," + id + ",")).ToArray();
                    File.WriteAllLines(path, lines);
                    BatteryMonitor.History.Init(BatteryMonitor.History.FilePath,
                        BatteryMonitor.History.BtFilePath,
                        BatteryMonitor.Config.Load().HistoryDays);
                }
                Reload();
                await System.Threading.Tasks.Task.CompletedTask;
            };
            _ = dlg.ShowAsync();
        }
    }
}
