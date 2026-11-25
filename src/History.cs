// 历史数据，两个文件：
//   battery-monitor-history.csv      一行一个采样点   "yyyy-MM-dd HH:mm:ss,电量,温度,是否接通电源"
//   battery-monitor-history-bt.csv   一行一台设备一次 "yyyy-MM-dd HH:mm:ss,地址,名称,电量,类型"
//                                    类型是可选的第五列（0 未知 / 1 耳机 / 2 键盘 …），
//                                    老文件只有四列，读进来按名字反推，写回去就补齐了
//
// 蓝牙单独存一份，是因为设备数量不固定：塞进主表就得让列数跟着设备数变，读写都得对表头，
// 一台上报不了的设备还能把整行格式带歪。分开存之后，只写"真的连上、真的报了电量"的设备，
// 断开期间自然没有行，图上那段就是空的 —— 不需要额外记"什么时候断开"。
//
// 只做追加写 + 启动时按保留天数裁剪，不引入数据库。

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
    internal static class History
    {
        internal sealed class Point
        {
            public DateTime Time;
            public int Percent;
            public double TempC = double.NaN;
            public bool Ac;
        }

        // 蓝牙设备的一个采样点。"地址"是分组依据（同一台设备多次连接算同一条曲线），
        // 名称只用来画图例/页签，改名了也不影响归组；Kind 是设备类型，用来自动画类型图标。
        // 老文件只有前 4 列，读进来 Kind = Unknown，再由名字反推
        internal sealed class BtPoint
        {
            public DateTime Time;
            public string Id = "";
            public string Label = "";
            public int Percent;
            public BtKind Kind = BtKind.Unknown;
        }

        private const string StampFormat = "yyyy-MM-dd HH:mm:ss";

        private static string _path = "";
        private static string _btPath = "";
        private static int _keepDays = 30;
        private static readonly List<Point> _list = new List<Point>();
        private static readonly List<BtPoint> _bt = new List<BtPoint>();
        private static int _trimmedSinceSave;   // 累计删掉多少行，够多才整体重写文件

        public static string FilePath { get { return _path; } }
        public static string BtFilePath { get { return _btPath; } }
        public static int Count { get { return _list.Count; } }
        public static int BtCount { get { return _bt.Count; } }

        public static DateTime LastTime
        {
            get { return _list.Count == 0 ? DateTime.MinValue : _list[_list.Count - 1].Time; }
        }

        public static DateTime FirstTime
        {
            get { return _list.Count == 0 ? DateTime.MinValue : _list[0].Time; }
        }

        public static long FileSize
        {
            get
            {
                long n = 0;
                try { if (File.Exists(_path)) n += new FileInfo(_path).Length; } catch { }
                try { if (File.Exists(_btPath)) n += new FileInfo(_btPath).Length; } catch { }
                return n;
            }
        }

        public static List<Point> Snapshot() { return new List<Point>(_list); }
        public static List<BtPoint> BtSnapshot() { return new List<BtPoint>(_bt); }

        public static void Init(string path, string btPath, int keepDays)
        {
            _path = path;
            _btPath = btPath;
            _keepDays = keepDays > 0 ? keepDays : 30;
            try { Load(); } catch { }
        }

        private static void Load()
        {
            _list.Clear();
            _bt.Clear();
            DateTime cutoff = DateTime.Now.AddDays(-_keepDays);

            if (File.Exists(_path))
            {
                foreach (string raw in File.ReadAllLines(_path))
                {
                    Point p = Parse(raw);
                    if (p == null) continue;
                    if (p.Time < cutoff) { _trimmedSinceSave++; continue; }
                    _list.Add(p);
                }
                _list.Sort(delegate (Point a, Point b) { return a.Time.CompareTo(b.Time); });
            }

            if (File.Exists(_btPath))
            {
                foreach (string raw in File.ReadAllLines(_btPath))
                {
                    BtPoint b = ParseBt(raw);
                    if (b == null) continue;
                    if (b.Time < cutoff) { _trimmedSinceSave++; continue; }
                    _bt.Add(b);
                }
                _bt.Sort(delegate (BtPoint a, BtPoint b) { return a.Time.CompareTo(b.Time); });
            }

            if (_trimmedSinceSave > 0) Rewrite();
        }

        private static Point Parse(string raw)
        {
            string line = raw.Trim().Trim('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#")) return null;
            string[] f = line.Split(',');
            if (f.Length < 2) return null;

            DateTime t;
            if (!DateTime.TryParseExact(f[0].Trim(), StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out t)) return null;

            int pct;
            if (!int.TryParse(f[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pct)) return null;
            if (pct < 0 || pct > 100) return null;

            Point p = new Point();
            p.Time = t;
            p.Percent = pct;
            if (f.Length > 2)
            {
                double temp;
                if (double.TryParse(f[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out temp))
                    p.TempC = temp;
            }
            if (f.Length > 3) p.Ac = f[3].Trim() == "1";
            return p;
        }

        private static BtPoint ParseBt(string raw)
        {
            string line = raw.Trim().Trim('\uFEFF');
            if (line.Length == 0 || line.StartsWith("#")) return null;
            string[] f = line.Split(',');
            if (f.Length < 4) return null;

            DateTime t;
            if (!DateTime.TryParseExact(f[0].Trim(), StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out t)) return null;

            int pct;
            if (!int.TryParse(f[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pct)) return null;
            if (pct < 0 || pct > 100) return null;

            BtPoint b = new BtPoint();
            b.Time = t;
            b.Id = f[1].Trim();
            b.Label = f[2].Trim();
            b.Percent = pct;
            if (f.Length > 4)
            {
                int k;
                if (int.TryParse(f[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out k)
                    && k >= 0 && k <= (int)BtKind.Speaker)
                    b.Kind = (BtKind)k;
            }
            b.Kind = Sensors.GuessKind(b.Label, b.Kind);
            return b;
        }

        public static void Append(Sample s)
        {
            if (s == null) return;
            DateTime now = DateTime.Now;

            // 本机没电池（台式机）时主表不写，但蓝牙设备照样要记 —— 所以这里不是"没电量就整条返回"，
            // 而是两件事分开判断
            if (s.Percent >= 0)
            {
                Point p = new Point();
                p.Time = now;
                p.Percent = s.Percent;
                p.TempC = s.TempC;
                p.Ac = s.AcOnline;
                _list.Add(p);

                try
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(p.Time.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(double.IsNaN(p.TempC) ? "" : p.TempC.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Ac ? "1" : "0");
                    File.AppendAllText(_path, sb.ToString() + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }

            AppendBt(s, now);
            TrimFromMemory();
        }

        // 蓝牙：Sample.Bt 里只会有"当前连着"的设备，没连的压根不在这里。
        // 再筛掉 Percent < 0 的（连上了但系统不上报，例如部分手柄）—— 没有数值画不出曲线，
        // 记一行只有时间没有电量的数据反而会在图上留个空洞
        private static void AppendBt(Sample s, DateTime now)
        {
            if (s.Bt == null || s.Bt.Count == 0 || _btPath.Length == 0) return;

            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (BtDevice d in s.Bt)
            {
                if (d == null || d.Percent < 0 || d.Percent > 100) continue;

                string id = string.IsNullOrEmpty(d.Address) ? d.Label : d.Address;
                string label = BtName(d);

                BtPoint b = new BtPoint();
                b.Time = now;
                b.Id = id;
                b.Label = label;
                b.Percent = d.Percent;
                b.Kind = d.Kind;
                _bt.Add(b);

                sb.Append(now.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                sb.Append(Clean(id)).Append(',');
                sb.Append(label).Append(',');
                sb.Append(b.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(((int)b.Kind).ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
                n++;
            }
            if (n == 0) return;

            try { File.AppendAllText(_btPath, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
        }

        // 页签/图例上显示的名字：优先型号（"QCY ArcBuds Lite"），没型号才退回类型名（"耳机"）。
        // 写进文件的是型号，所以历史统计的页签是"有啥显示啥"，不是写死一个"耳机"
        private static string BtName(BtDevice d)
        {
            string m = Clean(d.Model);
            return m.Length > 0 ? m : Clean(d.Label);
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(',', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        // 内存里超过保留期就丢，丢够 100 条再整体重写文件（避免每 5 分钟重写 260 KB）
        private static void TrimFromMemory()
        {
            DateTime cutoff = DateTime.Now.AddDays(-_keepDays);
            int dropped = 0;

            int i = 0;
            while (i < _list.Count && _list[i].Time < cutoff) i++;
            if (i > 0) { _list.RemoveRange(0, i); dropped += i; }

            int j = 0;
            while (j < _bt.Count && _bt[j].Time < cutoff) j++;
            if (j > 0) { _bt.RemoveRange(0, j); dropped += j; }

            _trimmedSinceSave += dropped;
            if (_trimmedSinceSave >= 100) Rewrite();
        }

        private static void Rewrite()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (Point p in _list)
                {
                    sb.Append(p.Time.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(double.IsNaN(p.TempC) ? "" : p.TempC.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Ac ? "1" : "0").Append(Environment.NewLine);
                }
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (BtPoint b in _bt)
                {
                    sb.Append(b.Time.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(Clean(b.Id)).Append(',');
                    sb.Append(Clean(b.Label)).Append(',');
                    sb.Append(b.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(((int)b.Kind).ToString(CultureInfo.InvariantCulture)).Append(Environment.NewLine);
                }
                if (_btPath.Length > 0) File.WriteAllText(_btPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }

            _trimmedSinceSave = 0;
        }

        // ── 历史管理页要用的三件事：导出、清空、立刻补一个点 ──

        // 导出一份带表头的 csv（Excel 直接打开不乱码：加 BOM）。
        // 蓝牙那份接在后面单独一段：列数跟设备数有关，混在一张表里只会逼人多读几列空白
        public static bool Export(string path)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("时间,电量%,温度℃,接通电源");
                foreach (Point p in _list)
                {
                    sb.Append(p.Time.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(p.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(double.IsNaN(p.TempC) ? "" : p.TempC.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
                    sb.AppendLine(p.Ac ? "1" : "0");
                }

                if (_bt.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("时间,设备地址,设备名称,蓝牙电量%,设备类型");
                    foreach (BtPoint b in _bt)
                    {
                        sb.Append(b.Time.ToString(StampFormat, CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(Clean(b.Id)).Append(',');
                        sb.Append(Clean(b.Label)).Append(',');
                        sb.Append(b.Percent.ToString(CultureInfo.InvariantCulture)).Append(',');
                        sb.AppendLine(Sensors.KindName(b.Kind));
                    }
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                return true;
            }
            catch { return false; }
        }

        // 按设备地址批量删蓝牙历史行（本机 history.csv 一行都不动）。
        // 删完立刻重写文件，内存和磁盘保持一致，不用重启。
        public static int DeleteDevices(IEnumerable<string> ids)
        {
            if (ids == null) return 0;
            HashSet<string> set = new HashSet<string>(ids);
            if (set.Count == 0) return 0;
            int removed = _bt.RemoveAll(delegate (BtPoint b) { return set.Contains(b.Id); });
            if (removed > 0) Rewrite();
            return removed;
        }

        // 清空只删本程序追加的内容，文件留着（避免和别的程序抢删除权限）
        public static bool Clear()
        {
            try
            {
                _list.Clear();
                _bt.Clear();
                _trimmedSinceSave = 0;
                File.WriteAllText(_path, "", new UTF8Encoding(false));
                if (_btPath.Length > 0) File.WriteAllText(_btPath, "", new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }
    }
}
