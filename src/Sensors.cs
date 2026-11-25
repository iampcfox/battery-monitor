// 本机电池采样：电量百分比、温度、功率、是否接通电源、是否充电中。
// 一条链路读不全就降级到下一档（CallNtPowerInformation → WMI BatteryStatus → Win32_Battery → ACPI 热区），
// 任何一项读失败都不抛异常，宁可少一个字段也不让卡片消失。
// 蓝牙部分在 Bluetooth.cs（同一个 partial 类）。

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
    internal static partial class Sensors
    {
        // 功率取值的优先级：
        //   ① CallNtPowerInformation(SystemBatteryState).Rate（毫瓦，电池类驱动填，多数 x86 笔记本有）
        //   ② root\wmi BatteryStatus 的 ChargeRate / DischargeRate（毫瓦）
        //   ③ 都没有就按"容量跳变区间"估算：Δ剩余容量(mWh) ÷ Δ时间(h) = mW
        // 这块 QCOM052D 只有 ③ 可用（①② 恒为 0），所以默认显示的是估算值。
        private const int SystemBatteryState = 5;

        // WMI 查询每次都要跨进程走 COM，慢而且会把 System.Management / wbem* 那一串 DLL
        // 一直钉在内存里。这里按变化速度分别缓存：
        //   · 电源状态 30 秒（插拔由上面 CallNtPowerInformation 每拍直读，不受影响）
        //   · 速率 60 秒，连续 5 次都读不到就彻底放弃（这台机器就是这种）
        //   · 温度 60 秒（电池温度变化以分钟计）
        private static DateTime _wmiStatusAt = DateTime.MinValue;
        private static bool _wmiStatusOk;
        private static bool _wmiOnline, _wmiCharging;
        private static DateTime _wmiRateAt = DateTime.MinValue;
        private static int _wmiRate, _wmiRateTries;
        private static DateTime _wmiTempAt = DateTime.MinValue;
        private static DateTime _acpiTempAt = DateTime.MinValue;
        private static double _wmiTemp = double.NaN;
        private static double _acpiTemp = double.NaN;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_BATTERY_STATE
        {
            public byte AcOnLine;
            public byte BatteryPresent;
            public byte Charging;
            public byte Discharging;
            public byte Spare1_0, Spare1_1, Spare1_2, Spare1_3;
            public int MaxCapacity;
            public int RemainingCapacity;
            public int Rate;              // 毫瓦，正负表示充/放
            public int EstimatedTime;
            public int DefaultAlert1;
            public int DefaultAlert2;
        }

        [DllImport("powrprof.dll")]
        private static extern uint CallNtPowerInformation(int level, IntPtr input,
            uint inputSize, IntPtr output, uint outputSize);

        public static Sample Read()
        {
            Sample s = new Sample();
            s.At = DateTime.Now;
            int rateMw = 0;            // 驱动直读的功率（毫瓦），0 = 没给
            int remainingMwh = -1;     // 剩余容量（毫瓦时），估算用

            // 0) 系统电源状态
            try
            {
                int sz = Marshal.SizeOf(typeof(SYSTEM_BATTERY_STATE));
                IntPtr buf = Marshal.AllocCoTaskMem(sz);
                try
                {
                    if (CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, buf, (uint)sz) == 0)
                    {
                        SYSTEM_BATTERY_STATE st = (SYSTEM_BATTERY_STATE)Marshal.PtrToStructure(
                            buf, typeof(SYSTEM_BATTERY_STATE));
                        if (st.BatteryPresent == 0) s.HasBattery = false;
                        if (st.AcOnLine != 0) s.AcOnline = true;
                        if (st.Charging != 0) s.Charging = true;
                        else if (st.Discharging != 0) s.Charging = false;
                        if (st.Rate > 0) rateMw = st.Rate;
                        if (st.RemainingCapacity > 0) remainingMwh = st.RemainingCapacity;
                    }
                }
                finally { Marshal.FreeCoTaskMem(buf); }
            }
            catch { }

            // 1) 基础状态
            try
            {
                PowerStatus ps = SystemInformation.PowerStatus;
                if (ps.BatteryLifePercent < 0f || ps.BatteryLifePercent > 1f) s.HasBattery = false;
                else s.Percent = (int)Math.Round(ps.BatteryLifePercent * 100.0);
                s.Charging = ps.PowerLineStatus == PowerLineStatus.Online;
                s.AcOnline = s.Charging;
                // BatteryChargeStatus 没有 Full 成员（只有 High/Low/Critical/Charging/NoSystemBattery/Unknown/None），
                // “已充满”统一由下面 Percent >= 99 && Charging 判定。
            }
            catch { }

            // 2) root\wmi BatteryStatus：电源在线与充/放状态（30 秒查一次）
            if ((s.At - _wmiStatusAt).TotalSeconds >= 30)
            {
                _wmiStatusAt = s.At;
                _wmiOnline = s.AcOnline;
                _wmiCharging = s.Charging;
                _wmiStatusOk = false;
                try
                {
                    using (ManagementObjectSearcher se = new ManagementObjectSearcher("root\\wmi",
                        "SELECT PowerOnline, Charging, Discharging FROM BatteryStatus"))
                    {
                        foreach (ManagementBaseObject mo in se.Get())
                        {
                            try
                            {
                                object online = mo["PowerOnline"];
                                if (online is bool) { _wmiOnline = (bool)online; _wmiCharging = (bool)online; _wmiStatusOk = true; }
                                object chg = mo["Charging"];
                                if (chg is bool && (bool)chg) { _wmiCharging = true; _wmiStatusOk = true; }
                                object dis = mo["Discharging"];
                                if (dis is bool && (bool)dis) { _wmiCharging = false; _wmiStatusOk = true; }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            if (_wmiStatusOk) { s.AcOnline = _wmiOnline; s.Charging = _wmiCharging; }

            // 2b) 速率字段单独查：有些机型的 BatteryStatus 不带这两个属性，整条查询会失败，
            //     所以和上面的状态查询分开写。读不到就 5 次后放弃，不再每拍空转
            if (rateMw == 0)
            {
                if (_wmiRate > 0) rateMw = _wmiRate;
                else if (_wmiRateTries < 5 && (s.At - _wmiRateAt).TotalSeconds >= 60)
                {
                    _wmiRateTries++;
                    _wmiRateAt = s.At;
                    try
                    {
                        using (ManagementObjectSearcher se = new ManagementObjectSearcher("root\\wmi",
                            "SELECT ChargeRate, DischargeRate FROM BatteryStatus"))
                        {
                            foreach (ManagementBaseObject mo in se.Get())
                            {
                                try
                                {
                                    int cr = ToInt(mo["ChargeRate"]);
                                    int dr = ToInt(mo["DischargeRate"]);
                                    if (cr > 0) { _wmiRate = cr; break; }
                                    if (dr > 0) { _wmiRate = dr; break; }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    rateMw = _wmiRate;
                }
            }

            // 3) 电池温度（60 秒查一次，缓存命中就直接用）
            if (double.IsNaN(s.TempC))
            {
                if ((s.At - _wmiTempAt).TotalSeconds >= 60)
                {
                    _wmiTempAt = s.At;
                    try
                    {
                        using (ManagementObjectSearcher se = new ManagementObjectSearcher("root\\wmi",
                            "SELECT Temperature FROM BatteryTemperature"))
                        {
                            foreach (ManagementBaseObject mo in se.Get())
                            {
                                try
                                {
                                    object t = mo["Temperature"];
                                    if (t != null)
                                    {
                                        double c = Convert.ToDouble(t) / 10.0 - 273.15;
                                        if (c > 0 && c < 80) { _wmiTemp = c; break; }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
                if (!double.IsNaN(_wmiTemp)) { s.TempC = _wmiTemp; s.TempLabel = "电池"; }
            }

            // 4) percent 兜底：Win32_Battery
            if (s.Percent < 0)
            {
                try
                {
                    using (ManagementObjectSearcher se = new ManagementObjectSearcher("root\\cimv2",
                        "SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery"))
                    {
                        foreach (ManagementBaseObject mo in se.Get())
                        {
                            try
                            {
                                object p = mo["EstimatedChargeRemaining"];
                                if (p != null) s.Percent = Convert.ToInt32(p);
                                object st = mo["BatteryStatus"];
                                if (st != null)
                                {
                                    int code = Convert.ToInt32(st);
                                    if (code == 2) s.Charging = true;
                                    if (code == 3) { s.Full = true; s.Charging = true; }
                                }
                            }
                            catch { }
                            break;
                        }
                    }
                }
                catch { }
            }

            // 5) 温度兜底：ACPI 热区（通常是 SoC / 机身），一样 60 秒查一次
            if (double.IsNaN(s.TempC) && (s.At - _acpiTempAt).TotalSeconds >= 60)
            {
                _acpiTempAt = s.At;
                try
                {
                    using (ManagementObjectSearcher se = new ManagementObjectSearcher("root\\wmi",
                        "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                    {
                        foreach (ManagementBaseObject mo in se.Get())
                        {
                            try
                            {
                                object t = mo["CurrentTemperature"];
                                if (t != null)
                                {
                                    double c = Convert.ToDouble(t) / 10.0 - 273.15;
                                    if (c > 0 && c < 120) { _acpiTemp = c; break; }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            if (double.IsNaN(s.TempC) && !double.IsNaN(_acpiTemp))
            {
                s.TempC = _acpiTemp;
                s.TempLabel = "温度";
            }

            if (s.Percent >= 99 && s.Charging) s.Full = true;

            s.Bt = ReadBtDevices();
            UpdateWatts(s, rateMw, remainingMwh);
            return s;
        }

        // 功率估算状态：只在"容量真的跳了一格"时算一次平均功率，然后把锚点移到当前时刻。
        // 绝不能用 now - 首次时刻 当分母，那样分母越来越大会把数值一路衰减到 0。
        private static double _estW = double.NaN;
        private static int _estMwh = -1;
        private static DateTime _estAt = DateTime.MinValue;
        private static bool _estWasAc;

        private static void UpdateWatts(Sample s, int rateMw, int remainingMwh)
        {
            DateTime now = DateTime.Now;

            // 插拔电源会让容量曲线拐头，旧锚点作废
            if (s.AcOnline != _estWasAc)
            {
                _estWasAc = s.AcOnline;
                _estMwh = remainingMwh > 0 ? remainingMwh : -1;
                _estAt = now;
                _estW = double.NaN;
            }

            // ① 驱动直读（毫瓦）
            if (rateMw != 0)
            {
                double w = Math.Abs(rateMw) / 1000.0;
                if (w > 0.1 && w < 400)
                {
                    s.Watts = w;
                    return;
                }
            }

            // ② 容量跳变区间法
            if (remainingMwh > 0)
            {
                if (_estMwh < 0)
                {
                    _estMwh = remainingMwh;
                    _estAt = now;
                }
                else if (remainingMwh != _estMwh)
                {
                    double min = (now - _estAt).TotalMinutes;
                    int dmwh = Math.Abs(remainingMwh - _estMwh);
                    if (min >= 0.4 && min <= 45 && dmwh >= 30)
                    {
                        double w = dmwh / (min / 60.0) / 1000.0;    // mWh ÷ h ÷ 1000 = W
                        if (w > 0.3 && w < 300) _estW = w;          // 超出 0.3~300W 当噪声丢掉
                    }
                    _estMwh = remainingMwh;   // 锚点移到这次跳变
                    _estAt = now;
                }
            }

            // 估算值只在最近一次跳变后 30 分钟内有效，过时就留空，宁可没有也不给旧数字
            if (!double.IsNaN(_estW) && (now - _estAt).TotalMinutes <= 30)
            {
                s.Watts = _estW;
            }
        }

        private static int ToInt(object v)
        {
            try
            {
                if (v == null) return 0;
                if (v is uint) return (int)(uint)v;
                return Convert.ToInt32(v);
            }
            catch { return 0; }
        }
    }
}
