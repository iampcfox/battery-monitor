// 蓝牙设备：枚举 → 按设备容器聚合 → 推断类型 → 判连接状态 → 取电量。
// 三条坑都在代码注释里写清楚了，概括：
//   1. 只能用容器 ID 聚合，不能用 DEVPKEY_Device_Children（会串台）
//   2. 设备树的 DevNodeStatus 判不出断开，要问 WinRT 的 ConnectionStatus（反射调用）
//   3. 通用名节点（"Bluetooth Keyboard"）只是类型线索，地址是系统另编的，不能拿它验链路

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
        // ── 蓝牙设备电量（耳机 / 键盘 / 鼠标）──
        // Windows 把设备上报的电量写成设备属性 DEVPKEY_Bluetooth_Battery：
        //   {104EA319-6EE2-4701-BD47-8DDBF425BBE5} pid=2，类型多为 BYTE
        // 它不挂在主设备上，而是挂在某一个服务实例上（本机：耳机挂在 "… Hands-Free AG"，
        // 键盘挂在那个 BLE 通用设备上），所以蓝牙三个枚举器下的节点要全扫一遍。
        // 走 SetupAPI 而不是 Registry：这批属性存在设备属性存储里，普通权限读注册表会被拒。
        private static readonly Guid BtBatteryFmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        // 设备通用属性的 fmtid（DevNodeStatus=pid2 / Parent=pid8 / Children=pid10）
        private static readonly Guid DevInfoFmtid = new Guid("4340A6C5-93FA-4706-972C-7B648008A5A7");
        private static List<BtDevice> _btCache = new List<BtDevice>();
        private static DateTime _btAt = DateTime.MinValue;

        // 上一次读到的电量（地址 → 电量/时间）。设备上报电量是"偶发"的：同一台耳机这一轮
        // 给 80，下一轮可能读不到（属性暂时不在）。没有这张表的话，30 秒内的偶发缺失会让
        // 它从窄条和悬浮窗里消失一下又回来 —— 看起来就是设备在闪
        private static readonly Dictionary<string, int> _btPct = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _btPctAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const int PctMemorySec = 30;

        private const int DIGCF_PRESENT = 0x02;
        private const int DIGCF_ALLCLASSES = 0x04;
        private const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        private const uint DEVPROP_TYPE_BYTE = 0x00000003;
        private const uint DEVPROP_TYPE_UINT16 = 0x00000005;
        private const uint DEVPROP_TYPE_UINT32 = 0x00000007;

        // 一个蓝牙设备节点：实例 ID、名字、自己的电量、设备节点状态、所属设备容器
        private sealed class BtNode
        {
            public string Id = "";
            public string Name = "";
            public int Percent = -1;
            public int Status = -1;
            public string Container = "";    // DEVPKEY_Device_ContainerId，16 字节十六进制串
            public string Enumerator = "";   // BTHENUM / BTHLE / BTHLEDEVICE
        }

        // 按"设备容器"聚合出来的一台设备（同一台设备的多个服务节点并到这里）
        private sealed class BtAgg
        {
            public string Address = "";
            public string Container = "";
            public string Enumerator = "";
            public string Names = "";     // 容器内所有节点名拼接，仅用于判类型
            public string Model = "";     // 第一个不像通用名的名字（兜底用）
            public List<string> Cand = new List<string>();   // 所有候选名，最后挑最短最干净的那个
            public int Percent = -1;      // -1 = 没上报电量
            public bool Online = false;   // 至少有一个节点是启动状态
            public BtKind Kind = BtKind.Unknown;
            public int Protos;            // 服务协议位掩码（见 ProtocolOf）
            public bool Classic;          // 出现过 BTHENUM 节点 = 经典蓝牙

            public void Append(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                if (Names.Length > 0) Names += ' ';
                Names += s.Trim();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr ClassGuid, string Enumerator,
            IntPtr hwndParent, int Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, int MemberIndex,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceProperty(IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData, ref DEVPROPKEY PropertyKey, out uint PropertyType,
            byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize, uint Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData, uint Property, out uint PropertyRegDataType,
            byte[] PropertyBuffer, uint PropertyBufferSize, out uint RequiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData, StringBuilder DeviceInstanceId,
            int DeviceInstanceIdSize, out int RequiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        // 设置面板里点"刷新"时用：丢掉 10 秒缓存，下一次 ReadBtDevices 立刻真扫一遍。
        // 不动 _linkReady —— 链路缓存本身每轮都会被 KickLinkRefresh 重新探测，
        // 这里把它置 false 只会让界面空白一秒，没好处
        internal static void InvalidateCache()
        {
            _btAt = DateTime.MinValue;
        }

        // 蓝牙电量变化很慢（几十秒才动一次），10 秒读一次就够；但断开要马上反映，
        // 缓存再长就会出现"耳机早关了还在显示"的假象
        public static List<BtDevice> ReadBtDevices()
        {
            DateTime now = DateTime.Now;
            if (_btAt != DateTime.MinValue && (now - _btAt).TotalSeconds < 10) return _btCache;
            // 首轮链路探测没回来之前不锁缓存：这时候"谁连着"还是未知数，
            // 下一拍（预热定时器 0.7 秒）要立刻重算，不能等 10 秒
            if (_linkReady) _btAt = now;

            List<BtNode> nodes = new List<BtNode>();
            try
            {
                // 经典蓝牙（A2DP / HFP）、BLE 主设备、BLE 的 GATT 服务节点，三条都扫
                CollectBtNodes("BTHENUM", nodes);
                CollectBtNodes("BTHLE", nodes);
                CollectBtNodes("BTHLEDEVICE", nodes);
            }
            catch { }

            // 按"设备容器"（DEVPKEY_Device_ContainerId）聚合：同一台物理设备的所有服务节点
            // 共享一个容器 ID（耳机四个节点连同它的 COM 串口都在 {535B0398…} 里），不同设备各占一个容器，
            // 所以两把同型号键盘、两个同型号手柄都不会被并成一条。
            // 绝对不能用 DEVPKEY_Device_Children 判类型：BTHENUM 节点的 children 返回的是
            // "本适配器下的全部节点"，实测耳机的 children 里就混着旁边那台手柄，害得耳机被判成手柄。
            List<BtAgg> aggs = new List<BtAgg>();
            Dictionary<string, BtAgg> byBox = new Dictionary<string, BtAgg>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; i++)
            {
                BtNode n = nodes[i];
                string key = n.Container.Length > 0 ? n.Container : n.Id;   // 读不到容器就退化成"一节点一台"
                if (key.Length == 0) continue;

                BtAgg a;
                if (!byBox.TryGetValue(key, out a))
                {
                    a = new BtAgg();
                    a.Container = key;
                    a.Enumerator = n.Enumerator;
                    byBox[key] = a;
                    aggs.Add(a);
                }

                a.Append(n.Name);
                if (a.Address.Length == 0) a.Address = BtAddress(n.Id);      // 排序用
                if (a.Percent < 0 && n.Percent >= 0 && n.Percent <= 100) a.Percent = n.Percent;
                if (IsBtOnline(n.Status)) a.Online = true;
                if (n.Enumerator == "BTHENUM") a.Classic = true;
                a.Protos |= ProtocolOf(n.Id);
                string nodeName = n.Name.Trim();
                if (nodeName.Length > 0 && !nodeName.ToLowerInvariant().StartsWith("bluetooth"))
                {
                    if (a.Model.Length == 0) a.Model = nodeName;
                    a.Cand.Add(nodeName);
                }
            }

            // 电量补记：这一轮没读到、但 30 秒内读到过的，沿用上次的值（见 _btPct 的注释）
            for (int i = 0; i < aggs.Count; i++)
            {
                BtAgg a = aggs[i];
                if (a.Address.Length < 12) continue;
                if (a.Percent >= 0 && a.Percent <= 100)
                {
                    _btPct[a.Address] = a.Percent;
                    _btPctAt[a.Address] = DateTime.Now;
                    continue;
                }
                int old;
                DateTime at;
                if (_btPct.TryGetValue(a.Address, out old) && _btPctAt.TryGetValue(a.Address, out at)
                    && (DateTime.Now - at).TotalSeconds <= PctMemorySec)
                    a.Percent = old;
            }

            // 先按需刷新"链路真通没通"：设备树说在线不算数（键盘断开后节点照样 STARTED）。
            // 刷新在后台线程跑，这次用上一轮的结果，没探到的不拦
            List<string> watch = new List<string>(aggs.Count);
            for (int i = 0; i < aggs.Count; i++)
            {
                BtAgg w = aggs[i];
                if (!w.Online || w.Address.Length < 12) continue;
                if (IsGenericBtName(w.Names)) continue;   // 线索节点地址是编的，问了也没意义
                watch.Add(w.Address);
            }
            KickLinkRefresh(watch);

            // 分类：有电量的直接出；没电量的分两种——
            //   · "Bluetooth Keyboard" 这种通用名节点：它是另一台 BLE 设备的 HID 功能视图，
            //     单独显示会让键盘出现两行（实测就多出一行"键盘 已连接"），只能当类型线索；
            //   · "Pro Controller" 这种真实名字：就是一台没上报电量的设备，显示"已连接"。
            List<BtDevice> list = new List<BtDevice>();
            Dictionary<string, List<BtAgg>> hints = new Dictionary<string, List<BtAgg>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<BtAgg>> pend = new Dictionary<string, List<BtAgg>>(StringComparer.OrdinalIgnoreCase);
            StringBuilder diag = new StringBuilder();       // 这轮被丢掉的是谁、为什么
            for (int i = 0; i < aggs.Count; i++)
            {
                BtAgg a = aggs[i];
                if (!a.Online) { DiagAdd(diag, a, "off"); continue; }   // 关机的不显示，旧电量也不留

                BtKind kind = ClassifyBt(a.Names);
                bool genericName = IsGenericBtName(a.Names);

                // 名字里带 A2DP/AVRCP 的都是"音频设备"，光靠设备节点名分不出音箱和耳机。
                // Windows 给播放端点起的名字能分：音箱是"扬声器 (…)"、头戴是"耳机 (…)"。
                // 只在判成耳机时再用它纠一次（反过来不纠，免得把耳机改错）
                if (kind == BtKind.Headphone && !genericName)
                {
                    int hint = Audio.KindHint(a.Container, PickModel(a));
                    if (hint == 1) kind = BtKind.Speaker;
                }

                // 设备树只说明"系统里还有这个节点"，不代表蓝牙链路现在通着。
                // 链路明确断开就跳过（键盘断开后电量还是 100 的旧值，不能拿它骗人）。
                // 但通用名线索节点（"Bluetooth Keyboard"）不能查：它在设备树里是独立容器
                // （BTHLE\DEV_101114A90050），地址是系统另编的，跟键盘真地址
                // （BTHLE\DEV_E6BD813A9447）不是一回事 —— 拿它去问永远得到"断开"，
                // 线索一死，等它配对的那台真设备就被连带丢掉了（表现就是键盘时隐时现）。
                if (!genericName)
                {
                    bool? link = BtLinkCached(a.Address);
                    // 明确断开 → 不显示；还没探到 → 首轮当"没连上"处理（宁缺勿滥），
                    // 探测结束以后仍然探不到（超时/系统不支持）才放行，免得漏掉真连着的
                    if (link.HasValue)
                    {
                        if (!link.Value) { DiagAdd(diag, a, "link0"); continue; }
                    }
                    else if (!_linkReady)
                    {
                        DiagAdd(diag, a, "wait");
                        continue;
                    }
                }

                if (a.Percent < 0)
                {
                    if (kind == BtKind.Unknown) { DiagAdd(diag, a, "noname"); continue; }
                    if (genericName)
                    {
                        a.Kind = kind;                     // 线索自己的类型，配对时要用
                        BucketAdd(hints, a.Enumerator, a);
                        continue;                          // 通用名节点不单独占一行
                    }
                    a.Kind = kind;
                    list.Add(ToDevice(a));
                }
                else
                {
                    if (kind == BtKind.Unknown) { DiagAdd(diag, a, "orphan"); BucketAdd(pend, a.Enumerator, a); continue; }
                    a.Kind = kind;
                    list.Add(ToDevice(a));
                }
            }

            // BLE 键盘的型号节点（"AK820 MAX5.0"）自己看不出是键盘，靠同枚举器里的
            // "Bluetooth Keyboard" 反推。数量一致才配对，宁可漏也不乱指：
            // 两把键盘 → 两条线索各认一个；线索数与设备数对不上就不猜。
            foreach (KeyValuePair<string, List<BtAgg>> kv in pend)
            {
                List<BtAgg> ps = kv.Value;
                List<BtAgg> hs;
                if (!hints.TryGetValue(kv.Key, out hs)) continue;
                if (ps.Count != hs.Count)
                {
                    for (int i = 0; i < ps.Count; i++) DiagAdd(diag, ps[i], "unpaired");
                    continue;
                }
                ps.Sort(CompareByAddress);
                hs.Sort(CompareByAddress);
                for (int i = 0; i < ps.Count; i++)
                {
                    ps[i].Kind = hs[i].Kind;
                    list.Add(ToDevice(ps[i]));
                }
            }

            // 同类型多台（两个手柄 / 两把键盘）：先按类型、再按地址排死。
            // List.Sort 本身不稳定，不拿地址兜底的话同类两行会在刷新时来回跳。
            list.Sort(delegate(BtDevice a, BtDevice b)
            {
                int r = BtRank(a.Kind) - BtRank(b.Kind);
                if (r != 0) return r;
                return string.CompareOrdinal(a.Address, b.Address);
            });

            // 同类不止一台才编号："键盘" vs "键盘1 / 键盘2"，只连一台时不加多余后缀
            Dictionary<BtKind, int> total = new Dictionary<BtKind, int>();
            for (int i = 0; i < list.Count; i++)
            {
                BtKind k = list[i].Kind;
                total[k] = (total.ContainsKey(k) ? total[k] : 0) + 1;
            }
            Dictionary<BtKind, int> seq = new Dictionary<BtKind, int>();
            for (int i = 0; i < list.Count; i++)
            {
                BtDevice d = list[i];
                if (total[d.Kind] <= 1) continue;
                int s = seq.ContainsKey(d.Kind) ? seq[d.Kind] + 1 : 1;
                seq[d.Kind] = s;
                d.Index = s;
                d.Label = BtKindName(d.Kind) + s.ToString(CultureInfo.InvariantCulture);
            }

            _btDiag = diag.Length == 0 ? "-" : diag.ToString();
            _btCache = list;
            return list;
        }

        // 诊断日志用：这轮被过滤掉的节点 + 原因
        //   off=设备树说不在线 / link0=链路查出来断开 / noname=没电量也认不出类型
        //   orphan=有电量但名字认不出类型（等通用名线索配对）/ unpaired=线索数与设备数对不上
        private static string _btDiag = "-";

        internal static string BtDiag()
        {
            return _btDiag;
        }

        private static void DiagAdd(StringBuilder sb, BtAgg a, string why)
        {
            if (sb.Length > 0) sb.Append(',');
            string nm = a.Model.Length > 0 ? a.Model : "(空名)";
            sb.Append(nm).Append('@').Append(a.Address)
              .Append('#').Append(a.Percent.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(why);
        }

        // 系统给 BLE 的 HID 功能节点起的通用名："Bluetooth Keyboard" / "Bluetooth Mouse" / "Bluetooth Gamepad"。
        // 这类节点是"某台设备的某个功能"，不是设备本身，不能单独占一行，只用来反推类型。
        // 只认这三个词，免得把真名叫 "Bluetooth Speaker" 之类的东西也当成通用名藏起来。
        private static bool IsGenericBtName(string s)
        {
            string t = s.Trim().ToLowerInvariant();
            if (t.Length == 0 || !t.StartsWith("bluetooth")) return false;
            return t.Contains("keyboard") || t.Contains("mouse") || t.Contains("gamepad")
                || t.Contains("game pad") || t.Contains("控制器");
        }

        private static void BucketAdd(Dictionary<string, List<BtAgg>> map, string key, BtAgg a)
        {
            List<BtAgg> v;
            if (!map.TryGetValue(key, out v))
            {
                v = new List<BtAgg>();
                map[key] = v;
            }
            v.Add(a);
        }

        private static int CompareByAddress(BtAgg a, BtAgg b)
        {
            return string.CompareOrdinal(a.Address, b.Address);
        }

        private static BtDevice ToDevice(BtAgg a)
        {
            BtDevice d = new BtDevice();
            d.Kind = a.Kind;
            d.Address = a.Address;
            d.Model = PickModel(a);
            d.Label = BtKindName(a.Kind);
            d.Percent = a.Percent;
            d.Protocols = ProtoNames(a.Protos);
            d.Container = a.Container;
            d.Le = !a.Classic;
            return d;
        }

        // ── 服务协议 ────────────────────────────────────────────────────────
        // 经典蓝牙的每个服务在设备树里各占一个节点，实例 ID 里带着服务 GUID：
        //   BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001000B…\…&84AC60112BD4_C00000000
        // 把这些 GUID 认出来，就成了"这台设备支持什么"——和系统设置里看到的信息一致。
        // 只认桌面端说得清的几项，别的一律忽略（不要把设备树翻译成一串看不懂的 UUID）
        private const int P_A2DP = 1, P_HFP = 2, P_HSP = 4, P_AVRCP = 8, P_HID = 16, P_HOGP = 32;

        private static int ProtocolOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            string t = id.ToLowerInvariant();
            int m = 0;
            if (t.Contains("0000110b")) m |= P_A2DP;                              // Audio Sink
            if (t.Contains("0000111e") || t.Contains("0000111f")) m |= P_HFP;     // Hands-Free
            if (t.Contains("00001108")) m |= P_HSP;                               // Headset
            if (t.Contains("0000110e") || t.Contains("0000110c") || t.Contains("0000110f"))
                m |= P_AVRCP;                                                     // A/V Remote Control
            if (t.Contains("00001124")) m |= P_HID;                               // HID（经典）
            if (t.Contains("00001812")) m |= P_HOGP;                              // HID over GATT（BLE）
            return m;
        }

        // 顺序按"用户最关心"排：先音频链路，再遥控，最后 HID
        private static string ProtoNames(int mask)
        {
            if (mask == 0) return "";
            StringBuilder sb = new StringBuilder();
            if ((mask & P_A2DP) != 0) sb.Append("A2DP");
            if ((mask & P_HFP) != 0) AppendProto(sb, "HFP");
            if ((mask & P_HSP) != 0) AppendProto(sb, "HSP");
            if ((mask & P_AVRCP) != 0) AppendProto(sb, "AVRCP");
            if ((mask & (P_HID | P_HOGP)) != 0) AppendProto(sb, "HID");
            return sb.ToString();
        }

        private static void AppendProto(StringBuilder sb, string s)
        {
            if (sb.Length > 0) sb.Append('/');
            sb.Append(s);
        }

        // 历史数据里存的是设备类型名，老数据没存类型：按名字反推一次（"耳机"、"键盘"…）
        internal static BtKind GuessKind(string name, BtKind known)
        {
            if (known != BtKind.Unknown) return known;
            return ClassifyBt(name);
        }

        // Windows 给同一台设备的每个服务节点都挂一个尾巴："<设备名> Avrcp 传输"、
        // "<设备名> A2DP 传输"、"<设备名> Hands-Free AG Audio"……直接用就变成
        // "QCY ArcBuds Lite Avrcp 传输"，卡片标题会被撑爆。这里把这些尾巴剪掉
        private static readonly string[] ModelTails =
        {
            " avrcp 传输", " avrcp transport", " a2dp 传输", " a2dp transport",
            " hfp 传输", " hfp transport", " hands-free ag audio", " hands free ag audio",
            " hands-free", " handsfree", " 免提", " 音频传输", " 音频接收器", " 传输"
        };

        private static string TrimModelTail(string raw)
        {
            string t = (raw ?? "").Trim();
            for (int guard = 0; guard < 4; guard++)          // 尾巴可能叠两层
            {
                bool cut = false;
                foreach (string tail in ModelTails)
                {
                    if (!t.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
                    t = t.Substring(0, t.Length - tail.Length).Trim();
                    t = t.TrimEnd('，', ',', '-', '·', '_', '(', ')', '/', '、').Trim();
                    cut = true;
                    break;
                }
                if (!cut) break;
            }
            return t;
        }

        // 同一台设备的几个节点名，短的通常就是干净的那个（长的是带服务尾巴的）
        private static string PickModel(BtAgg a)
        {
            string best = "";
            for (int i = 0; i < a.Cand.Count; i++)
            {
                string t = TrimModelTail(a.Cand[i]);
                if (t.Length == 0) continue;
                if (best.Length == 0 || t.Length < best.Length) best = t;
            }
            if (best.Length == 0) best = TrimModelTail(a.Model);
            return best;
        }

        // 从设备实例 ID 里抠出蓝牙地址（BD_ADDR，12 位十六进制）。
        // 系统本来有个 DEVPKEY_Bluetooth_DeviceAddress，但本机实测读不到，只能解析实例 ID：
        //   BTHENUM\{0000111E-…}_…\5&109C16B3&0&84AC60112BD4_C00000000   → 84AC60112BD4
        //   BTHENUM\DEV_84AC60112BD4\5&109C16B3&0&BLUETOOTHDEVICE_84AC60112BD4
        //   BTHLE\DEV_E6BD813A9447\5&144969EC&0&E6BD813A9447
        // 做法：扫全串，取最后一个前后都不是 hex 的 12 位段（"109C16B3" 这种 8 位的不会误命中）
        private static string BtAddress(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string best = "";
            for (int i = 0; i + 12 <= id.Length; i++)
            {
                if (i > 0 && IsHexChar(id[i - 1])) continue;
                bool ok = true;
                for (int k = 0; k < 12; k++)
                {
                    if (!IsHexChar(id[i + k])) { ok = false; break; }
                }
                if (!ok) continue;
                if (i + 12 < id.Length && IsHexChar(id[i + 12])) continue;
                best = id.Substring(i, 12).ToUpperInvariant();
                i += 11;
            }
            return best;
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        // 设备节点状态位（DEVPKEY_Device_DevNodeStatus）
        private const int DN_STARTED = 0x00000008;
        private const int DN_HAS_PROBLEM = 0x00000400;

        // 蓝牙设备关掉以后，节点并不会消失，只是驱动不再启动（丢掉 DN_STARTED / 带问题码），
        // 这时候读到的电量是断开前的旧值，不能拿它当"还连着"。
        // 读不到状态（-1）就当在线处理：宁可多显示一台，也别把真连着的漏掉。
        private static bool IsBtOnline(int status)
        {
            if (status < 0) return true;
            if ((status & DN_HAS_PROBLEM) != 0) return false;
            return (status & DN_STARTED) != 0;
        }

        // ── 链路状态：设备树骗人，只能问 Windows 运行时 ──
        // 实测（键盘断开蓝牙时）：设备节点仍是 0x0380200A，DN_STARTED 还在、ProblemCode=0，
        // 注册表里也没有任何"当前连接"标记 —— 光看 PnP 永远判不出断开。
        // 真正的答案在 Windows.Devices.Bluetooth：BLE 看 BluetoothLEDevice.ConnectionStatus，
        // 经典看 BluetoothDevice.ConnectionStatus。实测同一次运行里：
        //   耳机 Classic=Connected / 键盘 LE=Disconnected / 手柄 Classic=Disconnected，与实际完全一致。
        // 本程序是 .NET 4.x 编译、不能引用 WinRT 元数据，所以整条链走反射：
        //   Type.GetType("…, ContentType=WindowsRuntime") → FromBluetoothAddressAsync → AsTask → ConnectionStatus。
        // 任何一步失败（老系统、API 缺失、超时）都返回 null，调用方按"在线"处理：宁可多显示，不漏真连着的。
        private static Type _tLeDev, _tClDev;
        private static MethodInfo _mLeFrom, _mClFrom, _mAsTask;
        private static bool _winRtTried;

        private static readonly object _winRtLock = new object();

        private static void ProbeWinRt()
        {
            if (_winRtTried) return;
            lock (_winRtLock)
            {
                if (_winRtTried) return;
                _winRtTried = true;
                ProbeWinRtCore();
            }
        }

        private static void ProbeWinRtCore()
        {
            try
            {
                _tLeDev = Type.GetType(
                    "Windows.Devices.Bluetooth.BluetoothLEDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                _tClDev = Type.GetType(
                    "Windows.Devices.Bluetooth.BluetoothDevice, Windows.Devices.Bluetooth, ContentType=WindowsRuntime");
                if (_tLeDev != null)
                    _mLeFrom = _tLeDev.GetMethod("FromBluetoothAddressAsync", new Type[] { typeof(ulong) });
                if (_tClDev != null)
                    _mClFrom = _tClDev.GetMethod("FromBluetoothAddressAsync", new Type[] { typeof(ulong) });

                // IAsyncOperation<T> → Task<T> 的扩展方法在 System.Runtime.WindowsRuntime 里。
                // 它在 GAC 里，但简单名在部分宿主下 Load 不到（实测会 FileNotFound），必须给完整名
                Assembly asm = LoadWinRtAsm();
                Type ext = asm == null ? null : asm.GetType("System.WindowsRuntimeSystemExtensions");
                if (ext != null)
                {
                    MethodInfo[] ms = ext.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < ms.Length; i++)
                    {
                        MethodInfo m = ms[i];
                        if (!m.IsGenericMethodDefinition || m.Name != "AsTask") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length != 1) continue;
                        if (ps[0].ParameterType.Name.StartsWith("IAsyncOperation`1", StringComparison.Ordinal))
                        {
                            _mAsTask = m;
                            break;
                        }
                    }
                }
            }
            catch
            {
                _mLeFrom = null;
                _mClFrom = null;
                _mAsTask = null;
            }
        }

        // System.Runtime.WindowsRuntime 装在 GAC，简单名 Load 会 FileNotFound（实测），先按完整名加载，
        // 再退一步在已加载程序集里找（万一将来被别的东西先加载过）
        private static Assembly LoadWinRtAsm()
        {
            try
            {
                return Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, " +
                    "PublicKeyToken=b77a5c561934e089");
            }
            catch { }
            try
            {
                Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].GetName().Name == "System.Runtime.WindowsRuntime") return all[i];
                }
            }
            catch { }
            return null;
        }

        // 查一条通道（BLE 或经典）。known=false 表示这台设备不在这条通道上，或压根查不出来
        private static bool QueryLink(MethodInfo from, Type type, ulong addr, out bool known)
        {
            known = false;
            if (from == null || type == null || _mAsTask == null) return false;
            try
            {
                object op = from.Invoke(null, new object[] { addr });
                if (op == null) return false;
                object task = _mAsTask.MakeGenericMethod(type).Invoke(null, new object[] { op });
                if (task == null) return false;

                Type tt = task.GetType();
                MethodInfo wait = tt.GetMethod("Wait", new Type[] { typeof(int) });
                if (wait != null) wait.Invoke(task, new object[] { 400 });
                else tt.GetMethod("Wait", Type.EmptyTypes).Invoke(task, null);

                // 没跑完就不采信：超时多半是设备不在范围内，别让一次查询把卡片刷新拖住
                object st = tt.GetProperty("Status").GetValue(task, null);
                if (Convert.ToInt32(st, CultureInfo.InvariantCulture) != 5) return false;   // 5 = RanToCompletion

                object dev = tt.GetProperty("Result").GetValue(task, null);
                if (dev == null) return false;                                              // 这台不在该通道上
                object cs = dev.GetType().GetProperty("ConnectionStatus").GetValue(dev, null);
                known = true;                                                               // 0 = 断开, 1 = 已连接
                bool on = Convert.ToInt32(cs, CultureInfo.InvariantCulture) == 1;

                IDisposable closable = dev as IDisposable;
                if (closable != null) closable.Dispose();
                return on;
            }
            catch { return false; }
        }

        // true = 明确连着，false = 明确断了，null = 不知道（调用方别据此隐藏）
        private static bool? BtLinkState(string addr12)
        {
            ProbeWinRt();
            if (_mAsTask == null || addr12.Length < 12) return null;
            ulong addr;
            if (!ulong.TryParse(addr12, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr)) return null;
            if (addr == 0) return null;

            bool kLe, kCl;
            bool le = QueryLink(_mLeFrom, _tLeDev, addr, out kLe);
            if (kLe && le) return true;
            bool cl = QueryLink(_mClFrom, _tClDev, addr, out kCl);
            if (kCl && cl) return true;
            if (kLe || kCl) return false;
            return null;
        }

        private static readonly object _linkLock = new object();
        private static Dictionary<string, bool> _linkCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 连续几轮没问到结果。这里以前是"_linkCache = map"整体替换：某台设备这一轮
        // 查询超时（400ms 没回来）就会从表里消失 → BtLinkCached 返回 null → 调用方按
        // "未知"放行 → 明明没连的手柄闪一下又消失，再超时一次又冒出来。
        // 现在改成"问到才更新，没问到保留旧结论"，连续 3 轮都问不到才放弃（当成未知）
        private static Dictionary<string, int> _linkMiss =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static int _linkBusy;

        // 首轮链路探测完成没有。没完成时蓝牙行一律不显示 —— 冷启动那一刻"谁连着"
        // 还是未知数，先把所有历史设备铺出来再一个个消失，观感很差。
        private static volatile bool _linkReady;

        internal static bool LinkReady()
        {
            return _linkReady;
        }

        private static bool? BtLinkCached(string addr12)
        {
            if (addr12.Length == 0) return null;
            lock (_linkLock)
            {
                bool v;
                if (_linkCache.TryGetValue(addr12, out v)) return v;
            }
            return null;
        }

        // 后台刷一遍链路状态。WinRT 要等 IAsyncOperation，放 UI 线程会卡住拖动，所以扔线程池。
        // 结果下一轮（10 秒）生效：断开最迟十来秒那行就消失，重新连上也会自己回来。
        private static void KickLinkRefresh(List<string> addrs)
        {
            ProbeWinRt();
            // 没有蓝牙设备，或这台机器压根跑不了 WinRT：没什么可等，立刻算"已知"
            if (addrs.Count == 0 || _mAsTask == null) { _linkReady = true; return; }
            if (Interlocked.CompareExchange(ref _linkBusy, 1, 0) != 0) return;
            string[] arr = addrs.ToArray();
            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                Dictionary<string, bool> map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        bool? c = BtLinkState(arr[i]);
                        if (c.HasValue) map[arr[i]] = c.Value;
                    }
                }
                catch { }
                finally
                {
                    lock (_linkLock)
                    {
                        foreach (KeyValuePair<string, bool> kv in map)
                        {
                            _linkCache[kv.Key] = kv.Value;      // 这一轮问到了：以它为准
                            _linkMiss.Remove(kv.Key);
                        }
                        // 这一轮没问到的：保留上一轮结论，连续 3 轮都问不到才删（防止真断开后一直卡在"连着"）
                        List<string> stale = null;
                        foreach (KeyValuePair<string, bool> kv in _linkCache)
                        {
                            if (map.ContainsKey(kv.Key)) continue;
                            int miss;
                            _linkMiss.TryGetValue(kv.Key, out miss);
                            miss++;
                            _linkMiss[kv.Key] = miss;
                            if (miss > 3) { if (stale == null) stale = new List<string>(); stale.Add(kv.Key); }
                        }
                        if (stale != null)
                        {
                            for (int i = 0; i < stale.Count; i++)
                            {
                                _linkCache.Remove(stale[i]);
                                _linkMiss.Remove(stale[i]);
                            }
                        }
                    }
                    _linkReady = true;                 // 探过了，之后探不到就按"未知"放行
                    Interlocked.Exchange(ref _linkBusy, 0);
                }
            });
        }

        // 日志用：地址:1/0（1 连着、0 断开）；没探到的地址不出现
        internal static string LinkLog()
        {
            lock (_linkLock)
            {
                if (_linkCache.Count == 0) return "-";
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, bool> kv in _linkCache)
                {
                    if (sb.Length > 0) sb.Append('/');
                    sb.Append(kv.Key).Append(':').Append(kv.Value ? '1' : '0');
                }
                return sb.ToString();
            }
        }

        private static void CollectBtNodes(string enumerator, List<BtNode> nodes)
        {
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, enumerator, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return;
            try
            {
                for (int i = 0; ; i++)
                {
                    SP_DEVINFO_DATA data = new SP_DEVINFO_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;

                    BtNode n = new BtNode();
                    n.Id = DeviceInstanceId(set, ref data);
                    n.Name = DeviceFriendlyName(set, ref data);
                    n.Percent = DeviceBattery(set, ref data);
                    n.Status = DeviceDevNodeStatus(set, ref data);
                    n.Container = DeviceContainerId(set, ref data);
                    n.Enumerator = enumerator;
                    nodes.Add(n);
                }
            }
            catch { }
            finally { try { SetupDiDestroyDeviceInfoList(set); } catch { } }
        }

        private static string DeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            StringBuilder sb = new StringBuilder(512);
            int need;
            if (!SetupDiGetDeviceInstanceId(set, ref data, sb, sb.Capacity, out need)) return "";
            return sb.ToString();
        }

        private static string DeviceFriendlyName(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            byte[] buf = new byte[512];
            uint type, need;
            if (!SetupDiGetDeviceRegistryProperty(set, ref data, SPDRP_FRIENDLYNAME,
                out type, buf, (uint)buf.Length, out need)) return "";
            int len = (int)Math.Min(need, (uint)buf.Length);
            return Encoding.Unicode.GetString(buf, 0, len).TrimEnd('\0');
        }

        private static int DeviceBattery(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = BtBatteryFmtid;
            key.pid = 2;
            byte[] buf = new byte[16];
            uint type, need;
            if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type,
                buf, (uint)buf.Length, out need, 0)) return -1;
            if (type == DEVPROP_TYPE_BYTE) return buf[0];
            if (type == DEVPROP_TYPE_UINT16) return BitConverter.ToUInt16(buf, 0);
            if (type == DEVPROP_TYPE_UINT32) return (int)BitConverter.ToUInt32(buf, 0);
            return -1;
        }

        // 设备节点状态（DevNodeStatus，UINT32 位域）：{4340A6C5-…} pid=2
        private static int DeviceDevNodeStatus(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = DevInfoFmtid;
            key.pid = 2;
            byte[] buf = new byte[8];
            uint type, need;
            if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type,
                buf, (uint)buf.Length, out need, 0)) return -1;
            if (type == DEVPROP_TYPE_UINT32) return BitConverter.ToInt32(buf, 0);
            return -1;
        }

        // 设备容器 ID（DEVPKEY_Device_ContainerId）：{8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C} pid=2
        // 同一台物理设备的所有节点共享同一个 GUID —— 这是"这台设备"唯一可靠的身份证。
        // 实测：耳机的 HFP/A2DP/AVRCP/DEV_ 四个节点连同它的 COM 串口都是 {535B0398…}，
        // 旁边那台 Pro Controller 是 {0441CD95…}，所以按容器聚合既不会拆散一台设备，也不会把两台并起来。
        private static readonly Guid DevContainerFmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C");
        private const uint DEVPROP_TYPE_GUID = 0x0000000D;

        // 直接拿 16 字节转十六进制当 key，不关心 GUID 的字节序，只要同设备一致就行
        private static string DeviceContainerId(IntPtr set, ref SP_DEVINFO_DATA data)
        {
            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = DevContainerFmtid;
            key.pid = 2;
            byte[] buf = new byte[16];
            uint type, need;
            if (!SetupDiGetDeviceProperty(set, ref data, ref key, out type,
                buf, (uint)buf.Length, out need, 0)) return "";
            if (type != DEVPROP_TYPE_GUID) return "";

            StringBuilder sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(buf[i].ToString("X2"));
            string id = sb.ToString();

            // {00000000-0000-0000-FFFF-FFFFFFFFFFFF} 是"没有容器"的哨兵值，
            // 好几台设备都没有容器时不能靠它并成一台（本机的 COM4 就是这个值）
            if (id == "0000000000000000FFFFFFFFFFFFFFFF") return "";
            return id;
        }

        // 判类型：看容器里所有节点的名字。
        // 耳机的 HFP 节点带 "Hands-Free"，键盘的电量节点叫 "AK820 MAX5.0"、同容器里那个叫 "Bluetooth Keyboard"，
        // 手柄叫 "Pro Controller" —— 拼起来看就不会漏。
        // 手柄放在耳机前面判：手柄名里偶尔也带 "audio"，而 "controller" 更具体，先认它。
        private static BtKind ClassifyBt(string s)
        {
            if (string.IsNullOrEmpty(s)) return BtKind.Unknown;
            string t = s.ToLowerInvariant();
            if (t.Contains("keyboard") || s.Contains("键盘")) return BtKind.Keyboard;
            if (t.Contains("mouse") || s.Contains("鼠标")) return BtKind.Mouse;
            // 音箱排在耳机前面：音箱名里几乎都带 "audio"，而 "audio" 在下面归耳机，
            // 先认更具体的 speaker / soundbar，不然 JBL、Bose 这类会被认成耳机
            if (t.Contains("speaker") || t.Contains("soundbar") || t.Contains("sound bar")
                || t.Contains("boombox") || s.Contains("音箱") || s.Contains("音响")
                || s.Contains("扬声器")) return BtKind.Speaker;
            if (t.Contains("controller") || t.Contains("gamepad") || t.Contains("game pad")
                || t.Contains("joystick") || t.Contains("dualshock") || t.Contains("dualsense")
                || t.Contains("xbox") || t.Contains("8bitdo") || s.Contains("手柄"))
                return BtKind.Gamepad;
            if (t.Contains("hands-free") || t.Contains("handsfree") || t.Contains("avrcp")
                || t.Contains("headphone") || t.Contains("headset") || t.Contains("buds")
                || t.Contains("airpod") || t.Contains("earbud") || t.Contains("audio")
                || s.Contains("耳机")) return BtKind.Headphone;
            return BtKind.Unknown;
        }

        private static string BtKindName(BtKind k)
        {
            if (k == BtKind.Headphone) return "耳机";
            if (k == BtKind.Keyboard) return "键盘";
            if (k == BtKind.Gamepad) return "手柄";
            if (k == BtKind.Mouse) return "鼠标";
            if (k == BtKind.Speaker) return "音箱";
            return "蓝牙设备";
        }

        // 给历史记录/历史管理页用的类型名（那两个文件不该再复制一遍这张表）
        internal static string KindName(BtKind k)
        {
            return BtKindName(k);
        }

        internal static RowIcon BtKindIcon(BtKind k)
        {
            if (k == BtKind.Headphone) return RowIcon.Headphone;
            if (k == BtKind.Keyboard) return RowIcon.Keyboard;
            if (k == BtKind.Gamepad) return RowIcon.Gamepad;
            if (k == BtKind.Mouse) return RowIcon.Mouse;
            if (k == BtKind.Speaker) return RowIcon.Speaker;   // 跟音量图标同一个喇叭
            return RowIcon.None;
        }

        // 卡片上的固定顺序：耳机、键盘、手柄、鼠标，别的排最后
        private static int BtRank(BtKind k)
        {
            if (k == BtKind.Headphone) return 0;
            if (k == BtKind.Keyboard) return 1;
            if (k == BtKind.Gamepad) return 2;
            if (k == BtKind.Mouse) return 3;
            if (k == BtKind.Speaker) return 4;
            return 5;
        }

        // ── 远端设备的蓝牙版本（LMP）────────────────────────────────────────
        // 设备属性里没有版本这一项，真版本在蓝牙适配器驱动里：
        //   BluetoothFindFirstRadio → DeviceIoControl(IOCTL_BTH_GET_RADIO_INFO=0x410004)
        //   输入 8 字节 BD_ADDR，输出 BTH_RADIO_INFO，里面的 lmpVersion 对照 SIG 表
        //   （0x09 = 蓝牙 5.0，0x0C = 5.3）。只有当前有 ACL 链路才查得到；
        // 这是半公开接口，查不到就留空，绝不能影响现有的显示。
        [StructLayout(LayoutKind.Sequential)]
        private struct BtFindRadioParams
        {
            public uint dwSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BtRadioInfo
        {
            public ulong Features;
            public ushort Manufacturer;
            public ushort LmpSubversion;
            public byte LmpVersion;
        }

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern IntPtr BluetoothFindFirstRadio(ref BtFindRadioParams p, out IntPtr hRadio);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern bool BluetoothFindNextRadio(IntPtr hFind, out IntPtr hRadio);

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern bool BluetoothFindRadioClose(IntPtr hFind);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint code, IntPtr inBuf, uint inSize,
            IntPtr outBuf, uint outSize, out uint bytes, IntPtr overlapped);

        private const uint IoctlBthGetRadioInfo = 0x410004;

        private static readonly object _verLock = new object();
        private static readonly Dictionary<string, string> _verCache = new Dictionary<string, string>();
        private static readonly Dictionary<string, DateTime> _verAt = new Dictionary<string, DateTime>();
        private static readonly Dictionary<string, int> _verBusy = new Dictionary<string, int>();

        // UI 直接调：缓存里有就立刻返回；没有就丢到后台查，本次先返回空串。
        // 失败也缓存 10 分钟，免得每 2 秒重查一次（版本不会变，断开时查不到而已）
        internal static string BtVersion(string addr12)
        {
            if (string.IsNullOrEmpty(addr12)) return "";
            string key = addr12.ToUpperInvariant();
            lock (_verLock)
            {
                string v;
                if (_verCache.TryGetValue(key, out v)) return v;
                if (_verBusy.ContainsKey(key)) return "";
                DateTime at;
                if (_verAt.TryGetValue(key, out at) && (DateTime.Now - at).TotalMinutes < 10) return "";
                _verBusy[key] = 1;
            }
            ThreadPool.QueueUserWorkItem(delegate { ProbeBtVersion(key); });
            return "";
        }

        private static void ProbeBtVersion(string key)
        {
            string found = "";
            ulong addr;
            if (ulong.TryParse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
            {
                IntPtr find = IntPtr.Zero, radio = IntPtr.Zero;
                try
                {
                    BtFindRadioParams p = new BtFindRadioParams();
                    p.dwSize = (uint)Marshal.SizeOf(typeof(BtFindRadioParams));
                    find = BluetoothFindFirstRadio(ref p, out radio);
                    while (find != IntPtr.Zero)
                    {
                        string v = ReadRadioVersion(radio, addr);
                        if (radio != IntPtr.Zero) { try { CloseHandle(radio); } catch { } radio = IntPtr.Zero; }
                        if (v.Length > 0) { found = v; break; }
                        if (!BluetoothFindNextRadio(find, out radio)) break;
                    }
                }
                catch { }
                finally
                {
                    if (radio != IntPtr.Zero) { try { CloseHandle(radio); } catch { } }
                    if (find != IntPtr.Zero) { try { BluetoothFindRadioClose(find); } catch { } }
                }
            }
            lock (_verLock)
            {
                _verCache[key] = found;
                _verAt[key] = DateTime.Now;
                _verBusy.Remove(key);
            }
        }

        private static string ReadRadioVersion(IntPtr radio, ulong addr)
        {
            IntPtr inBuf = IntPtr.Zero, outBuf = IntPtr.Zero;
            try
            {
                inBuf = Marshal.AllocHGlobal(8);
                outBuf = Marshal.AllocHGlobal(32);
                Marshal.WriteInt64(inBuf, unchecked((long)addr));       // BTH_ADDR = ULONGLONG，小端写入
                for (int i = 0; i < 32; i++) Marshal.WriteByte(outBuf, i, 0);
                uint bytes = 0;
                if (!DeviceIoControl(radio, IoctlBthGetRadioInfo, inBuf, 8,
                    outBuf, 13, out bytes, IntPtr.Zero)) return "";
                if (bytes < 13) return "";
                BtRadioInfo info = (BtRadioInfo)Marshal.PtrToStructure(outBuf, typeof(BtRadioInfo));
                return VersionName(info.LmpVersion);
            }
            catch { return ""; }
            finally
            {
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            }
        }

        // 蓝牙 SIG Assigned Numbers：LMP/HCI 主版本 → 核心规范版本
        private static string VersionName(byte v)
        {
            switch (v)
            {
                case 0x00: return "1.0";
                case 0x01: return "1.1";
                case 0x02: return "1.2";
                case 0x03: return "2.0";
                case 0x04: return "2.1";
                case 0x05: return "3.0";
                case 0x06: return "4.0";
                case 0x07: return "4.1";
                case 0x08: return "4.2";
                case 0x09: return "5.0";
                case 0x0A: return "5.1";
                case 0x0B: return "5.2";
                case 0x0C: return "5.3";
                case 0x0D: return "5.4";
                case 0x0E: return "6.0";
                default: return "";
            }
        }
    }
}
