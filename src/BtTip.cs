// 蓝牙设备的悬停文案：任务栏窄条和悬浮窗共用这一份。
// 以前只有窄条里有这两段，悬浮窗要用就得复制一遍 —— 两处措辞早晚会走岔，干脆抽出来。

using System;
using System.Collections.Generic;
using System.Globalization;

namespace BatteryMonitor
{
    internal static class BtTip
    {
        // 标题：类型 · 型号（"耳机1 · QCY-T13"）
        internal static string Title(BtDevice d)
        {
            if (d == null) return "蓝牙设备";
            return string.IsNullOrEmpty(d.Model) ? d.Label : d.Label + " · " + d.Model;
        }

        // 三行：电量 / 状态（带蓝牙版本）/ 地址
        internal static string[] Lines(BtDevice d)
        {
            List<string> L = new List<string>();
            if (d == null) return L.ToArray();

            L.Add(d.Percent >= 0
                ? "电量：" + d.Percent.ToString(CultureInfo.InvariantCulture) + "%"
                : "电量：--（设备未上报）");

            // 版本要走驱动 IOCTL 查，第一次是空的，后台查到后下一拍悬停就会补上
            string ver = Sensors.BtVersion(d.Address);
            string st = "状态：已连接";
            if (ver.Length > 0) st += " · 蓝牙 " + ver;
            L.Add(st);

            if (!string.IsNullOrEmpty(d.Address)) L.Add("地址：" + d.Address);
            return L.ToArray();
        }
    }
}
