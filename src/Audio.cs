// 当前系统音量：卡片上耳机行最右边那个"小喇叭 + 数字"。
//
// 为什么走 CoreAudio 而不是读注册表 / 找别的键：Windows 的音量不是"每个设备一个键"，
// 唯一的正规出口是默认播放设备的 IAudioEndpointVolume。注册表里那套（各种 AudioEndpoint
// 子键）既不稳定也不含实时音量，读出来跟实际听到的对不上。
//
// 接口只声明到第 7 个槽：COM 的 vtable 是按"声明顺序"对位的，前 N 个方法顺序对得上，
// 后面用不到的方法就不必声明（少写一堆 SetXxx / 通道音量）。方法全部 [PreserveSig]，
// 失败返回 HRESULT 而不是抛异常 —— 声卡被独占、没有播放设备时这里要安静地给出 -1。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryMonitor
{
    internal static class Audio
    {
        private const int eRender = 0;      // EDataFlow.eRender
        private const int eConsole = 0;     // ERole.eConsole
        private const int CLSCTX_ALL = 23;

        private static readonly Guid IID_IAudioEndpointVolume =
            new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        // 设备属性（名字、容器 ID）都在 property store 里。PROPVARIANT 只按需取两种：
        //   VT_LPWSTR（31）= 字符串、VT_CLSID（72）= GUID
        // 结构体用显式布局对位：vt 占前 2 字节，联合体在偏移 8（x86/x64 都是 8）
        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptr;
            [FieldOffset(8)] public Guid guid;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint props);
            [PreserveSig] int GetAt(uint prop, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pv);

        private static readonly PROPERTYKEY PkeyFriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            pid = 14
        };

        // {8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C} pid=2：跟 Bluetooth.cs 里判设备容器用的是同一个键，
        // 蓝牙耳机这一整台设备（含它的音频端点）共享一个容器 ID —— 靠它把"设备"和"音频端点"对上
        private static readonly PROPERTYKEY PkeyContainerId = new PROPERTYKEY
        {
            fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
            pid = 2
        };

        private const int STGM_READ = 0;
        private const int DEVICE_STATE_ACTIVE = 0x00000001;
        private const ushort VT_LPWSTR = 31;
        private const ushort VT_CLSID = 72;

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float level);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        }

        // 枚举器建一次就留着（每秒 Activate 一次没必要，也会把音频服务反复唤醒）
        private static IMMDeviceEnumerator _enum;
        private static int _last = -1;
        private static DateTime _at = DateTime.MinValue;
        private const int CacheMs = 1500;

        // 返回 0~100；拿不到（没播放设备 / 调用失败）返回 -1，调用方就不画这一块
        internal static int VolumePercent()
        {
            DateTime now = DateTime.Now;
            if (_at != DateTime.MinValue && (now - _at).TotalMilliseconds < CacheMs) return _last;
            _at = now;
            _last = Read();
            return _last;
        }

        private static int Read()
        {
            IMMDevice dev = null;
            try
            {
                if (_enum == null) _enum = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                if (_enum.GetDefaultAudioEndpoint(eRender, eConsole, out dev) != 0 || dev == null)
                    return -1;
                return ReadVolume(dev);
            }
            catch
            {
                _enum = null;      // 音频服务重启过就把枚举器丢掉，下一拍重建
                return -1;
            }
            finally
            {
                if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } }
            }
        }

        // 取某个音频端点自己的主音量（0~100），失败 -1
        private static int ReadVolume(IMMDevice dev)
        {
            object iface = null;
            try
            {
                Guid iid = IID_IAudioEndpointVolume;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out iface) != 0 || iface == null)
                    return -1;
                float v;
                if (((IAudioEndpointVolume)iface).GetMasterVolumeLevelScalar(out v) != 0) return -1;
                int pct = (int)Math.Round(v * 100f);
                if (pct < 0) pct = 0;
                if (pct > 100) pct = 100;
                return pct;
            }
            catch { return -1; }
            finally
            {
                if (iface != null) { try { Marshal.ReleaseComObject(iface); } catch { } }
            }
        }

        // ── 按设备取音量 ────────────────────────────────────────────────────
        // 以前只有一个"系统默认播放设备"的音量，两台耳机都被写成同一个数。
        // Windows 的音量是**每个播放端点各自一份**（蓝牙音箱 100、耳机 30 可以完全不同），
        // 所以这里枚举所有活动播放端点，用设备容器 ID 把"蓝牙设备"和"它的音频端点"配对，
        // 再读那个端点自己的音量。
        private sealed class Endpoint
        {
            public string Id = "";
            public string Name = "";
            public string Container = "";
        }

        private static List<Endpoint> _eps = new List<Endpoint>();
        private static DateTime _epsAt = DateTime.MinValue;
        private const int EpCacheMs = 5000;

        private static List<Endpoint> Endpoints()
        {
            DateTime now = DateTime.Now;
            if (_epsAt != DateTime.MinValue && (now - _epsAt).TotalMilliseconds < EpCacheMs) return _eps;
            _epsAt = now;
            List<Endpoint> list = new List<Endpoint>();
            IMMDeviceCollection coll = null;
            try
            {
                if (_enum == null) _enum = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                if (_enum.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out coll) != 0 || coll == null)
                {
                    _eps = list;
                    return list;
                }
                int n;
                if (coll.GetCount(out n) != 0) { _eps = list; return list; }
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev = null;
                    try
                    {
                        if (coll.Item(i, out dev) != 0 || dev == null) continue;
                        Endpoint e = new Endpoint();
                        string id;
                        if (dev.GetId(out id) == 0) e.Id = id ?? "";
                        ReadProps(dev, e);
                        if (e.Id.Length > 0) list.Add(e);
                    }
                    finally
                    {
                        if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } }
                    }
                }
            }
            catch { }
            finally
            {
                if (coll != null) { try { Marshal.ReleaseComObject(coll); } catch { } }
            }
            _eps = list;
            return list;
        }

        // 读一个端点的"友好名"和"容器 ID"
        private static void ReadProps(IMMDevice dev, Endpoint e)
        {
            IntPtr psPtr = IntPtr.Zero;
            IPropertyStore ps = null;
            try
            {
                if (dev.OpenPropertyStore(STGM_READ, out psPtr) != 0 || psPtr == IntPtr.Zero) return;
                ps = (IPropertyStore)Marshal.GetObjectForIUnknown(psPtr);

                PROPVARIANT pv;
                PROPERTYKEY k = PkeyFriendlyName;
                if (ps.GetValue(ref k, out pv) == 0)
                {
                    if (pv.vt == VT_LPWSTR && pv.ptr != IntPtr.Zero) e.Name = Marshal.PtrToStringUni(pv.ptr) ?? "";
                    PropVariantClear(ref pv);
                }

                k = PkeyContainerId;
                if (ps.GetValue(ref k, out pv) == 0)
                {
                    if (pv.vt == VT_CLSID) e.Container = GuidHex(pv.guid);
                    PropVariantClear(ref pv);
                }
            }
            catch { }
            finally
            {
                if (ps != null) { try { Marshal.ReleaseComObject(ps); } catch { } }
                if (psPtr != IntPtr.Zero) { try { Marshal.Release(psPtr); } catch { } }
            }
        }

        // 跟 Bluetooth.cs 里 DeviceContainerId 同一种写法：16 字节顺序拼十六进制，不带横线
        private static string GuidHex(Guid g)
        {
            byte[] b = g.ToByteArray();
            StringBuilder sb = new StringBuilder(32);
            for (int i = 0; i < b.Length; i++) sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

        // container：蓝牙设备的容器 ID（对得上就是同一个物理设备）
        // model：兜底用，端点名里一般就带着设备型号（"耳机 (QCY ArcBuds Lite)"）
        // 匹配不到返回 -1（调用方不画音量），不退回"默认设备音量" —— 那正是之前两台都写 100 的原因
        private static readonly Dictionary<string, int> _volCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _volAt = new Dictionary<string, DateTime>();
        private const int VolCacheMs = 1200;

        // 把"蓝牙设备"对到一个播放端点上：容器 ID 一致最可靠，否则按端点名里的型号匹配
        private static Endpoint MatchEndpoint(string container, string model)
        {
            List<Endpoint> eps = Endpoints();
            if (!string.IsNullOrEmpty(container))
            {
                for (int i = 0; i < eps.Count; i++)
                {
                    if (string.Equals(eps[i].Container, container, StringComparison.OrdinalIgnoreCase)) return eps[i];
                }
            }
            if (!string.IsNullOrEmpty(model) && model.Trim().Length >= 3)
            {
                string m = model.Trim();
                for (int i = 0; i < eps.Count; i++)
                {
                    if (eps[i].Name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0) return eps[i];
                }
            }
            return null;
        }

        // 类型提示：Windows 给播放端点起的名字本身就说明了它是什么 ——
        // 音箱/扬声器 = "扬声器 (IN糖)"，头戴耳机 = "耳机 (QCY ArcBuds)"。
        // 蓝牙设备自己的节点名看不出这层区别（音箱也带 A2DP/AVRCP），只能借端点名判。
        // 返回 1 = 音箱、2 = 耳机、0 = 说不准
        internal static int KindHint(string container, string model)
        {
            try
            {
                Endpoint e = MatchEndpoint(container, model);
                if (e == null || e.Name.Length == 0) return 0;
                string n = e.Name.ToLowerInvariant();
                if (n.Contains("扬声器") || n.Contains("音箱") || n.Contains("speaker")) return 1;
                if (n.Contains("耳机") || n.Contains("headset") || n.Contains("headphone")) return 2;
            }
            catch { }
            return 0;
        }

        internal static int VolumeFor(string container, string model)
        {
            string key = (container ?? "") + "|" + (model ?? "");
            DateTime now = DateTime.Now;
            DateTime at;
            int hit;
            if (_volAt.TryGetValue(key, out at) && (now - at).TotalMilliseconds < VolCacheMs
                && _volCache.TryGetValue(key, out hit)) return hit;

            int val = -1;
            try
            {
                Endpoint best = MatchEndpoint(container, model);
                if (best != null && best.Id.Length > 0)
                {
                    IMMDevice dev = null;
                    try
                    {
                        if (_enum == null) _enum = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                        if (_enum.GetDevice(best.Id, out dev) == 0 && dev != null) val = ReadVolume(dev);
                    }
                    finally
                    {
                        if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } }
                    }
                }
            }
            catch { }

            _volCache[key] = val;
            _volAt[key] = now;
            return val;
        }

        // 诊断用：把所有活动播放端点（名字 + 容器 ID）拼成一行，排查"配不上"时看它
        internal static string Diag()
        {
            List<Endpoint> eps = Endpoints();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < eps.Count; i++)
            {
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append(eps[i].Name).Append('@').Append(eps[i].Container);
                int v = -1;
                IMMDevice dev = null;
                try
                {
                    if (_enum == null) _enum = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
                    if (_enum.GetDevice(eps[i].Id, out dev) == 0 && dev != null) v = ReadVolume(dev);
                }
                catch { }
                finally { if (dev != null) { try { Marshal.ReleaseComObject(dev); } catch { } } }
                sb.Append('=').Append(v);
            }
            return sb.Length == 0 ? "-" : sb.ToString();
        }
    }
}
