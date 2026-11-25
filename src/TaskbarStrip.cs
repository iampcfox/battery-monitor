// 任务栏窄条：把窗口 SetParent 到 Shell_TrayWnd，跟 TrafficMonitor 是同一个路子
// （不是 deskband，不需要 COM 注册，也不用 32/64 位各一份）。
//
// 为什么要挂进去而不是浮在上面：
//   任务栏自动隐藏时它会跟着一起滑走、全屏游戏时它跟着任务栏一起被盖住，
//   这两件事都由父窗口代劳，不用自己去猜"现在该不该显示"。
//
// 画法沿用卡片那套：32bpp ARGB 位图 + UpdateLayeredWindow 逐像素透明，
// 这样背景是真的透，下面是 Win11 的云母效果也能透出来，不用配什么近似底色。
// 万一 ULW 在子窗口上不认（返回 false），自动退化成"实底 + 普通 WM_PAINT"。
//
// 位置：思路来自 TrafficMonitor 的两套策略（ClassicalTaskbarDlg / Win11TaskbarDlg），落点改成绝对定位。
//   · 经典式任务栏（Explorer Patcher 把 Win11 换回 Win10 结构，有 ReBar+MSTaskSwWClass）：
//     先把窄条落点定在"贴托盘左缘"，再反推容器该多宽（右缘落在窄条左边 2px），
//     只调宽度、不动左缘（SetWindowPos + SWP_NOMOVE）—— 左缘归 shell 和 TM 管。
//   · 原生 Win11 任务栏（Shell_TrayWnd 下有 DesktopWindowContentBridge）：不压缩，贴托盘左缘空隙。
//     TM 官方 Help.md 承认这条分支在任务栏占满时也会重叠，无解。
// 压缩前后都写标记文件，进程崩溃/Explorer 重启后能收敛还原。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryMonitor
{
    internal sealed class TaskbarStrip : Form
    {
        // ── Win32 常量 ──────────────────────────────────────────────────────
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_LAYERED = 0x00080000;

        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_FRAMECHANGED = 0x0020;
        private const int SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const int ULW_ALPHA = 2;
        private const byte AC_SRC_OVER = 0;
        private const byte AC_SRC_ALPHA = 1;

        // 背景填充的 alpha：0 = 鼠标穿透（只有笔画能点），1 = 整块可点且肉眼看不出来
        private const int HIT_ALPHA = 1;

        // 右键相关消息：WinForms 的 Click 只认左键，右键必须从窗口消息层接
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_RBUTTONUP = 0x0205;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        private delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr param);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr h, StringBuilder cls, int max);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr h);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr h, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr h, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(IntPtr h, int x, int y, int cx, int cy, bool repaint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr h, ref POINT p);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr h, ref POINT p);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr h);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr h);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr h, IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        // pptDst 传 IntPtr.Zero：位置全交给 MoveWindow，只更新内容
        [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int flags);

        // ── 状态 ────────────────────────────────────────────────────────────
        private struct Seg
        {
            public string Text;
            public string Num;       // 数字部分（电量和温度）：三列对齐时单独画
            public string Unit;      // 符号部分（% / ℃）：单独画在同一列
            public bool Bold;
            public bool Glyph;
            public int Line;         // 0 = 上行（电池胶囊 + 电量），1 = 下行（温度 / 功率）

            public int Icon;         // RowIcon 的值；0(None) = 没有行首图标，就是一段纯文字
            public string Pre;       // 段首的小标签（"TEM"）：比正文小一档、淡一档，跟数值一起裁剪
            public bool Bt;          // true = 一台蓝牙设备（图标 + 电量），排在右边那一列
            public int Pct;          // 蓝牙设备电量；-1 = 连上了但没上报（手柄常见）
            public int DevIdx;       // 蓝牙段：这台在 Sample.Bt 里的下标（悬停提示要拿型号）
            public int Kind;         // 裁剪顺序用：见 K_ 常量
        }

        // 内容类别：空间不够时按"信息量"从低到高砍，而不是简单掐尾巴
        private const int K_GLYPH = 0, K_PCT = 1, K_TEMP = 2, K_WATT = 3, K_BT = 4;

        private readonly Config _cfg;
        private readonly FloatForm _card;
        private readonly Timer _timer;

        private IntPtr _taskbar;

        // ── 任务按钮区占位（思路来自 TrafficMonitor 经典分支，落点改成绝对定位）──
        // TM 的做法：把 MSTaskSwWClass 压掉"自己那个宽度"，然后贴到压缩后的右缘 + 2。
        // 我们的做法：**落点先定（贴托盘左缘），容器宽度由落点反推**，宽度调整只走
        // SetContainerWidth（MoveWindow，左缘用每轮实测值），不再有"快照还原再重压"那一步。
        //
        // 一条硬规则：**窄条内容永远画全**。电量、温度、蓝牙一台都不减，也不 Hide。
        // 容器压不动（被 shell 还原、被 TM 挤、坐标飘到屏幕外）时，宁可让窄条盖住
        // 最后一个任务按钮的尾巴 —— "只剩本机电量"那种"优化"是 bug，不是功能。
        private bool _nativeWin11;         // true = 原生 Win11 任务栏（有 DesktopWindowContentBridge）→ 不压缩
        private bool _checkedTaskbarType;  // 分支判断只做一次，任务栏重建时重置
        private int _oriW;                 // MSTaskSwWClass 未压缩时的原始宽度（TM 的 m_rcMinOri）
        private int _oriX;                 // 同一时刻的左缘（还原要连左缘一起回去，见 RestoreNatural）
        private bool _driftChecked;        // 启动时查过一次"左缘漂没漂"
        private int _oriH;                 // 容器原始高度
        private bool _compressed;          // 当前是否处于压缩态
        private int _plannedW;             // 上次规划时的窄条宽度（变了才重排）
        private int _plannedListW;         // 上次压缩后按钮容器的实际宽度（shell 把它还原了就要重压）
        private bool _forceAdjust;         // 这一拍强制重排（首次 / 布局变化）
        private readonly string _reservePath = Path.Combine(AppPaths.Dir, "battery-monitor.taskbar");

        private bool _embedded;          // true = 已经是任务栏的子窗口
        private bool _alpha = true;      // false = ULW 不可用，改用实底绘制
        private bool _light = true;      // 任务栏是浅色主题
        private int _w, _h;              // 当前尺寸（像素）
        private int _px, _py;            // 当前在父窗口里的左上角
        private float _u = 1f;           // 缩放单位（dpi × 界面缩放）
        private string _key = "";        // 内容指纹，没变就不重画
        private bool _placed;            // 位置是否已经摆过（第一次必须摆）
        private Font _fBold, _fNorm, _fTiny, _fTinyBold;
        private float _inkBold = -1f;    // 粗体的墨迹中心 → DrawString 矩形上边
        private float _inkNorm = -1f;    // 常规字重同上
        private float _inkTiny = -1f;    // 行首小标签（TEM）同上
        private float _inkTinyBold = -1f;

        // 诊断：只记录"原因变化"，同一原因不刷屏。排查完可以整体删掉
        private string _lastDiag = "";
        private void Diag(string msg)
        {
            if (_card == null || msg == _lastDiag) return;
            _lastDiag = msg;
            _card.WriteDiag("tb " + msg);
        }

        // 蓝牙设备多、任务栏那块空位只够显示一台时，轮流展示（每 BtTurnMs 换一台）。
        // 任务栏空隙通常只有 100px 上下，两台设备全画必然超宽；轮流看比"只显示第一台、
        // 其余永远看不到"更有用，图标本身就说明了当前这是哪台设备。
        private int _btPage;
        private DateTime _btPageAt;
        private const int BtTurnMs = 6000;

        // 右侧蓝牙排成几列：一列最多上下两台，第三台另起一列。
        // 任务栏就那个高度，一列塞三台 12.5px 的字会挤到看不清；两列共 4 台是宽度上限，
        // 再多就靠 _btPage 轮流露脸（换台逻辑见 Sync）
        private const int BtRowsPerCol = 2;
        private const int MaxBtCols = 2;
        private const int MaxBtShow = BtRowsPerCol * MaxBtCols;

        // ── 悬停提示 ────────────────────────────────────────────────────────
        // 任务栏那一行放不下型号（"QCY ArcBuds Lite" 比整条还宽），鼠标停上去才展开讲：
        // 本机那一块报电量 / 状态 / 温度，每台蓝牙设备各报型号 / 电量 / 地址。
        // 命中区不另算，就是"刚画出来的那些格子"—— 布局改了提示跟着改，不会有错位。
        private sealed class HitBox
        {
            public RectangleF Rect;                  // 窗口内坐标
            public string Title = "";
            public string[] Lines = new string[0];
            public BtDevice Dev;                     // 非空 = 蓝牙格：提示内容悬停时现算（版本号是后台查的）
        }

        private readonly List<HitBox> _hits = new List<HitBox>();
        private StripTip _tip;
        private string _tipSig = "";   // 上一次塞给提示卡的内容指纹（没变就不重设，免得每 120ms 重画）
        private Timer _hover;
        private Timer _z;

        internal TaskbarStrip(Config cfg, FloatForm card)
        {
            _cfg = cfg;
            _card = card;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "BatteryMonitorStrip";
            Cursor = Cursors.Default;   // 左键不做事了，就别给手型——手型是有链接可点的意思
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer, true);
            _light = IsLightTaskbar();

            _timer = new Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { Sync(); };

            // 悬停靠轮询光标，不靠鼠标消息：这个窗口是 SetParent 到 explorer 的子窗口，
            // 消息得绕一圈才到，任务栏自己又爱抢 hit-test，直接用 GetCursorPos 最省事
            _hover = new Timer();
            _hover.Interval = 120;
            _hover.Tick += delegate { OnHoverTick(); };

            // z 序看门狗：比 1 秒的 Sync 快一个量级，任务栏一抬上来就能在 1~2 帧内补回去
            _z = new Timer();
            _z.Interval = 50;
            _z.Tick += delegate { try { KeepAbove(); } catch { } };   // 定时器里绝不能抛，抛一次整个进程就没了

            // 先建句柄再挂父窗口：Show() 之前就位，避免"先闪一下再跳进任务栏"
            Attach();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Attach();                    // 句柄重建（explorer 重启后）也要重新挂
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timer.Start();
            _hover.Start();
            _z.Start();
            Sync();
        }

        // 条被藏起来（自动隐藏 / 全屏 / 任务栏重建）时，提示也必须跟着走，
        // 否则会有一张"没主的卡片"留在屏幕上
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { HideTip(); RestoreContainer(); }   // 藏起来时把按钮区还回去（TM 同款）
        }

        // ── 挂到任务栏 ──────────────────────────────────────────────────────
        // 返回 false = 挂不进去（系统不让 SetParent），退回"浮在上面"的模式，
        // 那时就得自己判断自动隐藏和全屏了，跟 TrafficMonitor 的兜底分支一样。
        private bool Attach()
        {
            if (IsDisposed) return false;
            IntPtr hwnd = Handle;
            if (hwnd == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            _selfPid = pid;

            _taskbar = FindWindow("Shell_TrayWnd", null);
            if (_taskbar == IntPtr.Zero)
            {
                _embedded = false;
                return false;
            }

            if (GetParent(hwnd) == _taskbar)
            {
                _embedded = true;
                return true;
            }

            // 先改成 CHILD 样式再 SetParent，否则 Windows 会留一条标题栏/边框的怪相
            int style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
            SetParent(hwnd, _taskbar);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            // SetParent 成功时返回"原来的父窗口"，顶层窗口的父是 NULL，光看返回值分不清成败，
            // 所以这里直接回读 GetParent 确认
            _embedded = GetParent(hwnd) == _taskbar;
            if (!_embedded)
            {
                // 兜底：当普通置顶窗口浮在任务栏上
                SetWindowLong(hwnd, GWL_STYLE, style);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            _px = _py = int.MinValue;      // 强制下一次重新定位
            return _embedded;
        }

        // explorer 重启后任务栏是新的窗口，得重新挂一次
        // 设置页改完"窄条显示哪几项"后立刻重画一次（不然要等下一拍 1 秒的定时器）
        internal void Poke()
        {
            try { _key = ""; Sync(); } catch { }
        }

        internal void Reset()
        {
            try
            {
                _taskbar = IntPtr.Zero;
                _px = _py = int.MinValue;
                _key = "";
                if (!IsWindow(Handle)) return;   // 句柄没了就等它自己重建
                Attach();
                Sync();
            }
            catch { }
        }

        // ── 每次刷新：定位 + 内容 ────────────────────────────────────────────
        // 任务按钮容器不在 Shell_TrayWnd 的直属子窗口里，而是：
        //   Shell_TrayWnd → ReBarWindow32 → MSTaskSwWClass
        // 只从任务栏直接找会找不到，占位自然也做不成
        private IntPtr FindTaskList()
        {
            if (_taskbar == IntPtr.Zero) return IntPtr.Zero;
            try
            {
                IntPtr rebar = FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
                if (rebar != IntPtr.Zero)
                {
                    IntPtr list = FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
                    if (list != IntPtr.Zero) return list;
                }
                return FindWindowEx(_taskbar, IntPtr.Zero, "MSTaskSwWClass", null);
            }
            catch { return IntPtr.Zero; }
        }

        private uint _selfPid;            // 自己的进程号：悬浮卡也会压在这一行上，别把自己当浮条
        private bool _dumped;             // 把任务栏那一行上的窗口写进日志（首次 + 浮条位置大幅变化时）
        private int _dumpedAt;

        // ── 任务栏类型判断（照抄 TM 的 CheckWindows11Taskbar）────────────────
        // Shell_TrayWnd 下有 Windows.UI.Composition.DesktopWindowContentBridge = 原生 Win11 任务栏，
        // 这条路 TM 也不压缩，只贴托盘左缘；没有它（EP 换回 Win10 结构，有 ReBar+MSTaskSwWClass）
        // 就走经典分支，压缩按钮容器。
        private void CheckTaskbarType()
        {
            if (_checkedTaskbarType && IsWindow(_taskbar)) return;
            _checkedTaskbarType = true;
            _nativeWin11 = false;
            try
            {
                if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000)
                    _nativeWin11 = FindWindowEx(_taskbar, IntPtr.Zero,
                        "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero;
            }
            catch { }
            Diag("taskbar type: " + (_nativeWin11 ? "win11-native" : "classic"));
        }
        // 把按钮容器调成 targetW：**只改右缘，左缘保持实测位置不动**。
        // 两个坑：
        //  ① 不能用 SetWindowPos + SWP_NOMOVE：宽度是改了，但 shell 不会重排容器里
        //      MSTaskListWClass 的按钮，于是按钮停在原地、溢出到窄条底下
        //     （日志里 btn=986 而 listRight=919 就是它），后续每拍都"放不下"一路减段。
        //     必须用 MoveWindow 才会触发重排。
        //  ② x 不能写死（TM 的 m_left_space 或首次快照）：TM 的两个窗口占着 96~362，
        //     容器左缘由它决定。写死 96 就会把容器拉回去压在 TM 身上，shell 再推开 ——
        //     这就是"越压越左、右缘对不准托盘"。所以每轮都拿**当前实测左缘**，
        //     等于只动右缘。
        private bool SetContainerWidth(IntPtr list, int targetW)
        {
            if (list == IntPtr.Zero || targetW < 60) return false;
            try
            {
                RECT r;
                if (!GetWindowRect(list, out r)) return false;
                int cur = r.Right - r.Left;
                if (Math.Abs(cur - targetW) <= 2) return true;   // 已经到位，别碰它
                int h = r.Bottom - r.Top;
                if (h <= 0) h = 40;
                IntPtr parent = GetParent(list);

                int req = targetW;
                for (int i = 0; i < 3; i++)
                {
                    RECT rr;
                    if (!GetWindowRect(list, out rr)) break;
                    POINT p = new POINT();
                    p.X = rr.Left; p.Y = rr.Top;
                    if (parent != IntPtr.Zero) ScreenToClient(parent, ref p);
                    if (!MoveWindow(list, p.X, p.Y, req, h, true)) break;
                    RECT r2;
                    if (!GetWindowRect(list, out r2)) break;
                    int got = r2.Right - r2.Left;
                    if (Math.Abs(got - targetW) <= 2) { WriteReserveMarker(); return true; }
                    req += targetW - got;                        // 差多少补多少，收敛三轮
                    if (req < 60) break;
                }
                WriteReserveMarker();
                return true;
            }
            catch (Exception ex) { Diag("setw ex: " + ex.Message); return false; }
        }

        // 把容器整块摆回"自然位置"：左缘回到 ReBar 起点、宽度铺到 ReBar 右缘。
        // **必须连左缘一起还原**。以前只还原宽度，一旦上一次是被强杀的（左缘已经漂到中间），
        // 就会在漂移后的左缘上套一个自然宽度 → 容器右缘越过托盘 → shell 重排时把按钮区
        // 推到中间，任务栏左边留一大片空白，窄条也只剩 59px 的"只剩电量"。
        private void RestoreNatural(IntPtr list)
        {
            if (list == IntPtr.Zero) return;
            try
            {
                RECT lr;
                if (!GetWindowRect(list, out lr)) return;
                int h = Math.Max(20, lr.Bottom - lr.Top);
                int x = _oriX, w = _oriW;
                if (x <= 0 || w < 60)
                {
                    RECT rb;
                    IntPtr rebar = FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
                    if (rebar == IntPtr.Zero || !GetWindowRect(rebar, out rb)) return;
                    x = rb.Left + 2;
                    w = rb.Right - rb.Left - 2;
                }
                IntPtr parent = GetParent(list);
                POINT p = new POINT();
                p.X = x; p.Y = lr.Top;
                if (parent != IntPtr.Zero) ScreenToClient(parent, ref p);
                MoveWindow(list, p.X, p.Y, w, h, true);
            }
            catch { }
        }

        // 还原到"没压缩"的样子：宽度和左缘一起回去，左缘交给 shell 的前提是它本来就在自然位置
        private void RestoreContainer()
        {
            if (!_compressed) { _compressed = false; return; }
            try { RestoreNatural(FindTaskList()); } catch { }
            _compressed = false;
            _plannedListW = 0;
            ClearReserveMarker();
        }

        // 标记文件记"左缘,宽度"两个数：只记宽度的话，左缘漂了就没法还原（老版本就是这么坏的）
        private void WriteReserveMarker()
        {
            try
            {
                File.WriteAllText(_reservePath,
                    _oriX.ToString(CultureInfo.InvariantCulture) + "," + _oriW.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }

        private bool ReadReserveMarker(out int x, out int w)
        {
            x = 0; w = 0;
            try
            {
                if (!File.Exists(_reservePath)) return false;
                string[] parts = File.ReadAllText(_reservePath).Trim().Split(',');
                if (parts.Length >= 2)
                {
                    int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x);
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out w);
                }
                else if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) x = 0;  // 老格式：只有宽度
                return w >= 60;
            }
            catch { return false; }
        }

        // "左边有没有别人占着"：TrafficMonitor 那种第三方 band 就压在 ReBar 左边，
        // 这时候左缘本来就不在自然位置，不能硬掰回去。
        private bool ForeignBandBetween(int from, int to)
        {
            if (to - from < 8) return false;
            bool hit = false;
            EnumProc cb = delegate(IntPtr h, IntPtr p)
            {
                try
                {
                    if (h == IntPtr.Zero || h == Handle) return true;
                    if (!IsWindowVisible(h)) return true;
                    string cls = ClassOf(h);
                    if (IsTaskbarPart(cls)) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    if (r.Right - r.Left < 20) return true;      // 细缝、零宽度的占位窗不算
                    if (r.Right > from && r.Left < to) { hit = true; return false; }
                }
                catch { }
                return true;
            };
            try { EnumChildWindows(_taskbar, cb, IntPtr.Zero); } catch { }
            return hit;
        }

        private void ClearReserveMarker()
        {
            try { if (File.Exists(_reservePath)) File.Delete(_reservePath); } catch { }
        }

        // 崩溃兜底：上次压缩没还原（进程被强杀）就先整块摆回自然位置。
        // 判据是"左缘或宽度跟标记对不上"，两个都得看 —— 只看宽度会漏掉"左缘漂到中间"那种。
        private void SelfHealReserve()
        {
            try
            {
                int sx, sw;
                if (!ReadReserveMarker(out sx, out sw)) { ClearReserveMarker(); return; }
                IntPtr list = FindTaskList();
                if (list != IntPtr.Zero)
                {
                    RECT r;
                    if (GetWindowRect(list, out r))
                    {
                        bool widthOff = r.Right - r.Left < sw - 8;
                        bool leftOff = sx > 0 && Math.Abs(r.Left - sx) > 8;
                        if (widthOff || leftOff)
                        {
                            _oriX = sx; _oriW = sw;
                            RestoreNatural(list);
                            Diag("self heal: " + (r.Right - r.Left) + " -> " + sw + " @" + (sx > 0 ? sx : -1));
                        }
                    }
                }
                ClearReserveMarker();
            }
            catch { }
        }

        private void Sync()
        {
            try
            {
                if (IsDisposed || !IsWindow(Handle)) { Hide(); return; }

                if (!IsWindow(_taskbar))
                {
                    _taskbar = FindWindow("Shell_TrayWnd", null);
                    if (_taskbar == IntPtr.Zero) { Diag("Shell_TrayWnd not found"); Hide(); return; }
                    if (GetParent(Handle) != _taskbar) { Attach(); }
                    _checkedTaskbarType = false;       // 新任务栏：重新判断经典 / 原生 Win11
                    _driftChecked = false;             // 新任务栏：左缘漂移要重新查一遍
                    SelfHealReserve();                 // 上次压缩没还原就收敛回来
                    _oriW = 0; _compressed = false;    // 新任务栏的容器宽度要重新量
                    _forceAdjust = true;               // 强制重排一次
                }
                CheckTaskbarType();

                // _embedded 标志每次都以实际父子关系为准 —— explorer 重启、句柄重建时
                // 标志会和现实短暂错位，错位期间坐标换算方向反了，窗口会被摆到屏幕外
                bool reallyEmbedded = GetParent(Handle) == _taskbar;
                if (reallyEmbedded != _embedded)
                {
                    Diag("embed flag fixed: " + reallyEmbedded);
                    _embedded = reallyEmbedded;
                    _px = _py = int.MinValue;        // 下一拍强制重摆
                }

                RECT tb;
                if (!GetWindowRect(_taskbar, out tb)) { Diag("GetWindowRect(taskbar) failed"); Hide(); return; }
                int tbH = tb.Bottom - tb.Top;
                if (tbH <= 0) { Diag("taskbar height<=0"); Hide(); return; }

                // 任务栏被自动隐藏时整个窗口会滑到屏幕外：子窗口跟着走，这里只需要别画
                if (!IsWindowVisible(_taskbar) || !TouchesAnyScreen(tb)) { Diag("taskbar hidden/offscreen"); Hide(); return; }
                if (!Visible) Show();

                uint dpi = 96;
                try { dpi = GetDpiForWindow(_taskbar); } catch { }
                if (dpi < 72 || dpi > 480) dpi = 96;
                float scale = _cfg.Scale <= 0 ? 1f : Math.Min(1.6f, Math.Max(0.8f, _cfg.Scale));
                _u = dpi / 96f * scale;

                // 任务栏那条空隙里可能已经有别人的浮条（TrafficMonitor 就是），
                // 只按"贴托盘左边"算必然撞上它，或者干脆被挤到它右边去。
                int gapPx = (int)Math.Round(6 * _u);
                int trayLeft = tb.Right - (int)Math.Round(10 * _u);
                IntPtr tray = FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
                RECT tr;
                if (tray != IntPtr.Zero && GetWindowRect(tray, out tr) && tr.Right > tr.Left) trayLeft = tr.Left;

                int listLeft = tb.Left + (int)Math.Round(2 * _u);
                int listRight = 0;
                IntPtr list = FindTaskList();
                RECT lr = new RECT();
                bool haveList = list != IntPtr.Zero && GetWindowRect(list, out lr) && lr.Right > lr.Left;
                if (haveList) { listLeft = lr.Left; listRight = lr.Right; }

                // 启动自愈（只做一次）：上一次要是被强杀的，容器左缘可能被留在中间 ——
                // 表现就是任务栏左边一大片空白、窄条只剩 59px。左边没有别人的 band 占着，
                // 就把容器整块摆回自然位置，让后面的压缩从干净状态开始。
                if (haveList && !_driftChecked)
                {
                    _driftChecked = true;
                    RECT rb;
                    IntPtr rebar = FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
                    if (rebar != IntPtr.Zero && GetWindowRect(rebar, out rb) && listLeft > rb.Left + 8
                        && !ForeignBandBetween(rb.Left, listLeft))
                    {
                        Diag("left drift: " + listLeft + " -> " + (rb.Left + 2));
                        int keepX = _oriX, keepW = _oriW;
                        _oriX = rb.Left + 2; _oriW = rb.Right - rb.Left - 2;
                        RestoreNatural(list);
                        _oriX = keepX; _oriW = keepW;
                        _compressed = false;
                        _forceAdjust = true;
                        if (GetWindowRect(list, out lr) && lr.Right > lr.Left)
                        { listLeft = lr.Left; listRight = lr.Right; }
                    }
                }
                if (listRight <= 0) listRight = listLeft;

                // 任务按钮实际排到哪儿（容器尾部常有余量，可以塞窄条，一个字都不动任务栏）
                int btnRight = 0;
                if (haveList) TaskButtons(list, lr, tb, tbH, out btnRight);
                if (btnRight <= 0) btnRight = listRight;

                // 右侧浮条（TrafficMonitor）的实际矩形。它是 ReBar 里的一个 band，会跟着任务按钮区
                // 一起被 shell 挪动 —— 所以它的位置只能用来"放"，绝不能用来反推压缩量。
                int wl = 0, wr = 0;
                bool hasWidget = FindWidget(listLeft, trayLeft - gapPx, tb, out wl, out wr);
                int rightEdge = trayLeft - gapPx;
                // 容器被搬到屏幕外 / 压成 0 宽时这里会算出负数 —— 按任务栏左缘兜底，
                // 绝不 Hide：藏起来等于内容全丢，比盖住按钮还糟。
                if (rightEdge - listLeft < 20) listLeft = tb.Left + 2;

                // 浮条位置大变（多半是任务栏布局换了）就把这一行重新记一遍，方便回头对账
                if (!_dumped || Math.Abs(wl - _dumpedAt) > 30)
                {
                    _dumped = true;
                    _dumpedAt = wl;
                    DumpRow(tb, listLeft, rightEdge);
                }

                List<Seg> segs = BuildSegs();
                int w = 0, h = tbH;
                Measure(segs, tbH, out w, out h);
                int btBefore = CountKind(segs, K_BT);
                Sample smp = _card == null ? null : _card.Current;
                int btTotal = BtWithPower(smp == null ? null : smp.Bt).Count;

                // 多台蓝牙设备、空位只放得下一台：开始轮流展示，每 BtTurnMs 换一台。
                // 换台不用手动重画 —— 下一拍 BuildSegs 会按新的 _btPage 重新排，
                // 文字/图标一变 KeyOf 就变，自己就重绘了。
                // 设备台数超过两列装得下的数（4 台）就轮播：宽度再摊下去任务栏放不下
                if (btTotal > MaxBtShow || (btBefore > 1 && CountKind(segs, K_BT) <= 1))
                {
                    DateTime now = DateTime.UtcNow;
                    if (_btPageAt == DateTime.MinValue) _btPageAt = now;
                    else if ((now - _btPageAt).TotalMilliseconds >= BtTurnMs)
                    {
                        _btPageAt = now;
                        _btPage++;
                    }
                }
                else _btPageAt = DateTime.MinValue;

                // ── 落点 ───────────────────────────────────────────────────────
                // 硬规则：**内容永远画全**。电量、温度、蓝牙一台都不许减 ——
                // 容器被 shell 还原、被 TM 挤、坐标飘到屏幕外，都只影响"容器压到哪"，
                // 不影响窄条自己多宽。所以这里没有减段循环、也没有 Hide，只有一个落点：
                // 贴托盘左缘，实在没空就盖住最后一个任务按钮的尾巴。
                int wantX = rightEdge - w;
                // 右边真有别人的浮条（TM 那种）就让开；但它坐标得讲得通才算数 ——
                // explorer 重启后 TM 的 band 会飘到 32861 这种位置，当真就没法摆了。
                if (hasWidget && wl >= trayLeft - 400 && wl > listLeft)
                    wantX = Math.Min(wantX, wl - gapPx - w);
                if (wantX < tb.Left) wantX = tb.Left;

                // 容器位置可信吗：宽度得像个容器、得落在任务栏这一行里。
                // 不可信（按钮区被搬到屏幕外、压成 0 宽）就别碰它，窄条照常按托盘摆。
                bool listSane = haveList && listLeft >= tb.Left - 4 && listLeft < trayLeft - 60
                    && listRight - listLeft > 60;
                if (haveList && !listSane) Diag("list insane: [" + listLeft + "," + listRight + "]");

                // shell 会在托盘重排（点隐藏图标那个箭头、托盘图标增减）时把容器宽度还原成自然宽度，
                // 这时窄条宽度 w 没变，光看 w 就不会重压 —— 按钮顶着容器伸到托盘边，把窄条挤走。
                // 所以容器宽度对不上、且按钮区真的伸进了窄条要占的那段，也算"要重排"。
                bool listReset = listSane && _compressed && _plannedListW > 60
                    && Math.Abs((listRight - listLeft) - _plannedListW) > 2
                    && listRight > rightEdge - w;
                bool replan = _forceAdjust || w != _plannedW || listReset;
                if (replan)
                {
                    if (listReset && w == _plannedW) Diag("replan: list reset " + (listRight - listLeft) + " vs " + _plannedListW);
                    _plannedW = w; _forceAdjust = false;
                }

                if (replan && !_nativeWin11 && listSane)
                {
                    if (_oriW < 60)
                    {
                        // 第一次：记下压缩前的自然位置和宽度，退出/隐藏时按它还原
                        _oriW = Math.Max(60, listRight - listLeft);
                        _oriX = listLeft;
                        _oriH = Math.Max(20, lr.Bottom - lr.Top);
                        WriteReserveMarker();
                    }
                    // 容器右缘落在"落点 − 2"，宽度由此反推；上限是自然宽度，
                    // 别反过来把容器撑得比原来还宽（托盘识别飘一下就会那样）。
                    int targetW = Math.Max(60, (wantX - 2) - listLeft);
                    if (_oriW > 60 && targetW > _oriW) targetW = _oriW;
                    if (SetContainerWidth(list, targetW))
                    {
                        _compressed = true;
                        // 动完容器必须重测：按钮会跟着重排，浮条也可能被 shell 挪一段
                        if (GetWindowRect(list, out lr) && lr.Right > lr.Left)
                        { listLeft = lr.Left; listRight = lr.Right; }
                        int cr;
                        TaskButtons(list, lr, tb, tbH, out cr);
                        if (cr > 0) btnRight = cr;
                        int nl, nr;
                        hasWidget = FindWidget(listLeft, rightEdge, tb, out nl, out nr);
                        if (hasWidget) { wl = nl; wr = nr; }
                        _plannedListW = listRight - listLeft;
                        if (_plannedListW < 60) _plannedListW = 0;
                    }
                }

                // 落点定死：内容有多宽就画多宽，压不住按钮就压在按钮上面
                int x = wantX;

                // 窗口宽度就是紧凑内容宽度；按 TM 的按钮区方案，窄条不再摊开。
                int winW = w;
                Diag("ok: x=" + x + " w=" + w + " winW=" + winW
                    + " list=[" + listLeft + "," + listRight + "] btn=" + btnRight
                    + (hasWidget ? " tm=[" + wl + "," + wr + "]" : " tm=none")
                    + " win11=" + _nativeWin11 + " compressed=" + _compressed
                    // rg = 窄条右缘到托盘那条缝的余量：正常应该是 0，大于 0 说明右边还空着
                    + " rg=" + (rightEdge - (x + w))
                    + " tray=" + trayLeft);
                int y = tb.Top + (tbH - h) / 2;

                if (winW != _w || h != _h) { _w = winW; _h = h; _key = ""; }

                POINT pt;
                if (_embedded)
                {
                    // 不用 ScreenToClient：它的成败依赖 _embedded 标志跟实际父子关系一致，
                    // 而这个一致性在某些时机（explorer 重启、句柄重建）会短暂破掉，
                    // 出过"y 按屏幕坐标写进了客户区、窗口落到屏幕外 1459"的事故。
                    // 直接取任务栏客户区原点的屏幕坐标做减法，跟标志无关，永远正确
                    POINT org = new POINT();
                    ClientToScreen(_taskbar, ref org);
                    pt.X = x - org.X;
                    pt.Y = y - org.Y;
                }
                else { pt.X = x; pt.Y = y; }

                bool moved = pt.X != _px || pt.Y != _py || !_placed;
                if (moved)
                {
                    MoveWindow(Handle, pt.X, pt.Y, _w, _h, !_alpha);
                    _px = pt.X; _py = pt.Y; _placed = true;
                    _key = "";
                }

                // 挂在任务栏里时把自己顶到兄弟窗口最上层：任务栏那套 XAML 内容也是它的子窗口，
                // 不主动抬一下，某些版本上会被盖在底下（看不见但确实在画）
                if (_embedded)
                {
                    SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                else
                {
                    // 浮在任务栏上时插到 Shell_TrayWnd 正上方，别顶到 HWND_TOPMOST 最顶层 ——
                    // 顶到最顶会盖住托盘抽屉、右键菜单这些"本来就该在上面"的窗口
                    SetWindowPos(Handle, ZAnchor(), 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }

                // 落点复核：窗口实际位置必须落在任务栏这一行、且就是想要的 x/y。
                // 以前出过 pt 固定不变 → moved=false → 错误位置永远不重摆的事故，
                // 所以这步每拍都跑，发现漂移就把 _px/_py 打掉，下一拍强制重摆
                RECT chk;
                if (GetWindowRect(Handle, out chk))
                {
                    bool onRow = chk.Top >= tb.Top - 4 && chk.Bottom <= tb.Bottom + 4
                        && chk.Left >= tb.Left - 4 && chk.Right <= tb.Right + 4;
                    bool rightSpot = Math.Abs(chk.Left - x) <= 2 && Math.Abs(chk.Top - y) <= 2;
                    if (!onRow || !rightSpot)
                    {
                        Diag("placement drift: want(" + x + "," + y + ") got("
                            + chk.Left + "," + chk.Top + " " + (chk.Right - chk.Left) + "x"
                            + (chk.Bottom - chk.Top) + ")");
                        _px = _py = int.MinValue;
                    }
                }

                string key = KeyOf(segs) + "|" + _w.ToString() + "x" + _h.ToString()
                    + "|" + (_light ? "L" : "D");
                if (key != _key) { _key = key; Render(segs); }
            }
            catch (Exception ex) { LogOnce("Sync", ex); }
        }

        private string _errTag;

        // 每类错误只记一次，免得每秒刷屏；出问题先看 battery-monitor-crash.log
        private void LogOnce(string tag, Exception ex)
        {
            if (_errTag == tag) return;
            _errTag = tag;
            Program.Trace(tag + ": " + ex);
        }

        private static void AddObstacle(List<int[]> used, RECT r, int from, int to, int pad)
        {
            int l = Math.Max(r.Left, from) - pad;
            int rr = Math.Min(r.Right, to) + pad;
            if (rr <= l) return;
            if (l < from) l = from;
            if (rr > to) rr = to;
            used.Add(new int[] { l, rr });
        }

        // ── 空位计算 ────────────────────────────────────────────────────────
        // 谁在任务栏上占了一块？现在按"类名是不是任务栏自己人"判断，不再按高度：
        // TrafficMonitor 可以铺满整条任务栏，旧的"比任务栏矮才算第三方"规则会把它漏掉，
        // 结果空位计算以为那里是空的，我们就画到它的字上面去了。
        private static string ClassOf(IntPtr h)
        {
            try
            {
                StringBuilder sb = new StringBuilder(128);
                if (GetClassName(h, sb, sb.Capacity) > 0) return sb.ToString();
            }
            catch { }
            return "";
        }

        private static bool IsTaskbarPart(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            switch (cls.ToLowerInvariant())
            {
                case "shell_traywnd":
                case "shell_secondarytraywnd":
                case "traynotifywnd":
                case "mstaskswwclass":
                case "mstasklistwclass":
                case "rebarwindow32":
                case "toolbarwindow32":
                case "trayclockwclass":
                case "trayshowdesktopbuttonwclass":
                case "syspager":
                case "traydummycontrol":
                case "netstate":
                case "start":
                case "windows.ui.core.corewindow":
                case "windows.ui.composition.desktopwindowcontentbridge":
                case "xamlexplorerhostislandwindow":
                    return true;
                default:
                    return false;
            }
        }
        // ── 右侧浮条（TrafficMonitor 那类）的矩形 ────────────────────────────
        // 以前只按"任务栏子窗口 + 高度不超过任务栏"筛，TM 设成两行时窗口比任务栏还高，
        // 被那条规则滤掉了 —— 空位计算以为右边是空地，窄条就贴到托盘上去，TM 被夹在中间。
        // 现在放宽成"跟任务栏这一行有实质重叠的第三方窗口"，再按水平相邻聚成一簇，
        // 取最靠右那一簇（TM 及其同桌的兄弟窗）当浮条。
        private bool FindWidget(int from, int to, RECT tb, out int left, out int right)
        {
            left = right = 0;
            if (to - from < 20) return false;
            int tbH = tb.Bottom - tb.Top;
            int minW = Math.Max(10, (int)Math.Round(12 * _u));
            List<int[]> spans = new List<int[]>();

            EnumProc collect = delegate(IntPtr h, IntPtr p)
            {
                try
                {
                    if (h == IntPtr.Zero || h == Handle || h == _taskbar) return true;
                    if (!IsWindowVisible(h)) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == _selfPid) return true;                 // 自己的窗口（悬浮卡 / 面板）
                    string cls = ClassOf(h);
                    if (IsTaskbarPart(cls)) return true;
                    // 气泡提示 / 右键菜单这类临时窗口也会压在任务栏上，但它们不是锚点
                    if (cls == "#32768" || string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase))
                        return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                    if (w < minW || hh < minW) return true;
                    if (hh > tbH * 3) return true;                    // 对话框 / 整窗，不是浮条
                    if (r.Bottom <= tb.Top + tbH / 4 || r.Top >= tb.Bottom - tbH / 4) return true;  // 不在这一行
                    if (w >= (to - from) * 0.9) return true;          // 铺满整块空地，不是小浮条
                    int l = Math.Max(r.Left, from), rr = Math.Min(r.Right, to);
                    if (rr - l < minW) return true;
                    spans.Add(new int[] { l, rr });
                }
                catch { }
                return true;
            };

            EnumChildWindows(_taskbar, collect, IntPtr.Zero);   // 挂进任务栏的（TM 的老路）
            EnumWindows(collect, IntPtr.Zero);                  // SetParent 到桌面的顶层工具窗

            if (spans.Count == 0) return false;
            spans.Sort(delegate(int[] a, int[] b) { return a[0].CompareTo(b[0]); });

            // 从最右那块往左合并"挨着"的（缝 < 8px）：一个工具条常常是两三个窗口
            int joint = Math.Max(6, (int)Math.Round(8 * _u));
            left = spans[spans.Count - 1][0];
            right = spans[spans.Count - 1][1];
            for (int i = spans.Count - 2; i >= 0; i--)
            {
                if (spans[i][1] < left - joint) break;
                if (spans[i][0] < left) left = spans[i][0];
                if (spans[i][1] > right) right = spans[i][1];
            }
            return true;
        }

        // 任务按钮的数量 + "实际内容右缘"。容器的窗口常常比按钮内容宽出一截（关了窗口之后
        // 那截余量 shell 不一定马上收），那截空白正好给窄条站 —— 站在 TM 左边还不用碰任务栏。
        // 注意 MSTaskSwWClass → MSTaskListWClass 这层包装窗跟容器一样大，它不是按钮，
        // 不能拿它的右缘当"按钮右缘"（否则永远没有余量）；跳过它，EnumChildWindows 会继续往下。
        private int TaskButtons(IntPtr taskList, RECT listRect, RECT tb, int tbH, out int right)
        {
            right = 0;
            if (taskList == IntPtr.Zero) return 0;
            int cnt = 0, max = 0;
            try
            {
                EnumChildWindows(taskList, delegate(IntPtr h, IntPtr p)
                {
                    try
                    {
                        if (h == Handle || !IsWindowVisible(h)) return true;
                        RECT r;
                        if (!GetWindowRect(h, out r)) return true;
                        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                        if (w < 10 || hh < 8 || hh > tbH + 6) return true;
                        if (r.Bottom <= tb.Top + 2 || r.Top >= tb.Bottom - 2) return true;
                        if (r.Left <= listRect.Left + 2 && r.Right >= listRect.Right - 2) return true;  // 包装窗
                        cnt++;
                        if (r.Right > max) max = r.Right;
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            right = max;
            return cnt;
        }

        // 一次性诊断：把"任务栏这一行上都有谁"写进日志。定位再出问题先看这一段
        private void DumpRow(RECT tb, int from, int to)
        {
            try
            {
                _card.WriteDiag(string.Format(CultureInfo.InvariantCulture,
                    "tb row: taskbar=[{0},{1}-{2},{3}] band=[{4},{5}]",
                    tb.Left, tb.Top, tb.Right, tb.Bottom, from, to));
                EnumProc dump = delegate(IntPtr h, IntPtr p)
                {
                    try
                    {
                        RECT r;
                        if (h == IntPtr.Zero || h == Handle || !GetWindowRect(h, out r)) return true;
                        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                        if (w < 6 || hh < 6) return true;
                        if (r.Bottom <= tb.Top + 2 || r.Top >= tb.Bottom - 2) return true;
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        _card.WriteDiag(string.Format(CultureInfo.InvariantCulture,
                            "tb cand {0} [{1},{2}-{3},{4}] {5}x{6} vis={7} pid={8}",
                            ClassOf(h), r.Left, r.Top, r.Right, r.Bottom, w, hh,
                            IsWindowVisible(h) ? 1 : 0, pid));
                    }
                    catch { }
                    return true;
                };
                EnumChildWindows(_taskbar, dump, IntPtr.Zero);
                EnumWindows(dump, IntPtr.Zero);
            }
            catch { }
        }

        // 【当前没有调用】定位改成"TM 左边空档 → TM 右边 → 压按钮"三段式之后，不再需要
        // 全量空档扫描；这段留着备查（万一以后要支持第二个第三方浮条，还得靠它算空档）。
        private List<int[]> FreeGaps(int from, int to, RECT tb, int tbH, IntPtr taskList)
        {
            if (to - from < 10) return new List<int[]>();

            int pad = (int)Math.Round(3 * _u);
            int span = to - from;
            List<int[]> used = new List<int[]>();

            EnumChildWindows(_taskbar, delegate(IntPtr h, IntPtr p)
            {
                try
                {
                    if (h == Handle || h == IntPtr.Zero) return true;
                    if (!IsWindowVisible(h)) return true;
                    uint cpid;
                    GetWindowThreadProcessId(h, out cpid);
                    if (cpid == _selfPid) return true;                       // 自己的窗口不算别人的地盘
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                    if (w < 6 || hh < 6) return true;                        // 0×0 的隐藏占位
                    // 任务栏自己人按类名排除；其余哪怕比任务栏还高也算障碍
                    // （TM 设成两行时窗口就比任务栏高，按高度筛会把它漏掉，窄条就画到它字上）
                    if (IsTaskbarPart(ClassOf(h))) return true;
                    if (hh > tbH * 3) return true;                           // 对话框 / 整窗，不是浮条
                    if (r.Bottom <= tb.Top + 2 || r.Top >= tb.Bottom - 2) return true;   // 不在这一行上
                    if (w > span) return true;                               // 比整块空地还宽，不是小工具条
                    AddObstacle(used, r, from, to, pad);
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            // 任务列表本身跟任务栏等高，按类名被跳过了；但里面每个任务按钮是独立子窗口，
            // 不挖掉的话"没空档就退回任务列表"那一步会直接盖住"任务管理器"这类按钮
            if (taskList != IntPtr.Zero)
            {
                EnumChildWindows(taskList, delegate(IntPtr h, IntPtr p)
                {
                    try
                    {
                        if (h == Handle || !IsWindowVisible(h)) return true;
                        RECT r;
                        if (!GetWindowRect(h, out r)) return true;
                        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                        if (w < 10 || hh < 8 || hh > tbH + 6) return true;
                        if (r.Bottom <= tb.Top + 2 || r.Top >= tb.Bottom - 2) return true;
                        AddObstacle(used, r, from, to, pad);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }

            // 第三方的浮条也可能不是任务栏的子窗口，而是顶层工具窗（SetParent 到桌面），
            // 只枚举子窗口就会漏掉它 —— 再扫一遍顶层窗口，条件完全一样
            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                try
                {
                    if (h == IntPtr.Zero || h == _taskbar || h == Handle) return true;
                    if (!IsWindowVisible(h)) return true;
                    uint cpid;
                    GetWindowThreadProcessId(h, out cpid);
                    if (cpid == _selfPid) return true;
                    RECT r;
                    if (!GetWindowRect(h, out r)) return true;
                    int w = r.Right - r.Left, hh = r.Bottom - r.Top;
                    if (w < 6 || hh < 6 || hh > tbH * 3) return true;
                    if (r.Bottom <= tb.Top + 2 || r.Top >= tb.Bottom - 2) return true;
                    if (IsTaskbarPart(ClassOf(h))) return true;
                    if (w > span) return true;
                    AddObstacle(used, r, from, to, pad);
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            used.Sort(delegate(int[] a, int[] b) { return a[0].CompareTo(b[0]); });

            List<int[]> gaps = new List<int[]>();
            int cur = from;
            for (int i = 0; i < used.Count; i++)
            {
                if (used[i][0] > cur) gaps.Add(new int[] { cur, used[i][0] });
                if (used[i][1] > cur) cur = used[i][1];
            }
            if (cur < to) gaps.Add(new int[] { cur, to });
            return gaps;
        }

        // 从右往左挑：优先找完全落在任务列表右边那块（不压任务按钮），
        // 实在没有才退回"盖在任务列表空白处"的那块
        private static int[] PickSlot(List<int[]> gaps, int need, int preferFrom)
        {
            int[] fallback = null;
            for (int i = gaps.Count - 1; i >= 0; i--)
            {
                if (gaps[i][1] - gaps[i][0] < need) continue;
                if (gaps[i][0] >= preferFrom) return gaps[i];
                if (fallback == null) fallback = gaps[i];
            }
            return fallback;
        }

        private static int[] WidestGap(List<int[]> gaps)
        {
            int[] best = null;
            for (int i = 0; i < gaps.Count; i++)
            {
                if (best == null || (gaps[i][1] - gaps[i][0]) > (best[1] - best[0])) best = gaps[i];
            }
            return best;
        }

        private static bool TouchesAnyScreen(RECT r)
        {
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                Rectangle b = Screen.AllScreens[i].Bounds;
                if (r.Right > b.Left && r.Left < b.Right && r.Bottom > b.Top && r.Top < b.Bottom) return true;
            }
            return false;
        }

        // ── 内容：左边本机（上行 电量，下行 温度 · 功率），右边一列蓝牙设备 ─────
        // 跟 TrafficMonitor 的 CPU / 内存那样上下两行；下行为空时只剩一行，电量自己垂直居中。
        // 蓝牙设备竖着排在竖线右边：一台居中，多台均分高度上下铺开 —— 横着排一台就多吃
        // 30 多像素，任务栏那块空位根本不够，竖排等于用高度换宽度
        private List<Seg> BuildSegs()
        {
            List<Seg> list = new List<Seg>();
            Sample s = _card == null ? null : _card.Current;
            if (s == null) return list;

            bool noBat = !s.HasBattery || s.Percent < 0;

            Seg g = new Seg();
            g.Glyph = true;
            g.Kind = K_GLYPH;
            list.Add(g);

            Seg p = new Seg();
            // 不用粗体：12px 的雅黑 Bold 笔画会糊成一坨（粗体是给大字号用的），
            // 任务栏这一行的信息密度靠字号和颜色就够，加粗反而比旁边的小图标还重
            p.Kind = K_PCT;
            p.Num = noBat ? "--" : s.Percent.ToString(CultureInfo.InvariantCulture);
            p.Unit = noBat || !_cfg.ShowPercentSign ? "" : "%";
            p.Text = p.Num + p.Unit;
            list.Add(p);

            // 蓝牙设备：耳机 / 键盘 / 鼠标 / 手柄，各一个"小图标 + 电量"，排在右侧那一列
            // （竖着摞，几台就几行；只有一台时在整条里垂直居中）。
            // 只放已连接的（Sample.Bt 里就只收连接中的），没上报电量的显示 "--" 而不是丢掉 ——
            // 手柄这类设备"连上了但系统不给电量"很常见，显示 -- 至少说明它还在。
            List<BtDevice> bts = BtWithPower(s.Bt);
            if (_cfg.StripBt && bts.Count > 0)
            {
                int n = bts.Count;
                int show = Math.Min(n, MaxBtShow);
                int start = ((_btPage % n) + n) % n;     // 轮播从第 _btPage 台开始，环形展开
                for (int k = 0; k < show; k++)
                {
                    BtDevice d = bts[(start + k) % n];
                    RowIcon ic = Sensors.BtKindIcon(d.Kind);
                    if (ic == RowIcon.None) ic = RowIcon.Battery;   // 认不出类型也要有个图标打头，不然就剩个裸数字
                    Seg b = new Seg();
                    b.Kind = K_BT;
                    b.Bt = true;            // 字重跟"91%"一致：全常规，不加粗
                    b.Icon = (int)ic;
                    b.Pct = d.Percent;
                    b.DevIdx = (start + k) % n;
                    b.Text = d.Percent >= 0
                        ? d.Percent.ToString(CultureInfo.InvariantCulture) + (_cfg.ShowPercentSign ? "%" : "")
                        : "--";
                    list.Add(b);
                }
            }

            if (_cfg.StripTemp && !double.IsNaN(s.TempC))
            {
                Seg t = new Seg();
                t.Line = 1;
                t.Kind = K_TEMP;
                // 温度前面写 "TEM"：下行的"42℃"和"6.2W"光看数字太像，
                // 文字标签比图标更好认 —— 任务栏这个尺寸下温度计只剩一根糊掉的竖条
                t.Pre = "TEM";
                // 温度按四舍五入取整（默认 Banker's rounding 会把 38.5 舍成 38）
                t.Num = Math.Round(s.TempC, 0, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture);
                t.Unit = "℃";
                t.Text = t.Num + t.Unit;
                list.Add(t);
            }

            if (_cfg.StripWatt && s.HasBattery)
            {
                Seg w = new Seg();
                w.Line = 1;
                w.Kind = K_WATT;
                w.Text = double.IsNaN(s.Watts)
                    ? "0.0W"
                    : string.Format(CultureInfo.InvariantCulture, "{0:0.0}W", s.Watts);
                list.Add(w);
            }
            return list;
        }
        // 窄条只画"报了电量"的蓝牙设备：没电量的（手柄这一类）塞进窄条也只剩一个 "--"，
        // 用户明确要求这类只在面板的设备电量页里看，不占任务栏这块地方。
        // 悬浮窗那边同一规则，见 CardRender.VisibleBt。
        private static List<BtDevice> BtWithPower(List<BtDevice> src)
        {
            List<BtDevice> list = new List<BtDevice>();
            if (src == null) return list;
            for (int i = 0; i < src.Count; i++)
            {
                BtDevice d = src[i];
                if (d.Percent >= 0 && d.Percent <= 100) list.Add(d);
            }
            return list;
        }

        private static int CountKind(List<Seg> segs, int kind)
        {
            int n = 0;
            for (int i = 0; i < segs.Count; i++) if (segs[i].Kind == kind) n++;
            return n;
        }

        // 主体有几行：蓝牙设备不算（它们独占右边一列），0/1 行都算一行
        private static int MainLines(List<Seg> segs)
        {
            for (int i = 0; i < segs.Count; i++)
                if (segs[i].Kind != K_BT && segs[i].Line > 0) return 2;
            return 1;
        }

        private static string KeyOf(List<Seg> segs)
        {
            string k = "";
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                k += s.Glyph ? "#" : (s.Icon != 0 ? "i" + s.Icon.ToString(CultureInfo.InvariantCulture) : "")
                    + (s.Pre ?? "") + "|" + s.Text + (s.Bold ? "*" : "") + "@" + s.Line + "/";
            }
            return k;
        }

        private void EnsureFonts()
        {
            // 用的是 TrafficMonitor 那一套：微软雅黑 9 号字 —— 9pt 在 96dpi 下就是 12px。
            // 两点跟"清晰"直接相关：
            //   1. 字号必须是整数。以前写 12.5px，GDI+ 按小数度量栅格化，竖笔落在半像素上，
            //      边缘被摊成两级灰，看着就是"糊一层"；
            //   2. 字体名用 "Microsoft YaHei"（TrafficMonitor 用的就是它），
            //      不是 "Microsoft YaHei UI" —— UI 变体是给系统界面收紧字距用的，小字号下更细。
            float px = (float)Math.Round(12.0 * _u);
            if (px < 9f) px = 9f;
            float tiny = (float)Math.Round(px * 0.62f);   // "TEM" 这类行首标签小一档半：
            if (tiny < 6f) tiny = 6f;                     // 它是给眼睛定位的，不该跟数值抢

            if (_fNorm != null && Math.Abs(_fNorm.Size - px) < 0.05f
                && _fTinyBold != null && Math.Abs(_fTinyBold.Size - tiny) < 0.05f) return;

            if (_fBold != null) _fBold.Dispose();
            if (_fNorm != null) _fNorm.Dispose();
            if (_fTiny != null) _fTiny.Dispose();
            if (_fTinyBold != null) _fTinyBold.Dispose();
            _fNorm = new Font("Microsoft YaHei", px, FontStyle.Regular, GraphicsUnit.Pixel);
            _fBold = new Font("Microsoft YaHei", px, FontStyle.Bold, GraphicsUnit.Pixel);
            // TEM 用小号粗体：7px 的常规字笔画只有一像素粗细，抗锯齿一摊就灰了；
            // 粗体把竖笔撑到两像素，小字号才立得住（这是唯一该加粗的地方 —— 数值 12px
            // 用粗体反而糊，文字标签反而需要）
            _fTiny = new Font("Microsoft YaHei", tiny, FontStyle.Regular, GraphicsUnit.Pixel);
            _fTinyBold = new Font("Microsoft YaHei", tiny, FontStyle.Bold, GraphicsUnit.Pixel);

            // 字号一变，墨迹中心跟着变，重新量一次（只在字号变化时做，不是每帧）
            _inkNorm = InkOffset(_fNorm);
            _inkBold = InkOffset(_fBold);
            _inkTiny = InkOffset(_fTiny);
            _inkTinyBold = InkOffset(_fTinyBold);
        }

        // 数字的墨迹中心离 DrawString 矩形上边有多远。
        //
        // 为什么不能直接用"行盒居中"：GDI+ 的 StringAlignment.Center 居的是行盒，
        // 行盒下方永远给 p / g 这些下伸字母留着空，而电量数字一个下伸部都没有，
        // 于是按行盒居中时数字看上去偏高，电池胶囊按几何中心居中就跟着错位。
        //
        // 这里用和真正绘制完全相同的字体 / StringFormat / 抗锯齿，把 "88" 画到临时位图上
        // 数一遍像素，量出墨迹的上下边界。既然两边参数一致，GDI+ 内部再怎么偏移都会自己抵消，
        // 剩下的就只有"墨迹中心"，正是眼睛看到的那个中心。
        private static float InkOffset(Font f)
        {
            const int BOX = 72;
            try
            {
                float top = BOX / 3f;
                int min = int.MaxValue, max = -1;

                using (Bitmap bmp = new Bitmap(BOX, BOX, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.None;   // 同 PaintContent：关掉半像素重采样
                    g.TextContrast = 5;                         // 同 PaintContent：默认 4，+1 更实

                    StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
                    sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                    sf.LineAlignment = StringAlignment.Near;
                    sf.Alignment = StringAlignment.Near;

                    using (SolidBrush b = new SolidBrush(Color.Black))
                        g.DrawString("88", f, b,
                            new RectangleF(2f, top, BOX - 4f, BOX - top - 2f), sf);

                    for (int y = 0; y < BOX; y++)
                    {
                        for (int x = 0; x < BOX; x++)
                        {
                            if (bmp.GetPixel(x, y).A < 16) continue;
                            if (y < min) min = y;
                            if (y > max) max = y;
                        }
                    }
                }

                if (max < 0) return f.Size * 0.62f;      // 一个像素都没画出来：退回经验值
                return (min + max + 1) / 2f - top;
            }
            catch { return f.Size * 0.62f; }
        }

        private float GlyphW { get { return 15f * _u; } }

        // 段首小标签（TEM）与数值之间的缝
        private float PreGap { get { return 2.4f * _u; } }

        // 蓝牙设备的行首图标（耳机 / 键盘 / 鼠标 / 手柄）的方形边长：比电池胶囊矮一点，
        // 跟 12.5px 的数字在一个视觉重量上，太大会把电量数字挤没
        private float IconW { get { return 12.6f * _u; } }

        // 图标和电量数字之间的缝（三处共用：量宽、画蓝牙列、画行首图标）
        private float IconGap { get { return 1.6f * _u; } }

        // 主体（本机信息）与右侧蓝牙列之间那道竖线：线宽 + 两侧的缝
        private float DivW { get { return Math.Max(1f, 1.1f * _u); } }
        private float DivGap { get { return 5f * _u; } }

        // 蓝牙列与列之间的缝：比组内行距宽一点，一眼看得出"这是另一列"
        private float BtColGap { get { return 5f * _u; } }

        // 一块内容在窗口里的落点：主体两行 + 右侧蓝牙列，位置全部在这里算一次。
        // Measure 和 PaintContent 共用它 —— 两边各算一遍的话，改一处忘另一处就会错位
        private struct StripLayout
        {
            public int Lines;      // 主体行数：1 或 2
            public float Pad;
            public float MainW;    // 主体区宽度（从 pad 起算，不含右侧的 pad）
            public float LowX;     // 下行的起点：TEM 右对齐到胶囊那一列后剩下的位置
            public float ValRight; // 符号列（% / ℃）的右缘
            public float NumX;     // 数字列的左缘
            public float NumW;     // 数字列宽度 = "100" 的宽度（三位上限）
            public float UnitW;    // 符号列宽度（% 和 ℃ 里较宽的那个）
            public int BtN;        // 蓝牙设备台数
            public int BtCols;     // 排成几列（每列最多 BtRowsPerCol 台）
            public float[] BtColW; // 每列宽度
            public float DivX;     // 竖线的左缘
            public float BtX;      // 蓝牙区左缘（第一列的左缘）
            public float BtW;      // 蓝牙区总宽（各列宽 + 列间距）
        }

        // 紧凑排布：段与段之间只留基础缝，多出来的空地不摊、窗口也不拉宽。
        private StripLayout BuildLayout(Graphics g, List<Seg> segs)
        {
            StripLayout L = new StripLayout();
            L.Pad = 3.5f * _u;
            L.Lines = MainLines(segs);
            float pad = L.Pad, gap = 3f * _u;

            // 上行的电量数字从 pad + 胶囊 + 缝 起（anchor）
            float anchor = pad + GlyphW + gap;

            // 下行从 pad 起：TEM 顶到最左边，跟上面的电池图标左边缘对齐。
            // 以前是把 TEM 往右挤、让温度数字压在电量数字那一列，结果 TEM 悬在半空，
            // 下行的左边缘跟电池图标对不上，一眼看着就是"两行各排各的"。
            // 温度数字不再左对齐电量数字，改由 ValRight 把 % 和 ℃ 右端对齐（数值仍是一列）
            L.LowX = pad;

            float[] lineW = new float[2];
            bool[] started = new bool[2];
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                if (s.Kind == K_BT)
                {
                    // 第 k 台落在第 k / BtRowsPerCol 列，列宽取该列里最宽的那台
                    int col = L.BtN / BtRowsPerCol;
                    if (L.BtColW == null) L.BtColW = new float[MaxBtCols];
                    // 蓝牙列按"100%"这个最长值定宽，而不是按本次实际值。
                    // 否则这拍只有 90%、下拍来了 100%，列宽跟着变，% 就左右跳
                    float bw = BtSlotWidth(g);
                    if (bw > L.BtColW[col]) L.BtColW[col] = bw;
                    L.BtN++;
                    continue;
                }
                int ln = s.Line == 1 ? 1 : 0;
                if (started[ln]) lineW[ln] += gap;        // 同一行里段与段之间才留缝
                lineW[ln] += SegWidth(g, s);
                started[ln] = true;
            }

            // 本机两行排成三列：图标（TEM）/ 数字 / 符号。
            // 数字列按 "100" 三位预留：电量和温度都可能到 100，位数变化时数字仍从同一处起，
            // 符号也落在同一列。整串右对齐做不到这点（% 和 ℃ 字宽不同，39℃ 的 3 会挤到十位）
            float numW = g.MeasureString("100", _fNorm, PointF.Empty,
                StringFormat.GenericTypographic).Width;
            L.UnitW = Math.Max(
                g.MeasureString("%", _fNorm, PointF.Empty, StringFormat.GenericTypographic).Width,
                g.MeasureString("℃", _fNorm, PointF.Empty, StringFormat.GenericTypographic).Width);
            float lowStart = L.LowX;
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                if (s.Kind == K_TEMP) { lowStart = L.LowX + PreWidth(g, s.Pre) + PreGap; break; }
            }
            L.NumX = Math.Max(anchor, lowStart);
            L.NumW = numW;
            L.ValRight = L.NumX + numW + L.UnitW;
            lineW[0] = (anchor - pad) + numW + L.UnitW;
            if (started[1]) lineW[1] = (lowStart - pad) + numW + L.UnitW;
            // 功率跟在符号列后面，别把它的宽度吞掉
            for (int i = 0; i < segs.Count; i++)
                if (segs[i].Kind == K_WATT) lineW[1] += gap + SegWidth(g, segs[i]);
            for (int i = 0; i < 2; i++)
            {
                if (!started[i]) continue;
                float start = i == 0 ? pad : L.LowX;
                float total = (start - pad) + lineW[i];
                if (total > L.MainW) L.MainW = total;
            }

            if (L.BtN > 0)
            {
                // 蓝牙区：每列装 BtRowsPerCol 台，列与列之间留一档缝。
                // 先知道总宽，右侧空档才够长（不然第 3 台的列会被窗口边缘切掉）
                L.BtCols = (L.BtN + BtRowsPerCol - 1) / BtRowsPerCol;
                L.BtW = 0f;
                for (int c = 0; c < L.BtCols; c++)
                {
                    if (c > 0) L.BtW += BtColGap;
                    L.BtW += L.BtColW[c];
                }
            }

            if (L.BtN > 0)
            {
                L.DivX = pad + L.MainW + DivGap;
                L.BtX = L.DivX + DivW + DivGap;
            }
            return L;
        }

        private void Measure(List<Seg> segs, int tbH, out int w, out int h)
        {
            EnsureFonts();

            // 高度：主体 1~2 行有自己的下限；右侧蓝牙设备一台一行，台数一多窗口就跟着长高，
            // 但不超过任务栏本身（h 是子窗口高度，超了就压到任务栏外面去了）
            float need = MainLines(segs) > 1 ? 34f * _u : 26f * _u;
            // 蓝牙一列最多上下两台，第三台起另起一列 —— 所以高度只按"最多两行"算，
            // 设备再多也不会把窗口顶出任务栏
            int btRows = Math.Min(CountKind(segs, K_BT), BtRowsPerCol);
            float btNeed = btRows * 15f * _u;
            if (btNeed > need) need = btNeed;

            h = (int)Math.Round(need);
            if (h > tbH - 4) h = tbH - 4;
            if (h < 16) h = 16;

            float total = 0f;
            using (Bitmap tmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(tmp))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.PixelOffsetMode = PixelOffsetMode.None;
                g.TextContrast = 5;
                StripLayout L = BuildLayout(g, segs);
                total = L.Pad * 2f + L.MainW;
                if (L.BtN > 0) total += DivGap + DivW + DivGap + L.BtW;
            }
            w = (int)Math.Ceiling(total);
            if (w < 40) w = 40;
        }

        private float SegWidth(Graphics g, Seg s)
        {
            if (s.Glyph) return GlyphW;
            float iw = s.Icon != 0 ? IconW + IconGap : 0f;
            if (!string.IsNullOrEmpty(s.Pre)) iw += PreWidth(g, s.Pre) + PreGap;
            return iw + g.MeasureString(s.Text, s.Bold ? _fBold : _fNorm, PointF.Empty,
                StringFormat.GenericTypographic).Width;
        }

        // 蓝牙一格固定按"100%"的宽度算：电量最多三位，给足这个宽度，
        // 再把数字右对齐，% 就落在同一条竖线上（90% 和 100% 的 % 不差那几像素）。
        // 不按本次实际文本定宽是为了避免设备电量变化时列宽跟着抖
        private float BtSlotWidth(Graphics g)
        {
            string max = _cfg.ShowPercentSign ? "100%" : "100";
            return IconW + IconGap
                + g.MeasureString(max, _fNorm, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        // 值段（电量 / 温度）的起点：电量接在电池胶囊后面，温度接在 TEM 后面
        private float ValueStartX(Graphics g, Seg s, float anchor, float lowX)
        {
            if (s.Kind == K_PCT) return anchor;
            return lowX + (string.IsNullOrEmpty(s.Pre) ? 0f : PreWidth(g, s.Pre) + PreGap);
        }

        private float ValueTextWidth(Graphics g, Seg s)
        {
            return g.MeasureString(s.Text, s.Bold ? _fBold : _fNorm, PointF.Empty,
                StringFormat.GenericTypographic).Width;
        }

        // 行首小标签（TEM）按小一号的粗体量：用同一个 _fTinyBold 画和量，宽度才对得上
        private float PreWidth(Graphics g, string pre)
        {
            return g.MeasureString(pre, _fTinyBold, PointF.Empty,
                StringFormat.GenericTypographic).Width;
        }

        // ── 绘制 ────────────────────────────────────────────────────────────
        private Color InkColor()
        {
            return _light ? Color.FromArgb(255, 26, 28, 33) : Color.FromArgb(255, 246, 248, 253);
        }

        private Color SubColor()
        {
            return _light ? Color.FromArgb(170, 44, 47, 54) : Color.FromArgb(175, 232, 236, 245);
        }

        private Color AccentOf(Sample s, bool noBat)
        {
            if (noBat) return _light ? Color.FromArgb(150, 155, 163) : Color.FromArgb(175, 178, 185);
            if (ChargeVisual.Powered(s)) return _light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135);
            if (s.Percent <= 10) return _light ? Color.FromArgb(212, 50, 50) : Color.FromArgb(242, 92, 92);
            if (s.Percent <= 20) return _light ? Color.FromArgb(198, 116, 14) : Color.FromArgb(246, 150, 62);
            return _light ? Color.FromArgb(6, 156, 90) : Color.FromArgb(70, 214, 135);
        }

        // 蓝牙设备的字色：平时跟本机电量一个色（上行是"一排电量"，不是"主 + 次"），
        // 只有掉到 20% 以下才换警告色 —— 耳机没电是突然断连，这一眼必须能看见
        private Color BtColor(int pct, Color baseColor)
        {
            if (pct < 0) return baseColor;
            if (pct <= 10) return _light ? Color.FromArgb(212, 50, 50) : Color.FromArgb(242, 92, 92);
            if (pct <= 20) return _light ? Color.FromArgb(198, 116, 14) : Color.FromArgb(246, 150, 62);
            return baseColor;
        }

        private void Render(List<Seg> segs)
        {
            if (_w <= 0 || _h <= 0) return;

            using (Bitmap bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // 背景不能清成全透明。Win8 起分层窗口是按 alpha 逐像素做命中测试的：
                    // alpha=0 的像素鼠标直接穿过去，于是只有电池图形和数字笔画能点中，
                    // 窄条就成了"必须精准点在字上才出菜单"的东西——手感极差。
                    // 填 alpha=1（几乎不可见，肉眼与全透明无异）之后整块矩形都可命中。
                    g.Clear(Color.FromArgb(HIT_ALPHA, 0, 0, 0));
                    PaintContent(g, segs, _w, _h);
                }

                if (_alpha && !_cfg.StripSolid && PushLayered(bmp)) return;

                // ULW 不认子窗口：退回实底 + 普通重绘
                _alpha = false;
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                BackColor = _light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
                Invalidate();
            }
        }

        // 把整张 ARGB 位图扔给系统合成：位置不动（pptDst = NULL），只换内容
        private bool PushLayered(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = SelectObject(memDc, hBitmap);

                SIZE size = new SIZE();
                size.cx = bmp.Width;
                size.cy = bmp.Height;
                POINT src = new POINT();

                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;

                return UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc,
                    ref src, 0, ref blend, ULW_ALPHA);
            }
            catch { return false; }
            finally
            {
                if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // ULW 失败时走普通 WM_PAINT（这时没有逐像素透明，背景是实色）
        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                PaintContent(e.Graphics, BuildSegs(), ClientSize.Width, ClientSize.Height);
            }
            catch (Exception ex) { LogOnce("OnPaint", ex); }
        }

        private void PaintContent(Graphics g, List<Seg> segs, int w, int h)
        {
            Sample s = _card == null ? null : _card.Current;
            if (s == null || segs.Count == 0) { _hits.Clear(); return; }
            if (_fNorm == null || _fBold == null || _fTinyBold == null) EnsureFonts();

            bool noBat = !s.HasBattery || s.Percent < 0;
            Color ink = InkColor();
            Color sub = SubColor();
            Color accent = AccentOf(s, noBat);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 文本要"锐"，图形要"滑"，这两件事在 GDI+ 里是分开的两个开关：
            //   TextRenderingHint.AntiAliasGridFit —— 字形轮廓对齐到像素网格（必需）
            //   PixelOffsetMode.HighQuality —— 原本以为是"高质量"，实际是给整幅图做半像素
            //     重采样，笔画边缘被插值摊成两级灰，12px 的小字立刻就糊了，必须关掉
            //   TextContrast —— 默认 4，抬到 5 让笔画更实，小数字不吃"灰掉一半"
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.TextContrast = 5;

            StringFormat sf = new StringFormat(StringFormat.GenericTypographic);
            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
            sf.LineAlignment = StringAlignment.Near;      // 上边的位置由墨迹反推，不交给行盒
            sf.Alignment = StringAlignment.Near;

            StripLayout L = BuildLayout(g, segs);
            float gap = 3f * _u;
            int lines = L.Lines < 1 ? 1 : L.Lines;

            // 主体那两行按自己的"紧凑高度"整体居中，不跟着蓝牙列一起被拉开 ——
            // 右边竖排三台设备时窗口会长到 44，本机的两行要是也摊到 22 一行，看着就散了
            float mainBlock = lines > 1 ? 34f * _u : 26f * _u;
            if (mainBlock > h) mainBlock = h;
            float mainTop = (h - mainBlock) / 2f;
            float lineH = mainBlock / (float)lines;

            // 两行各排各的：上行从 pad 起，下行从 TEM 之后起（数字正好压在上行电量下面那一列）
            float[] lineX = new float[lines];
            lineX[0] = L.Pad;
            if (lines > 1) lineX[1] = L.LowX;

            // 值列的右缘（BuildLayout 里算好的）：% 和 ℃ 都右对齐到这条线
            float valRight = L.ValRight;

            // 分组竖线：左边是本机（电量 / 温度 / 功率），右边一列是蓝牙外设。
            // 两组含义完全不同，只靠间距区分不够，一条细线一眼就分开了。
            // 高度只占 42%：立在两行文字的行距之间那一档就够了，拉到 60% 比字还高，
            // 反而像表格边框、把人往两边推
            if (L.BtN > 0)
            {
                float divH = h * 0.42f;
                using (SolidBrush db = new SolidBrush(Color.FromArgb(90, sub.R, sub.G, sub.B)))
                    g.FillRectangle(db, L.DivX, (h - divH) / 2f, DivW, divH);
            }

            // 蓝牙区：一列最多上下两台，第三台另起一列。
            // 一列里只有一台时，它自己在整高里垂直居中；两台就各占一半，上下对齐铺开
            float[] colX = new float[L.BtCols > 0 ? L.BtCols : 1];
            for (int c = 0; c < L.BtCols; c++)
            {
                colX[c] = L.BtX;
                for (int k = 0; k < c; k++) colX[c] += L.BtColW[k] + BtColGap;
            }
            // 悬停提示的命中区，跟这一帧画出来的格子一一对应（布局一变就会重算）
            _hits.Clear();
            float mainRight = L.BtN > 0 ? L.DivX : w;
            _hits.Add(Hit(new RectangleF(0f, 0f, Math.Max(10f, mainRight), h),
                "本机", LocalTipLines(s)));

            int btSeen = 0;

            for (int i = 0; i < segs.Count; i++)
            {
                Seg seg = segs[i];
                bool isBt = seg.Kind == K_BT;
                int ln = seg.Line == 1 ? 1 : 0;
                if (ln >= lines) ln = lines - 1;

                float cy, x;
                float btColW = 0f;
                if (isBt)
                {
                    int col = btSeen / BtRowsPerCol;
                    int row = btSeen % BtRowsPerCol;
                    int inCol = Math.Min(BtRowsPerCol, L.BtN - col * BtRowsPerCol);
                    float band = h / (float)inCol;
                    cy = band * row + band / 2f;
                    x = colX[col];
                    btColW = L.BtColW[col];
                    btSeen++;

                    // 这一格就是这台设备的悬停区：鼠标落在格子里（图标或数字上都算）就报型号
                    BtDevice dv = BtOf(s, seg.DevIdx);
                    HitBox hb2 = Hit(new RectangleF(colX[col], band * row, btColW + BtColGap, band),
                        BtTipTitle(dv), BtTipLines(dv));
                    hb2.Dev = dv;      // 悬停时再拼一次，蓝牙版本号查到了才写得出来
                    _hits.Add(hb2);
                }
                else
                {
                    cy = mainTop + lineH * ln + lineH / 2f;
                    x = lineX[ln];
                }

                if (seg.Glyph)
                {
                    DrawBattery(g, x, cy, noBat ? -1 : s.Percent, accent, ink, ChargeVisual.Bolt(s));
                    lineX[ln] = x + GlyphW + gap;
                    continue;
                }

                Font f = seg.Bold ? _fBold : _fNorm;
                // 整条文字同一种颜色：电量、温度、功率、蓝牙都用主墨色。
                // 以前按"主读数 / 次要信息"分两档浓淡，缩到 12px 之后淡的那档直接发灰、
                // 像没渲染干净 —— 任务栏这一行靠字号和图标区分层次就够了，不靠灰度
                Color c = ink;

                // 蓝牙设备：数字跟本机电量同样粗细、同样颜色，一列读下来就是一串电量
                if (seg.Bt) c = BtColor(seg.Pct, ink);

                // 行首图标：蓝牙设备画设备图标，温度画温度计。图标画法和卡片共用一份
                // （FloatForm 的 partial 部分），两处图标永远长得一样，不维护两套矢量代码
                if (seg.Icon != 0)
                {
                    float b = IconW;
                    FloatForm.DrawRowIcon(g, (RowIcon)seg.Icon, x, cy - b / 2f, b, c, -1);
                    x += b + IconGap;
                }

                // 把数字的墨迹中心放到行带中心 cy 上：胶囊也是以 cy 为中线画的，
                // 两边同一个基准，图标和数字才是真对齐（不是"行盒对齐"看着错半格）
                float inkOff = seg.Bold ? _inkBold : _inkNorm;
                if (inkOff < 0f || inkOff > lineH) inkOff = lineH / 2f;

                // 段首小标签（TEM）：小一档、淡一档，墨迹中心也落在 cy 上（跟数值同一个视觉中线）。
                // 它是这段的一部分 —— 位置不够时跟着数值一起被裁掉，不会剩个孤零零的 TEM
                if (!string.IsNullOrEmpty(seg.Pre))
                {
                    float pw = PreWidth(g, seg.Pre);
                    float po = _inkTinyBold;
                    if (po < 0f || po > lineH) po = lineH / 2f;
                    // 跟数值同字体族（微软雅黑）、同常规字重、同主墨色，只是小一档。
                    // 不再压成淡灰：小字一淡就飘成灰雾，看着既不"黑"也不像雅黑
                    using (SolidBrush pb = new SolidBrush(ink))
                        g.DrawString(seg.Pre, _fTinyBold, pb,
                            new RectangleF((float)Math.Round(x), (float)Math.Round(cy - po),
                                pw + 2f, lineH), sf);
                    x += pw + PreGap;
                }

                // 本机两行：图标列（电池胶囊 / TEM）已画，这里数字和符号分两列画。
                // 数字在三位字段里右对齐、符号紧跟字段末尾，所以 93% 和 39℃ 的个位对个位、
                // % 对 ℃；哪怕一边跳到 100 也不会把另一行顶偏
                if (seg.Kind == K_PCT || seg.Kind == K_TEMP)
                {
                    string num = string.IsNullOrEmpty(seg.Num) ? seg.Text : seg.Num;
                    float nw = g.MeasureString(num, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                    float yy = (float)Math.Round(cy - inkOff);
                    using (SolidBrush b = new SolidBrush(c))
                    {
                        // 数字在三位字段里右对齐：93 的个位落在 100 的个位下面，
                        // 不会出现"93   %"那种留缝；符号紧跟着字段末尾，两行自然对齐
                        float nx = L.NumX + Math.Max(0f, L.NumW - nw);
                        g.DrawString(num, f, b,
                            new RectangleF((float)Math.Round(nx), yy, nw + 2f, lineH), sf);
                        if (!string.IsNullOrEmpty(seg.Unit))
                        {
                            // % 和 ℃ 字宽不同，都从列左缘起画会看着错半格；
                            // 各自在符号列里居中，视觉上才是上下对齐的
                            float uw = g.MeasureString(seg.Unit, f, PointF.Empty,
                                StringFormat.GenericTypographic).Width;
                            float ux = valRight - L.UnitW + Math.Max(0f, (L.UnitW - uw) / 2f);
                            g.DrawString(seg.Unit, f, b,
                                new RectangleF((float)Math.Round(ux), yy, uw + 2f, lineH), sf);
                        }
                    }
                    lineX[ln] = valRight + gap;
                    continue;
                }

                float tw = g.MeasureString(seg.Text, f, PointF.Empty, StringFormat.GenericTypographic).Width;
                float vx = x;

                // 有读数的右对齐到格子右缘：列宽固定按 "100%" 留，90% 和 100% 的 %
                // 就落在同一条竖线上。
                // "--" 没有 %，跟着右缘太远、贴读数左缘又太近，
                // 所以在读数占的那段宽度里取中间，离图标的距离落在两者之间
                if (isBt && btColW > 0f)
                {
                    float avail = btColW - (IconW + IconGap);
                    if (seg.Pct >= 0) vx = x + Math.Max(0f, avail - tw);
                    else
                    {
                        string maxText = _cfg.ShowPercentSign ? "100%" : "100";
                        float maxW = g.MeasureString(maxText, f, PointF.Empty,
                            StringFormat.GenericTypographic).Width;
                        // 在读数占的那段宽度里居中：贴左缘太近、贴右缘又太远
                        vx = x + Math.Max(0f, avail - maxW) + Math.Max(0f, (maxW - tw) / 2f);
                    }
                }

                // 原点取整再画：grid fit 只有在整数原点上才把笔画真正压到像素格里，
                // 差半个像素就是一级灰边 —— 12px 的字全靠这一点看着"利"
                using (SolidBrush b = new SolidBrush(c))
                    g.DrawString(seg.Text, f, b,
                        new RectangleF((float)Math.Round(vx), (float)Math.Round(cy - inkOff),
                            tw + 2f, lineH), sf);
                lineX[ln] = vx + tw + gap;
            }
        }

        // 电池胶囊：跟卡片上那个同款，小一号；充电时里面填绿并画个小闪电（bolt = 真在进电）
        private void DrawBattery(Graphics g, float x, float cy, int pct, Color fill, Color ink, bool bolt)
        {
            float u = _u;
            float w = GlyphW - 2.6f * u;
            float h = 9.6f * u;
            float lw = 1.15f * u;
            RectangleF body = new RectangleF(x, cy - h / 2f, w, h);
            RectangleF tip = new RectangleF(body.Right + 0.6f * u, cy - h * 0.22f, 2f * u, h * 0.44f);

            using (GraphicsPath p = Ui.Round(body, 2.6f * u))
            using (Pen pen = new Pen(ink, lw))
                g.DrawPath(pen, p);
            using (GraphicsPath p = Ui.Round(tip, 1f * u))
            using (SolidBrush b = new SolidBrush(ink))
                g.FillPath(b, p);

            if (pct >= 0)
            {
                float inset = lw + 0.6f * u;
                float inner = Math.Max(0f, body.Width - inset * 2f) * Math.Min(100, pct) / 100f;
                if (inner > 0.5f)
                {
                    RectangleF r = new RectangleF(body.X + inset, body.Y + inset, inner, body.Height - inset * 2f);
                    using (GraphicsPath p = Ui.Round(r, 1.5f * u))
                    using (SolidBrush b = new SolidBrush(fill))
                        g.FillPath(b, p);
                }
            }

            if (bolt)
            {
                // 小闪电，压在电池中间
                float bx = body.X + body.Width / 2f;
                float by = cy;
                PointF[] pts = new PointF[]
                {
                    new PointF(bx + 1.2f * u, by - 3.2f * u),
                    new PointF(bx - 1.4f * u, by + 0.2f * u),
                    new PointF(bx - 0.1f * u, by + 0.2f * u),
                    new PointF(bx - 1.0f * u, by + 3.2f * u),
                    new PointF(bx + 1.6f * u, by - 0.4f * u),
                    new PointF(bx + 0.3f * u, by - 0.4f * u)
                };
                using (SolidBrush b = new SolidBrush(_light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(20, 40, 30)))
                    g.FillPolygon(b, pts);
            }
        }

        // ── 交互 ────────────────────────────────────────────────────────────

        // ── 悬停提示 ────────────────────────────────────────────────────────

        private static HitBox Hit(RectangleF r, string title, string[] lines)
        {
            HitBox hb = new HitBox();
            hb.Rect = r;
            hb.Title = title ?? "";
            hb.Lines = lines == null ? new string[0] : lines;
            return hb;
        }

        // 取一台设备（下标由 BuildSegs 记在 Seg.DevIdx 上，轮播也不会错）。
        // 注意下标是**过滤后**列表里的位置（只算报了电量的），跟画出来的那一列一一对应，
        // 所以这里也得用同一份列表查，否则悬停会指到别的设备上
        private static BtDevice BtOf(Sample s, int idx)
        {
            List<BtDevice> bts = BtWithPower(s == null ? null : s.Bt);
            if (idx < 0 || idx >= bts.Count) return null;
            return bts[idx];
        }

        private static string BtTipTitle(BtDevice d) { return BtTip.Title(d); }

        private static string[] BtTipLines(BtDevice d) { return BtTip.Lines(d); }

        private static string[] LocalTipLines(Sample s)
        {
            List<string> L = new List<string>();
            if (s == null) return L.ToArray();

            L.Add(s.HasBattery && s.Percent >= 0
                ? "电池：" + s.Percent.ToString(CultureInfo.InvariantCulture) + "%"
                : "电池：--");

            string st;
            if (!s.HasBattery) st = "无电池";
            else if (s.Full) st = "已充满";
            else if (ChargeVisual.Powered(s)) st = s.Charging ? "充电中" : "已接电源";
            else st = "使用电池";
            if (!double.IsNaN(s.Watts))
                st += " · " + s.Watts.ToString("0.0", CultureInfo.InvariantCulture) + "W";
            L.Add("状态：" + st);

            if (!double.IsNaN(s.TempC))
                L.Add("温度：" + s.TempC.ToString("0", CultureInfo.InvariantCulture) + "℃");

            return L.ToArray();
        }

        // ── 层级看门狗（跟 1 秒的 Sync 分开跑）─────────────────────────────
        // 窄条是 SetParent 失败后浮在任务栏上方的顶层窗口（explorer 跟本进程的 DPI 感知不一致，
        // 跨进程 SetParent 直接被拒），所以它跟 Shell_TrayWnd 是在**同一层**抢 z 序的。
        // 点托盘那个"向上箭头"时，explorer 会把 Shell_TrayWnd 重新抬回最上层（抽屉要挂在它上面），
        // 窄条就被任务栏连同 ReBar 一起盖住 —— 而抬回来要等下一拍 Sync，最长 1 秒，
        // 用户看到的就是"闪一下"。实测：盖住期间 WindowFromPoint(条中心) 落在 ReBarWindow32。
        //
        // 这里每 50ms 探一次：只有"压住我们的确实是任务栏自己"才立刻抬。
        // 抬的时候插到 Shell_TrayWnd 的**正上方**，而不是 HWND_TOPMOST 顶到最顶 ——
        // 这样仍然待在托盘抽屉、菜单下面，不会把自己画到抽屉身上去。
        private int _zFix, _zFixLogged;

        // 抬 z 序的插入点：任务栏自己就是 topmost 时插到它正上方（既压住任务栏，又留在抽屉下面）；
        // 万一任务栏不是 topmost（某些主题/被别的程序改过），退回 HWND_TOPMOST，别让自己掉出最上层。
        private IntPtr ZAnchor()
        {
            if (_taskbar == IntPtr.Zero) return HWND_TOPMOST;
            int ex = GetWindowLong(_taskbar, GWL_EXSTYLE);
            return (ex & WS_EX_TOPMOST) != 0 ? _taskbar : HWND_TOPMOST;
        }

        private void KeepAbove()
        {
            if (_embedded || _taskbar == IntPtr.Zero) return;
            if (_w <= 0 || _h <= 0 || !Visible) return;

            RECT r;
            if (!GetWindowRect(Handle, out r)) return;
            POINT p = new POINT();
            p.X = (r.Left + r.Right) / 2;
            p.Y = (r.Top + r.Bottom) / 2;

            IntPtr above = WindowFromPoint(p);
            if (above == IntPtr.Zero || above == Handle) return;

            // 往上找根：根是任务栏 → 是任务栏把我们压住了；是别的窗口（抽屉/菜单/提示卡）→ 不管
            bool byTaskbar = false;
            IntPtr h = above;
            for (int i = 0; i < 8 && h != IntPtr.Zero; i++)
            {
                if (h == _taskbar) { byTaskbar = true; break; }
                h = GetParent(h);
            }
            if (!byTaskbar) return;

            SetWindowPos(Handle, ZAnchor(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            _zFix++;
            // 每次都记一条（封顶 40 条）：闪烁这种一次性的现象，日志里必须有痕迹才能对账
            if (_zFix - _zFixLogged >= 1 && _zFix <= 40) { _zFixLogged = _zFix; Diag("zfix #" + _zFix); }
        }

        // 每 120ms 问一次光标：在条里就按命中区弹提示，出界就收。
        // 提示框的位置跟着条走（右对齐、贴上方），任务栏在顶部时自动翻到下方
        private void OnHoverTick()
        {
            try
            {
                if (IsDisposed || !IsWindow(Handle) || !Visible) { HideTip(); return; }

                POINT p;
                if (!GetCursorPos(out p)) { HideTip(); return; }

                RECT r;
                if (!GetWindowRect(Handle, out r)) { HideTip(); return; }
                if (p.X < r.Left || p.X >= r.Right || p.Y < r.Top || p.Y >= r.Bottom)
                {
                    HideTip();
                    return;
                }

                float cx = p.X - r.Left, cy = p.Y - r.Top;
                HitBox hit = null;
                for (int i = 0; i < _hits.Count; i++)
                {
                    if (_hits[i].Rect.Contains(cx, cy)) { hit = _hits[i]; break; }
                }
                if (hit == null) { HideTip(); return; }

                if (_tip == null) _tip = new StripTip();
                string tipTitle = hit.Dev != null ? BtTipTitle(hit.Dev) : hit.Title;
                string[] tipLines = hit.Dev != null ? BtTipLines(hit.Dev) : hit.Lines;
                string sig = tipTitle + "\u0001" + string.Join("\u0002", tipLines);
                if (sig != _tipSig)
                {
                    _tipSig = sig;
                    _tip.SetContent(_light, _u, tipTitle, tipLines);
                }

                int tw = _tip.Width, th = _tip.Height;
                int gp = (int)Math.Round(5 * _u);
                Rectangle wa;
                try { wa = Screen.FromPoint(new Point(p.X, p.Y)).WorkingArea; }
                catch { wa = Screen.PrimaryScreen.WorkingArea; }

                int x = r.Right - tw;                  // 右边缘跟条对齐
                int y = r.Top - th - gp;               // 默认浮在条上方（任务栏在底部）
                if (y < wa.Top) y = r.Bottom + gp;     // 任务栏在顶部：改放下面
                if (x < wa.Left + gp) x = wa.Left + gp;
                if (x + tw > wa.Right - gp) x = wa.Right - gp - tw;
                if (y < wa.Top + gp) y = wa.Top + gp;
                if (y + th > wa.Bottom - gp) y = wa.Bottom - gp - th;

                _tip.PlaceAt(x, y);
                _tip.ShowNoActivate();
            }
            catch { }
        }

        private void HideTip()
        {
            _tipSig = "";
            try { if (_tip != null && _tip.Visible) _tip.Hide(); } catch { }
        }

        // 右键 = 弹卡片那套菜单（同一份配置、同样的勾选状态）；左键刻意什么也不做。
        // 早先左键是"打开面板"，但任务栏那块地方离任务按钮太近，误点就把一个窗口拽到前面来，
        // 很打断动作。要看面板走右键菜单里的"打开面板"。
        //
        // 为什么不能只写 OnMouseClick / OnMouseUp：
        // WinForms 的 Click 只在左键时触发，右键根本进不来；真正该接的是 WM_CONTEXTMENU
        // —— 右键抬起后由系统发给光标下的窗口。本窗口是 Shell_TrayWnd 的子窗口，收到
        // WM_CONTEXTMENU 若不声明"已处理"，DefWindowProc 会把它转交给父窗口，explorer
        // 于是弹出任务栏自己的菜单（工具栏 / 层叠窗口 / 任务管理器那一套），
        // 看上去就像"在窄条上右键没反应"。
        protected override void WndProc(ref Message m)
        {
            const int WM_SETTINGCHANGE = 0x001A;
            const int WM_THEMECHANGED = 0x031A;

            // 系统主题换了要重画一次（颜色反了会看不清）
            if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_THEMECHANGED)
            {
                bool light = IsLightTaskbar();
                if (light != _light) { _light = light; _key = ""; }
            }

            if (m.Msg == WM_RBUTTONUP)
            {
                PostMenu();
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WM_CONTEXTMENU)
            {
                // lParam = -1 是键盘的菜单键，那种情况不归这里管
                if (m.LParam.ToInt64() != -1)
                {
                    PostMenu();
                    m.Result = (IntPtr)1;   // 吃掉：不再往上转，任务栏菜单就不会跟着冒出来
                    return;
                }
            }

            base.WndProc(ref m);
        }

        private long _menuAt;

        // 投到卡片的消息队列里再弹：菜单里"显示任务栏窗口"一勾掉就会 Dispose 本窗口，
        // 在鼠标消息处理栈里自毁容易炸，换个栈就干净了。
        // WM_RBUTTONUP 和随后的 WM_CONTEXTMENU 会连着来两条，用时间戳去重，只弹一次
        private void PostMenu()
        {
            if (_card == null) return;
            long now = Environment.TickCount;
            if (_menuAt != 0 && now - _menuAt < 400) return;
            _menuAt = now;
            try
            {
                _card.BeginInvoke(new Action(delegate { _card.ShowCardMenu(); }));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { _timer.Stop(); } catch { }
            try { _hover.Stop(); } catch { }
            HideTip();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RestoreContainer();                      // 退出前把按钮区还原（TM 同款）
                try { _timer.Stop(); _timer.Dispose(); } catch { }
                try { if (_hover != null) { _hover.Stop(); _hover.Dispose(); } } catch { }
                try { if (_tip != null) { _tip.Close(); _tip.Dispose(); _tip = null; } } catch { }
                try { if (_fBold != null) _fBold.Dispose(); } catch { }
                try { if (_fNorm != null) _fNorm.Dispose(); } catch { }
                try { if (_fTiny != null) _fTiny.Dispose(); } catch { }
                try { if (_fTinyBold != null) _fTinyBold.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // 任务栏是浅色还是深色：自带的 UI 会跟着系统主题走，文字得反着来
        internal static bool IsLightTaskbar()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 1);
                if (v is int) return (int)v != 0;
            }
            catch { }
            return true;
        }
    }
}
