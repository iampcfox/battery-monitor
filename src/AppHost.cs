// 宿主：把"卡片"和"托盘图标"合成一个进程、一套采样、一份配置。
// 为什么要有它：卡片是穿透的分层窗口，不适合当主窗体（关掉就等于退出整个程序），
// 而托盘图标要能在卡片隐藏时继续活着（继续采样、继续记历史、点一下把卡片叫回来）。
// 所以消息循环交给 ApplicationContext，两个部件都只是它管的对象。

using System;
using System.Windows.Forms;

namespace BatteryMonitor
{
    internal sealed class AppHost : ApplicationContext
    {
        private readonly Config _cfg;
        private readonly FloatForm _card;
        private TrayIcon _tray;
        private TaskbarStrip _strip;
        private bool _quitting;

        public AppHost(Config cfg)
        {
            _cfg = cfg;
            _card = new FloatForm(cfg);
            _card.QuitHook = Quit;              // 卡片菜单里的"退出"要连托盘一起收
            _card.TrayHook = ApplyTray;         // 设置页勾/取消托盘图标就地生效
            _card.StripHook = ApplyStrip;       // 任务栏窄条同理
            _card.StripRefresh = delegate { if (_strip != null) { try { _strip.Poke(); } catch { } } };
            // 采样完立刻把托盘和窄条一起推一下：三处画面在同一拍里换状态，
            // 不会出现"卡片绿了、托盘还蓝着""窄条闪了、卡片还没闪"这种错位
            _card.SampleChanged = delegate
            {
                if (_tray != null) { try { _tray.RefreshNow(); } catch { } }
                if (_strip != null) { try { _strip.Poke(); } catch { } }
            };
            _card.TaskbarCreatedHook = ResetStrip;   // explorer 重启后任务栏换了个窗口，要重新挂
            // 卡片被别的途径关掉（系统注销、任务栏"关闭窗口"）就别留一个看不见的进程
            _card.FormClosed += delegate { if (!_quitting) Quit(); };
            // 先把卡片显示出来：OnShown 里会立刻采一次样，托盘随后就有真数据，
            // 否则托盘头 5 秒会先画一个"AC"灰图标
            _card.Show();
            if (cfg.Tray) _tray = new TrayIcon(cfg, _card);
            if (cfg.Taskbar) _strip = new TaskbarStrip(cfg, _card);
            if (_strip != null) _strip.Show();
        }

        // on = true 建一个托盘图标，false = 收掉；重复调用同方向不做事
        private void ApplyTray(bool on)
        {
            if (on == (_tray != null)) return;
            if (on)
            {
                _tray = new TrayIcon(_cfg, _card);
            }
            else
            {
                TrayIcon t = _tray;
                _tray = null;
                try { t.Dispose(); } catch { }
            }
        }

        // 任务栏窄条：开 = new 一个并显示，关 = 收掉。同方向重复调用不做事
        private void ApplyStrip(bool on)
        {
            if (on == (_strip != null)) return;
            if (on)
            {
                _strip = new TaskbarStrip(_cfg, _card);
                _strip.Show();
            }
            else
            {
                TaskbarStrip s = _strip;
                _strip = null;
                try { s.Dispose(); } catch { }
            }
        }

        // 命令行 -panel：启动后直接打开设置面板（调排版、截图对账时用，正常启动走托盘/卡片菜单）
        internal void DebugOpenPanel()
        {
            _card.OpenPanel(SettingsForm.PAGE_DEVICES);
        }

        private void ResetStrip()
        {
            if (_strip == null) return;
            try { _strip.Reset(); } catch { }
        }

        internal void Quit()
        {
            if (_quitting) return;          // 两个入口（卡片菜单 / 托盘菜单）都可能按退出
            _quitting = true;
            if (_strip != null) { try { _strip.Dispose(); } catch { } }
            if (_tray != null) { try { _tray.Dispose(); } catch { } }
            try { _card.Close(); } catch { }
            ExitThread();
        }
    }
}
