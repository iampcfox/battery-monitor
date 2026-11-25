using Microsoft.UI.Xaml;
using System;

namespace BatteryMonitor.UI
{
    public partial class App : Application
    {
        public static IntPtr MainHwnd;

        public App()
        {
            InitializeComponent();

            // 历史文件初始化要读 CSV + 解析，放到线程池，别挡启动（否则窗口要等文件读完才出来）。
            // 各页面都是"先加载、后读数据"，晚一点 Init 完也不影响：Snapshot 返回当时的快照。
            System.Threading.Tasks.Task.Run(delegate
            {
                try
                {
                    var cfg = BatteryMonitor.Config.Load();
                    BatteryMonitor.History.Init(BatteryMonitor.AppPaths.Dir + "\\battery-monitor-history.csv",
                        BatteryMonitor.AppPaths.Dir + "\\battery-monitor-history-bt.csv", cfg.HistoryDays);
                }
                catch { }
            });
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var window = new MainWindow();
            window.Activate();
            // 必须在 Activate 之后 Resize，否则 AppWindow 未就绪，窗口尺寸会是 0
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 660));
            MainHwnd = window.GetWindowHandle();
        }
    }
}
