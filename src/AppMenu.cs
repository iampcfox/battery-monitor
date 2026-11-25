// 右键菜单的唯一一份定义。
//
// 三处入口：卡片（Ctrl + 右键）、任务栏窄条（右键）、托盘图标（右键）。
// 以前是两份代码各写一遍 —— 卡片/窄条走 Win32 TrackPopupMenu，托盘走 WinForms
// ContextMenuStrip，结果托盘多一项"电源和电池设置"，顺序也略有出入，改一处忘一处
// 就开始漂。现在内容只在 Build() 里写一次，两边各自只负责"怎么画出来"。
//
// 三处现在统一用 FloatForm.ShowCardMenu() 的 Win32 原生菜单：勾选是系统打勾、
// 分隔线也是系统样式，托盘不再用 WinForms ContextMenuStrip（那个是蓝框勾选的方角菜单）。
// 条目、顺序、勾选、可用性、动作全部只在 Build() 里写一次。

using System;
using System.Collections.Generic;

namespace BatteryMonitor
{
    internal static class AppMenu
    {
        internal sealed class Item
        {
            public string Text;             // null = 分隔线
            public Func<bool> Checked;      // 非 null = 勾选项，返回当前状态
            public Func<bool> Enabled;      // null = 始终可用
            public bool OpensWindow;        // 选中后会开一个新窗口（弹菜单前抢的前台要还回去）
            public Action Do;
        }

        // 顺序即菜单顺序：显示哪个窗口 → 打开哪个面板 → 启动 → 系统入口 → 退出
        internal static List<Item> Build(FloatForm card)
        {
            Config cfg = card.Cfg;
            List<Item> l = new List<Item>();

            l.Add(new Item
            {
                Text = "显示悬浮窗口",
                Checked = delegate { return card.CardVisible; },
                // 托盘关着时不给藏：藏了就没有入口叫回来，所以灰掉
                Enabled = delegate { return cfg.Tray; },
                Do = delegate { card.ToggleCardVisible(); }
            });
            l.Add(new Item
            {
                Text = "显示任务栏窗口",
                Checked = delegate { return cfg.Taskbar; },
                Do = delegate { card.ToggleTaskbarItem(); }
            });
            l.Add(new Item
            {
                Text = "横向排列",
                Checked = delegate { return cfg.Horizontal; },
                Do = delegate { card.ToggleHorizontal(); }
            });

            l.Add(new Item());

            l.Add(new Item
            {
                Text = "打开面板",
                OpensWindow = true,
                Do = delegate { card.OpenPanel(SettingsForm.PAGE_DEVICES); }
            });

            l.Add(new Item());

            l.Add(new Item
            {
                Text = "开机自启动",
                Checked = delegate { return FloatForm.AutoStartOn; },
                Do = delegate { FloatForm.SetAutoStartOn(!FloatForm.AutoStartOn); }
            });

            l.Add(new Item());

            l.Add(new Item
            {
                Text = "电源和电池设置",
                Do = delegate { OpenPowerSettings(); }
            });
            l.Add(new Item
            {
                Text = "退出",
                Do = delegate { card.Quit(); }
            });

            return l;
        }

        // Win11 的"电源和电池"设置页；老系统没有 ms-settings 协议就退回控制面板
        internal static void OpenPowerSettings()
        {
            try { System.Diagnostics.Process.Start("ms-settings:batterysaver"); }
            catch
            {
                try { System.Diagnostics.Process.Start("control.exe", "/name Microsoft.PowerOptions"); }
                catch { }
            }
        }
    }
}
