using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace BatteryMonitor.UI.Views
{
    public sealed partial class DevicesPage : Page
    {
        private DispatcherTimer _timer;
        private bool _busy;              // 采集在跑时不再叠一次，否则后台线程会被 WMI 拖住越积越多
        private bool _loadedOnce;

        public DevicesPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += DevicesPage_Loaded;
            Unloaded += DevicesPage_Unloaded;
        }

        private void DevicesPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 页面是 Required 缓存的，来回切页会重复触发 Loaded，定时器只能建一次
            if (_timer == null)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _timer.Tick += (s, args) => _ = RefreshAsync();
            }
            _timer.Start();
            if (!_loadedOnce) { _loadedOnce = true; _ = RefreshAsync(); }
        }

        private void DevicesPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_timer != null) _timer.Stop();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { _ = RefreshAsync(); }

        // 关键：Sensors.Read() 里有多条 WMI 查询 + 蓝牙 WinRT 调用（每台设备最长等 400ms），
        // 在 UI 线程上同步跑会直接把窗口卡成"未响应"。全部丢到线程池，回来后只做 UI 赋值。
        private async Task RefreshAsync()
        {
            if (_busy) return;
            _busy = true;
            try
            {
                SubtitleText.Text = "正在读取…";

                Sample s = null;
                string error = null;
                await Task.Run(delegate
                {
                    try { s = BatteryMonitor.Sensors.Read(); }
                    catch (Exception ex) { error = ex.Message; }
                });

                if (error != null) { SubtitleText.Text = "读取失败：" + error; return; }
                if (s == null) return;

                SubtitleText.Text = "本机 · 更新于 " + s.At.ToString("HH:mm:ss");
                DeviceList.Children.Clear();

                DeviceList.Children.Add(MakeCard(
                    "本机电池",
                    s.Percent >= 0 ? s.Percent + "%" : "—",
                    (BatteryMonitor.ChargeVisual.Powered(s) ? "已接通电源" : "电池供电")
                        + (double.IsNaN(s.TempC) ? "" : " · " + s.TempC.ToString("0") + "℃")
                        + (double.IsNaN(s.Watts) ? "" : " · " + s.Watts.ToString("0.0") + " W")));

                int btCount = 0;
                if (s.Bt != null)
                {
                    foreach (var b in s.Bt)
                    {
                        btCount++;
                        DeviceList.Children.Add(MakeCard(
                            string.IsNullOrEmpty(b.Model) ? b.Label : b.Model,
                            b.Percent >= 0 ? b.Percent + "%" : "连接中",
                            (b.Le ? "低功耗蓝牙" : "经典蓝牙")
                                + (string.IsNullOrEmpty(b.Address) ? "" : " · " + b.Address)));
                    }
                }

                if (btCount == 0)
                {
                    DeviceList.Children.Add(new TextBlock
                    {
                        Text = "没有已连接并上报电量的蓝牙设备",
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        FontSize = 12,
                        Margin = new Thickness(4, 4, 0, 0)
                    });
                }
            }
            finally { _busy = false; }
        }

        private static Border MakeCard(string title, string value, string note)
        {
            var border = new Border
            {
                Style = (Style)Application.Current.Resources["AppCardStyle"],
                Padding = new Thickness(16, 12, 16, 12)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            left.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 12,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            grid.Children.Add(left);

            var pct = new TextBlock
            {
                Text = value,
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pct, 1);
            grid.Children.Add(pct);

            border.Child = grid;
            return border;
        }
    }
}
