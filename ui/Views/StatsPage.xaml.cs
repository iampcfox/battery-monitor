using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BatteryMonitor.UI.Views
{
    public sealed partial class StatsPage : Page
    {
        private int _hours = 24;
        private List<BatteryMonitor.History.Point> _points = new List<BatteryMonitor.History.Point>();

        public StatsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += (s, e) => Reload();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) { Reload(); }

        private void Range_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton clicked) return;
            foreach (var tb in new[] { Range24, Range168, Range720 })
                tb.IsChecked = ReferenceEquals(tb, clicked);
            _hours = int.Parse((string)clicked.Tag);
            RangeText.Text = "最近 " + (_hours == 24 ? "24 小时" : _hours == 168 ? "7 天" : "30 天");
            Reload();
        }

        private void Reload()
        {
            var all = BatteryMonitor.History.Snapshot();
            var from = DateTime.Now.AddHours(-_hours);
            _points = all.Where(p => p.Time >= from && p.Percent >= 0).ToList();
            SubtitleText.Text = "共 " + _points.Count + " 个采样点 · " + DateTime.Now.ToString("MM-dd HH:mm") + " 更新";

            if (_points.Count == 0)
            {
                KpiCurrent.Text = KpiMax.Text = KpiMin.Text = KpiAvg.Text = "—";
                KpiCurrentSub.Text = KpiMaxSub.Text = KpiMinSub.Text = KpiAvgSub.Text = "无数据";
                ChartCanvas.Children.Clear();
                return;
            }

            int cur = _points.Last().Percent;
            int max = _points.Max(p => p.Percent);
            int min = _points.Min(p => p.Percent);
            double avg = _points.Average(p => p.Percent);
            var maxPt = _points.First(p => p.Percent == max);
            var minPt = _points.First(p => p.Percent == min);

            KpiCurrent.Text = cur + "%";
            KpiCurrentSub.Text = _points.Last().Time.ToString("MM-dd HH:mm");
            KpiMax.Text = max + "%";
            KpiMaxSub.Text = maxPt.Time.ToString("MM-dd HH:mm");
            KpiMin.Text = min + "%";
            KpiMinSub.Text = minPt.Time.ToString("MM-dd HH:mm");
            KpiAvg.Text = avg.ToString("0") + "%";
            KpiAvgSub.Text = "掉电 " + (max - cur) + " 点";

            DrawChart();
        }

        private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) { DrawChart(); }

        private void DrawChart()
        {
            ChartCanvas.Children.Clear();
            double w = ChartCanvas.ActualWidth, h = ChartCanvas.ActualHeight;
            if (w < 40 || h < 40 || _points.Count == 0) return;

            // 网格线：0/20/40/60/80/100%
            for (int v = 0; v <= 100; v += 20)
            {
                double y = h - 24 - (h - 40) * v / 100.0;
                var line = new Line
                {
                    X1 = 36, Y1 = y, X2 = w - 8, Y2 = y,
                    Stroke = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
                    StrokeThickness = 1
                };
                ChartCanvas.Children.Add(line);
                var label = new TextBlock
                {
                    Text = v + "%",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
                };
                Canvas.SetLeft(label, 2); Canvas.SetTop(label, y - 8);
                ChartCanvas.Children.Add(label);
            }

            // 折线
            var t0 = _points.First().Time;
            var t1 = _points.Last().Time;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);
            double plotW = w - 44, plotH = h - 40;
            var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            for (int i = 0; i < _points.Count - 1; i++)
            {
                var a = _points[i]; var b = _points[i + 1];
                double x1 = 36 + (a.Time - t0).TotalSeconds / span * plotW;
                double y1 = h - 24 - a.Percent / 100.0 * plotH;
                double x2 = 36 + (b.Time - t0).TotalSeconds / span * plotW;
                double y2 = h - 24 - b.Percent / 100.0 * plotH;
                ChartCanvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = accent, StrokeThickness = 2 });
            }
            // 末点
            var last = _points.Last();
            double lx = 36 + (last.Time - t0).TotalSeconds / span * plotW;
            double ly = h - 24 - last.Percent / 100.0 * plotH;
            var dot = new Ellipse { Width = 6, Height = 6, Fill = accent };
            Canvas.SetLeft(dot, lx - 3); Canvas.SetTop(dot, ly - 3);
            ChartCanvas.Children.Add(dot);
        }
    }
}
