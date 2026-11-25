// 跨文件共用的轻量类型：字段和枚举为主，另加一个 ChargeVisual —— 三处画面共用的
// "算不算在充电"判定，放这里是因为它只依赖 Sample，不该让渲染三边互相依赖。

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
    // 蓝牙设备类型：决定卡片上的名字和行首那个小图标
    internal enum BtKind
    {
        Unknown = 0,
        Headphone = 1,
        Keyboard = 2,
        Mouse = 3,
        Gamepad = 4,
        Speaker = 5      // 蓝牙音箱 / 条形音响：能报电量的就照常显示
    }

    // 一台蓝牙设备的电量（耳机 / 键盘 / 手柄…）
    internal sealed class BtDevice
    {
        public string Label = "";     // 卡片上显示的类型名（同类多台时带序号）
        public string Address = "";   // BD_ADDR，身份标识兼排序依据
        public string Model = "";     // 型号名：设置面板标题、历史页签都用它
        public BtKind Kind = BtKind.Unknown;
        public int Percent = -1;      // -1 = 已连接但系统没上报电量（手柄常见）
        public int Index;             // 同类多台时的序号（1、2…），0 = 只此一台、不编号
        public string Protocols = ""; // 服务协议：A2DP/HFP/AVRCP/HID…（设备树里那些服务节点的 GUID 反推）
        public string Container = "";  // 设备容器 ID：跟音频端点配对、取"这台设备自己的音量"要用
        public bool Le;               // true = 低功耗蓝牙，false = 经典蓝牙
    }

    // 卡片上的一行信息（温度 / 状态 / 蓝牙设备），带行首图标
    internal enum RowIcon
    {
        None = 0,
        Thermo = 1,
        Battery = 2,
        Headphone = 3,
        Keyboard = 4,
        Mouse = 5,
        Bolt = 6,
        Gamepad = 7,
        Speaker = 8
    }

    internal sealed class InfoRow
    {
        public string Text = "";
        public string Value = "";     // 行尾的值（"85%" / "已连接"）：靠右排，蓝牙行装进电池胶囊
        public RowIcon Icon = RowIcon.None;
        public int Percent = -1;      // 行首电池图标内部的填充比例；蓝牙行也是胶囊的填充依据
        public bool Bt;               // true = 蓝牙设备行（缩进画图标，和本机信息隔开一档）
        public int Volume = -1;       // 耳机行的当前音量（0~100）；-1 = 不显示（键盘 / 鼠标 / 手柄没有）

        public InfoRow() { }

        public InfoRow(string text, RowIcon icon)
        {
            Text = text;
            Icon = icon;
        }
    }

    internal sealed class Sample
    {
        public bool HasBattery = true;
        public int Percent = -1;
        public bool Charging = false;
        public bool Full = false;
        public double TempC = double.NaN;
        public string TempLabel = "电池";
        public bool AcOnline = false;         // 适配器是否接着（掉电图标就看它）
        public double Watts = double.NaN;     // 电池侧功率（W）；NaN = 暂时算不出来
        public DateTime At = DateTime.Now;    // 这次采样的时刻（设置面板显示"更新于"）
        public List<BtDevice> Bt = new List<BtDevice>();   // 已连接的蓝牙设备电量，空 = 没连或没上报
    }

    // "现在算不算在充电"这件事，三处画面（悬浮窗口 / 任务栏窗口 / 托盘图标）必须问同一个地方。
    // 以前各写各的：卡片看 AcOnline、窄条看 Full||Charging、托盘看 Charging&&!Full，
    // 结果"已充满"那一瞬间卡片是绿的、托盘还是蓝的，插着电停充时又反着来。
    internal static class ChargeVisual
    {
        // 接着电源（含已充满）：决定电量填充/强调色走不走绿色。
        // 用 AcOnline 而不是 Charging —— 有些机器（限充到 80% 的、慢充的）插着电但电池暂停充电，
        // 这时 Charging 是 false，可用户眼里"接着电源"就是接上了，颜色该跟着电源走
        public static bool Powered(Sample s)
        {
            return s != null && s.HasBattery && (s.AcOnline || s.Charging || s.Full);
        }

        // 真的还在往里进电：只有这个状态画闪电。
        // 已充满还画闪电，看着像"还在充"；插着电但电池停充（限充机型）也一样不该闪
        public static bool Bolt(Sample s)
        {
            return s != null && s.HasBattery && s.Charging && !s.Full;
        }

        // 已充满（插着电、电量到顶）：强调色再深一档，跟"正在充"区分开
        public static bool Full(Sample s)
        {
            return s != null && s.HasBattery && s.Full;
        }
    }
}
