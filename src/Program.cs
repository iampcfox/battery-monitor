// 电量悬浮窗 —— 程序入口
// 单实例互斥 + 读配置 + 起窗口。其余实现按职责拆在各文件里：
//   Config.cs       ini 配置与默认值
//   Types.cs        跨模块共用的数据结构（Sample / BtDevice / InfoRow …）
//   Sensors.cs      本机电池：电量、温度、功率、充放电状态
//   Bluetooth.cs    蓝牙设备：枚举、聚合、类型推断、连接状态、电量
//   History.cs      历史采样落盘与读取（统计窗口用）
//   FloatForm.cs    卡片窗口：生命周期、菜单、拖动、位置、定时器
//   CardRender.cs   卡片绘制：配色、文字排版、矢量图标、分层窗口提交
//   ChartView.cs    电量折线控件（设置面板的历史统计页用它）
//   SettingsForm.cs 设置面板：左导航 + 设备电量 / 历史统计 / 历史管理 / 设置
//   TrayIcon.cs     托盘图标（原 battery-tray 项目并进来，任务栏右下角数字电量）
//   TaskbarStrip.cs 任务栏窄条：SetParent 进 Shell_TrayWnd，贴在托盘区左边的电量条
//   AppHost.cs      宿主：把卡片、托盘、任务栏窄条装进同一个进程、同一条消息循环

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // 兜底：窗口线程里没接住的异常会直接把进程带走（winexe 连报错都看不见），
            // 所以先把它们写进 crash.log 再退出。没有这个，程序"闪一下就不见了"时
            // 只能靠猜是哪一步炸的。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CrashLog(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException +=
                delegate(object s, UnhandledExceptionEventArgs e) { CrashLog(e.ExceptionObject as Exception); };

            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\BatteryMonitorSingleton", out created))
            {
                if (!created) { CrashLog(new Exception("已有实例在跑，本次启动直接退出（单实例互斥）")); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                AppPaths.Migrate();            // battery-float.* → battery-monitor.*（只搬一次）
                FloatForm.MigrateAutoStart();  // 开机自启动的注册表值也改名，不然会静默失效
                Config cfg = Config.Load();
                if (cfg.ShowEmptyBt) Sensors.ShowAllBt = true;
                // 主题要在任何窗口创建之前定下来：Ui 的颜色是全局静态值，控件一创建就用它
                Ui.ApplyTheme(cfg.Theme == 2 || (cfg.Theme == 0 && Ui.SystemIsDark()));
                cfg.BgTopR = Ui.CardTop.R; cfg.BgTopG = Ui.CardTop.G; cfg.BgTopB = Ui.CardTop.B;
                cfg.BgBotR = Ui.CardBottom.R; cfg.BgBotG = Ui.CardBottom.G; cfg.BgBotB = Ui.CardBottom.B;
                // 统一走宿主：卡片 +（可选的）托盘图标，一套采样一份配置。
                // ini 里写 tray=false 就只起卡片，之后在"设置"里勾上托盘也能立刻长出来
                AppHost host = new AppHost(cfg);
                if (args != null && args.Length > 0 && args[0] == "-panel") host.DebugOpenPanel();
                Application.Run(host);
            }
        }

        // 临时诊断用的轨迹：程序"自己不见了"这种问题，光看有没有异常分不清是
        // 崩溃、互斥退出还是消息循环正常结束，所以把几个关键节点写进同一个文件
        internal static void Trace(string msg)
        {
            try
            {
                string path = Path.Combine(AppPaths.Dir, "battery-monitor-crash.log");
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        private static void CrashLog(Exception e)
        {
            try
            {
                string path = Path.Combine(AppPaths.Dir, "battery-monitor-crash.log");
                string text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  "
                    + (e == null ? "(null)" : e.ToString()) + Environment.NewLine;
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
