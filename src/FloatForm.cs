// 卡片窗口：无边框 + 分层，鼠标穿透；Ctrl 拖动、Ctrl 右键出菜单。
// 这里只管"窗口怎么活"：创建、尺寸、菜单、拖动、贴边、定时器、诊断日志。
// 画什么在 CardRender.cs（同一个 partial 类）。

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
    internal sealed partial class FloatForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int ULW_ALPHA = 0x00000002;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        private const int VK_LBUTTON = 0x01;
        private const int VK_CONTROL = 0x11;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        // 光标：SetCursor 是全局的，但只有窗口"可被点中"时才不会被下面的窗口改回去，
        // 所以配合动态开关 WS_EX_TRANSPARENT 使用
        [DllImport("user32.dll")]
        private static extern IntPtr SetCursor(IntPtr hCursor);

        private const int GWL_EXSTYLE = -20;

        // 只用到了 32 位的扩展样式位，用 int 版即可，避免 64 位下 SetWindowLongPtr 的导出差异
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // 原生弹出菜单：Ctrl+右键时窗口是可点中的，用 TrackPopupMenu 弹系统菜单，
        // 不依赖窗口被激活（卡片是 WS_EX_NOACTIVATE，WinForms 的 ContextMenuStrip 会闪一下就没）
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr hMenu, int uFlags, UIntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenu(IntPtr hMenu, int uFlags, int x, int y, int nReserved,
            IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private readonly Config _cfg;
        private int _w;                 // 随缩放设置变（ApplyConfig）
        private int _h;                 // 可随"显示电量"开关变化：两行 68，只剩温度时压成一行 40
        private readonly System.Windows.Forms.Timer _timer;
        private readonly System.Windows.Forms.Timer _warmTimer;   // 启动后补几拍，等首轮链路探测
        private readonly System.Windows.Forms.Timer _tipTimer;    // Ctrl+悬停：看某台蓝牙设备的详情
        private readonly System.Windows.Forms.Timer _animTimer;   // 充电动画：真在进电时重画，闪电呼吸
        private int _warmLeft;
        private bool _started;          // OnShown 只做一次初始化，隐藏后再显示不要重来
        private Sample _sample = new Sample();
        private bool _light;            // 背景够亮就走深墨字，否则走浅白字（换主题时重算）

        // 拖动：窗口本身是鼠标穿透的，收不到鼠标消息，所以用定时器轮询全局按键 + 光标位置
        private readonly System.Windows.Forms.Timer _dragTimer;
        private readonly string _posPath;
        private bool _dragging;
        private bool _wasDown;
        private bool _pressOutside;
        private int _grabDx;
        private int _grabDy;

        // 光标：默认全窗穿透（收不到鼠标消息），只有"按住 Ctrl 悬停在卡片上"时才临时
        // 去掉 WS_EX_TRANSPARENT，让窗口能收到 WM_SETCURSOR，从而稳定显示手型（不闪）
        private bool _hittable;
        private IntPtr _handCur = IntPtr.Zero;
        private IntPtr _arrowCur = IntPtr.Zero;

        // 右键菜单（Ctrl + 右键弹出）：此时窗口已经临时不穿透，右键能直接送到窗口
        private bool _menuOpen;

        // 统计数据：每 RecordMinutes 分钟往 battery-monitor-history.csv 追加一行
        // 设置面板（内含历史统计页）：卡片和面板互不依附，面板关掉不影响采样
        private SettingsForm _panel;
        private DateTime _lastRecAt = DateTime.MinValue;

        // 诊断日志：内容变化或每 60 秒写一行，用来证明"程序一直在跑"
        private readonly string _logPath;
        private readonly string _iniPath;   // 菜单里的显示开关要写回这里，重启后保持
        private string _lastLogText = "";
        private DateTime _lastLogAt = DateTime.MinValue;

        public FloatForm(Config cfg)
        {
            _cfg = cfg;
            float sc = cfg.Scale <= 0 ? 1f : cfg.Scale;
            // 134：蓝牙设备行是"图标 + 名称 + 电池胶囊 + 音量"四段。
            // 音量到 100 时占三位，再加小喇叭和缝；两台音频设备同时显示时，
            // 144 仍会把名字截成"耳…"，再加两个数字位（约 14px）名字才放得下
            if (cfg.Horizontal)
            {
                // 横向：宽高都由内容量出来，蓝牙设备一行一台往下叠
                Size hs = MeasureHorizontal();
                _w = Math.Max(60, hs.Width);
                _h = Math.Max(30, hs.Height);
            }
            else
            {
                _w = (int)Math.Round(134 * sc);
                _h = CalcHeight(cfg, sc, 0);   // 蓝牙行等第一次采样后由 ApplyHeight 补上
            }

            _light = IsLightTheme(cfg);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(_w, _h);

            _posPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "battery-monitor.pos");
            _logPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "battery-monitor.log");
            _iniPath = AppPaths.Ini;

            // 历史数据文件放在 exe 旁边，启动时顺便按保留天数裁一次。
            // 蓝牙那份单独一个文件：只有连上且上报了电量的设备才会往里写
            History.Init(AppPaths.History, AppPaths.HistoryBt, cfg.HistoryDays);

            try
            {
                _handCur = Cursors.Hand.Handle;    // 手型：暗示"这个可以拖"
                _arrowCur = Cursors.Default.Handle;
            }
            catch { }

            // 默认贴右下角：每次启动都按"当前工作区 + 当前卡片尺寸"重算一遍，
            // 不从旧坐标硬套 —— 换分辨率 / 远程桌面接回来 / 改过缩放之后，
            // 旧坐标早就不代表"右下角"了（出现半个卡片在屏幕外就是这么来的）。
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Point pos = new Point(wa.Right - _w - cfg.MarginRight, wa.Bottom - _h - cfg.MarginBottom);

            // 只有"上次保存时的工作区和现在一模一样"才沿用上次拖到的位置：
            // 屏幕没变 = 用户是故意挪的，该尊重；屏幕变了 = 坐标没有可比性，回到右下角。
            Point saved;
            if (TryLoadPosition(wa, out saved)) pos = saved;
            Location = ClampToWorkArea(pos.X, pos.Y);
            // 定位好了立刻按新格式写回：下次启动只认"距右下角的间距"，
            // 卡片尺寸再改也不会把某一侧的缝吃掉
            SavePosition();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = Math.Max(2, cfg.RefreshSeconds) * 1000;
            _timer.Tick += OnTick;

            _dragTimer = new System.Windows.Forms.Timer();
            _dragTimer.Interval = 25;
            _dragTimer.Tick += OnDragTick;
            _dragTimer.Start();

            // 链路探测在线程池里跑，启动那一拍必然是"还不知道谁连着"，
            // 所以每 0.7 秒补一拍，探完立刻停 —— 蓝牙行一秒多就位，不用等主刷新的 5 秒
            _warmTimer = new System.Windows.Forms.Timer();
            _warmTimer.Interval = 700;
            _warmTimer.Tick += OnWarmTick;

            // Ctrl + 悬停看设备详情。卡片本身是"鼠标穿透"的（WS_EX_TRANSPARENT，
            // 为的是不挡住底下的窗口），收不到 MouseMove，所以跟窄条一样改成轮询光标
            _tipTimer = new System.Windows.Forms.Timer();
            _tipTimer.Interval = 120;
            _tipTimer.Tick += delegate { OnTipTick(); };
            _tipTimer.Start();

            // 动画定时器常开，但 OnAnimTick 会在"卡片不可见 / 不在充电"时自己停掉，
            // OnTick 每拍又会按需拉起来 —— 平时（用电池）它是不跑的
            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 350;
            _animTimer.Tick += delegate { OnAnimTick(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        private const int WM_SETCURSOR = 0x0020;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_NULL = 0x0000;

        // explorer 重启后广播的消息：任务栏换了个新 HWND，挂在上面的窄条要重新挂
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);
        private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        // 这三项只在本文件内部用来切"卡片显示哪几行"，已经不在菜单里出现（菜单只切窗口显隐，
        // 细化项在设置页），所以留着做内部 id，不参与菜单构建
        private const int MENU_TEMP = 3;
        private const int MENU_STATE = 4;
        private const int MENU_BT = 6;

        // 菜单内容不在这里拼：统一由 AppMenu.Build 给出条目，这里只负责用 Win32 画出来
        private const int MF_STRING = 0x00000000;
        private const int MF_SEPARATOR = 0x00000800;
        private const int MF_CHECKED = 0x00000008;
        private const int MF_GRAYED = 0x00000001;
        private const int TPM_RIGHTBUTTON = 0x0002;
        private const int TPM_NONOTIFY = 0x0080;
        private const int TPM_RETURNCMD = 0x0100;

        // 只有"可点中"时才会走到这里，直接钉住手型并声明已处理，避免被 DefWndProc 改回箭头
        protected override void WndProc(ref Message m)
        {
            // 任务栏重建（explorer 崩溃/更新）后广播一次，通知宿主把窄条重新挂上去
            if (WM_TASKBARCREATED != 0 && m.Msg == WM_TASKBARCREATED)
            {
                if (TaskbarCreatedHook != null)
                {
                    try { TaskbarCreatedHook(); } catch { }
                }
            }

            if (m.Msg == WM_SETCURSOR && _hittable && _handCur != IntPtr.Zero)
            {
                SetCursor(_handCur);
                m.Result = (IntPtr)1;
                return;
            }

            // 能收到 WM_RBUTTONUP 就说明是 Ctrl+右键（否则窗口是穿透的）
            if (m.Msg == WM_RBUTTONUP)
            {
                ShowCardMenu();
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);
        }

        // Ctrl + 右键弹系统菜单。条目、顺序、勾选、可用性、动作全部来自 AppMenu.Build，
        // 托盘那份也读同一个列表 —— 三处入口不可能再出现"这项在那边有、这边没有"。
        // 任务栏窄条上的右键也走这一个入口（internal）。
        internal void ShowCardMenu()
        {
            if (_menuOpen) return;
            _menuOpen = true;
            IntPtr hMenu = IntPtr.Zero;
            IntPtr prev = IntPtr.Zero;
            int cmd = 0;

            List<AppMenu.Item> items = AppMenu.Build(this);
            AppMenu.Item picked = null;

            try
            {
                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return;

                // 菜单项 id 直接用下标 +1，选中后凭 id 回列表取动作，不再维护一套 id 常量
                for (int i = 0; i < items.Count; i++)
                {
                    AppMenu.Item it = items[i];
                    if (it.Text == null)
                    {
                        AppendMenu(hMenu, MF_SEPARATOR, UIntPtr.Zero, null);
                        continue;
                    }
                    int flags = MF_STRING;
                    if (it.Checked != null && it.Checked()) flags |= MF_CHECKED;
                    if (it.Enabled != null && !it.Enabled()) flags |= MF_GRAYED;
                    AppendMenu(hMenu, flags, new UIntPtr((uint)(i + 1)), it.Text);
                }

                prev = GetForegroundWindow();
                SetForegroundWindow(Handle);   // 让菜单"点别处就消失"，失败也不影响弹出

                Point c = System.Windows.Forms.Cursor.Position;
                cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
                    c.X, c.Y, 0, Handle, IntPtr.Zero);

                PostMessage(Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
            finally
            {
                try { if (hMenu != IntPtr.Zero) DestroyMenu(hMenu); } catch { }
                if (cmd > 0 && cmd <= items.Count) picked = items[cmd - 1];
                WriteDiag("menu cmd=" + cmd.ToString(CultureInfo.InvariantCulture)
                    + " item=" + (picked == null ? "-" : picked.Text));
                // 要开面板时不要抢着把前台还回去，否则新窗口起不来
                if (picked == null || !picked.OpensWindow)
                {
                    try { if (prev != IntPtr.Zero && prev != Handle) SetForegroundWindow(prev); } catch { }
                }
                _menuOpen = false;
            }

            if (picked != null && picked.Do != null)
            {
                if (picked.OpensWindow)
                {
                    // 菜单刚销毁、前台还没稳住时直接 Show 新窗口，可能起不来或被压在后面。
                    // 丢回消息循环下一拍再做，托盘入口尤其需要这样
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            try { picked.Do(); }
                            catch (Exception ex) { WriteDiag("menu action: " + ex.Message); }
                        }));
                        return;
                    }
                    catch { }
                }
                try { picked.Do(); }
                catch (Exception ex) { WriteDiag("menu action: " + ex.Message); }
            }
        }

        // 面板打不开这类问题只靠猜不行，留一行诊断在日志里
        internal void WriteDiag(string text)
        {
            try
            {
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    + " " + text + Environment.NewLine);
            }
            catch { }
        }

        // 菜单里的"显示悬浮窗口"。托盘关着时不给藏 —— 藏了就再也没入口叫回来（菜单项本身也灰了）
        internal void ToggleCardVisible()
        {
            if (Visible) { if (_cfg.Tray) Hide(); }
            else Show();
        }

        // 供 AppMenu 调用：窄条的显隐就是改配置 + 让宿主建/收窗口
        internal void ToggleTaskbarItem()
        {
            ToggleTaskbar();
        }

        // 供 AppMenu 调用：退出要经过宿主，连托盘一起收
        internal void Quit()
        {
            QuitApp();
        }

        // 卡片和托盘由同一个宿主管着，退出要走宿主（连托盘一起收）；
        // 没装宿主（比如只想起一张卡片）就退回老行为：关掉自己，进程跟着退出
        internal Action QuitHook;

        // 设置页里勾/取消"显示托盘图标"时由宿主实现：开 = new 一个、关 = Dispose
        internal Action<bool> TrayHook;

        // 任务栏窄条：同样是宿主建/收
        internal Action<bool> StripHook;

        // 改了"窄条显示哪几项"之后让窄条立刻重画（不等它自己那一拍定时器）
        internal Action StripRefresh;

        // 每采完一次样就通知一遍：托盘图标原来自己走 5 秒定时器，相位跟卡片对不上，
        // 插拔电源时会出现"卡片已经绿了、托盘还蓝着"的几秒空窗。现在改成卡片采完就喊一声
        internal Action SampleChanged;

        // explorer 重启（任务栏换了个新 HWND）时由宿主重新挂一次
        internal Action TaskbarCreatedHook;

        private void QuitApp()
        {
            if (QuitHook != null) QuitHook();
            else BeginInvoke(new Action(Close));
        }

        // ── 给托盘菜单用的入口：托盘和卡片必须改同一份 _cfg，不能各存各的 ──
        internal Sample Current { get { return _sample; } }
        internal Config Cfg { get { return _cfg; } }
        internal void OpenStats() { OpenPanel(SettingsForm.PAGE_STATS); }
        internal static bool AutoStartOn { get { return IsAutoStart(); } }
        internal static void SetAutoStartOn(bool on) { SetAutoStart(on); }
        internal bool CardVisible { get { return Visible; } }

        internal void ShowCard()
        {
            if (!Visible) Show();
        }

        internal void HideCard()
        {
            HideRowTip();
            if (Visible) Hide();
        }

        // 卡片高度：电量行固定占 54，下面每多一行信息加 RowPitch
        // 功率和状态共用同一行（"充电中 6.2W"），所以它们只要有一个开着就占一行；
        // 蓝牙是每台设备一行（耳机 / 键盘 / 手柄），断开就自动消失。
        // 本机信息（温度/状态）和蓝牙设备之间空 BtGap 一档，两组才分得开
        private static int CalcHeight(Config cfg, float sc, int btDevices)
        {
            if (sc <= 0) sc = 1f;
            int local = 0;
            if (cfg.ShowTemp) local++;
            if (cfg.ShowState || cfg.ShowWatt) local++;

            int bt = 0;
            int more = 0;                               // "还有 N 台"那一行（纯文字，不加高行距）
            if (cfg.ShowBt && btDevices > 0)
            {
                bt = Math.Min(btDevices, MaxBtRows);
                if (btDevices > MaxBtRows) more = 1;
            }

            float h = 54f + RowPitch * (local + more);
            // 蓝牙设备行单独用大行距：一台一台是独立记录，15 挨着排像一段连着的文字，
            // 19 才看得出是几条。首行仍按 RowPitch 起（上面已经有 BtGap 隔开了），
            // 最后一行的行距不参与算高度 —— 它后面没内容了，不该给卡片尾巴留白
            if (bt > 0) h += RowPitch + BtPitch * (bt - 1);
            if (local > 0 && bt > 0) h += BtGap;        // 两组之间那一档空隙
            return (int)Math.Round(h * sc);
        }

        // 本机信息行（温度 / 充放电）的行距（sc 单位）。蓝牙设备行另用 BtPitch，
        // 两组之间只靠 BtGap 拉开，不靠无限加大行距
        private const float RowPitch = 15f;

        // 蓝牙设备行之间的行距（sc 单位）：比本机信息行的 15 宽一档
        private const float BtPitch = 19f;

        // 本机信息行与蓝牙设备行之间的额外间距（sc 单位）。两行文字紧挨着会分不清
        // 哪条是本机的、哪条是外设的，空一档就分开了
        private const float BtGap = 5f;

        // 切换显示内容或蓝牙设备连断后重算窗口高度：底边留在原位（往上缩，而不是整体上移）
        // 卡片高度按"真正会画出来的蓝牙行数"算，不能用 _sample.Bt.Count ——
        // 没上报电量的设备（音箱、手柄）已经不进卡片了（见 CardRender.VisibleBt），
        // 还照原数量算就会在底下空出一行，看着像给另一台耳机留了位。
        private int BtRowCount()
        {
            if (_sample == null || _sample.Bt == null || !_cfg.ShowBt) return 0;
            int n = 0;
            for (int i = 0; i < _sample.Bt.Count; i++)
            {
                BtDevice d = _sample.Bt[i];
                // 判据必须和 CardRender.VisibleBt 一模一样，否则会算少一行 → 底部空出一块 / 内容被切
                if (!Sensors.ShowAllBt && (d.Percent < 0 || d.Percent > 100)) continue;
                n++;
            }
            return n;
        }

        private void ApplyHeight()
        {
            float sc = _cfg.Scale <= 0 ? 1f : _cfg.Scale;
            int nw, nh;
            if (_cfg.Horizontal)
            {
                // 横向的宽度也跟着内容走（设备增减、名字长短都会变），所以宽也要一起比
                Size hs = MeasureHorizontal();
                nw = Math.Max(60, hs.Width);
                nh = Math.Max(30, hs.Height);
            }
            else
            {
                nw = _w;
                nh = CalcHeight(_cfg, sc, BtRowCount());
            }
            if (nw == _w && nh == _h) return;

            // 右下角是锚点：变宽变高都往左上长，原地不动会顶出屏幕
            int dw = _w - nw, dh = _h - nh;
            _w = nw; _h = nh;
            Size = new Size(_w, _h);
            Location = ClampToWorkArea(Location.X + dw, Location.Y + dh);
            SavePosition();      // 尺寸变了，文件里的"最小右下间距"要跟着改，不然下次启动会跳
        }

        // 纵向 / 横向切换（菜单用）
        internal void ToggleHorizontal()
        {
            _cfg.Horizontal = !_cfg.Horizontal;
            try { _cfg.Save(AppPaths.Ini); } catch { }
            ApplyConfig();
            ForceRefresh();
        }

        // 温度 / 状态与功率 / 蓝牙开关：决定卡片有几行，蓝牙行数还取决于真的连了几台设备
        private void ToggleInfoRow(int menuId)
        {
            if (menuId == MENU_TEMP)
            {
                _cfg.ShowTemp = !_cfg.ShowTemp;
                SaveFlag("showtemp", _cfg.ShowTemp);
            }
            else if (menuId == MENU_STATE)
            {
                // 菜单里这两项是合成的：任一项开着就算"开"，点一下两项一起关；都关着就一起开。
                // 设置页仍然保留各自的复选框，可能出现只有一项开的状态，这时点菜单会一起收掉。
                bool on = !(_cfg.ShowState || _cfg.ShowWatt);
                _cfg.ShowState = on;
                _cfg.ShowWatt = on;
                SaveFlag("showstate", on);
                SaveFlag("showwatt", on);
            }
            else
            {
                _cfg.ShowBt = !_cfg.ShowBt;
                SaveFlag("showbt", _cfg.ShowBt);
            }
            ApplyHeight();                  // 先改窗口尺寸，Render 才知道新的 _h
            try { Render(); } catch { }     // 立刻重绘，不用等下一次刷新
        }

        // 任务栏窄条开关：跟托盘一个套路 —— 改同一份 _cfg、写回 ini、由宿主负责建/收窗口
        private void ToggleTaskbar()
        {
            _cfg.Taskbar = !_cfg.Taskbar;
            SaveFlag("taskbar", _cfg.Taskbar);
            if (StripHook != null)
            {
                try { StripHook(_cfg.Taskbar); } catch { }
            }
        }

        // 只改指定那一行，其余注释和配置原样保留；没有这一行就追加到末尾
        private void SaveFlag(string key, bool value)
        {
            Config.SetKey(_iniPath, key, value ? "true" : "false");
        }

        // 面板是普通窗口（有任务栏按钮、能激活），和穿透的卡片互不干扰。
        // 单实例：重复打开只是切页 + 提到前面，不会叠出好几个。
        internal void OpenPanel(int page)
        {
            try
            {
                if (_panel == null || _panel.IsDisposed)
                {
                    SettingsForm f = new SettingsForm(this, _cfg);
                    f.FormClosed += delegate { if (ReferenceEquals(_panel, f)) _panel = null; };
                    _panel = f;
                }
                if (_panel.WindowState == FormWindowState.Minimized) _panel.WindowState = FormWindowState.Normal;
                _panel.ShowPage(page);
                _panel.Show();
                _panel.Activate();
                _panel.BringToFront();
            }
            catch (Exception ex) { WriteDiag("OpenPanel: " + ex); }
        }

        // 设置页"保存并应用"后走这里：缩放 / 不透明度 / 主题 / 刷新间隔 / 托盘开关 全部就地生效，
        // 不用退出重开（唯一需要重启的是"记录间隔"，它只影响下一次记录的时间点，不必重开）
        internal void ApplyConfig()
        {
            float sc = _cfg.Scale <= 0 ? 1f : _cfg.Scale;

            // 缩放一改，宽高都跟着变。先记下当前离工作区右、底两边的距离，换完尺寸再按同样的
            // 距离摆回去 —— 以前是原地不动再夹紧，窗口变大就顶出屏幕，夹完后右边贴边、
            // 底边还留着一截，看着就是"两边间距不一样"
            Rectangle wa0;
            int gapR, gapB;
            try
            {
                wa0 = Screen.FromPoint(new Point(Location.X + _w / 2, Location.Y + _h / 2)).WorkingArea;
                gapR = wa0.Right - (Location.X + _w);
                gapB = wa0.Bottom - (Location.Y + _h);
            }
            catch
            {
                wa0 = Screen.PrimaryScreen.WorkingArea;
                gapR = _cfg.MarginRight;
                gapB = _cfg.MarginBottom;
            }

            _light = IsLightTheme(_cfg);
            _timer.Interval = Math.Max(2, _cfg.RefreshSeconds) * 1000;
            if (_cfg.Horizontal)
            {
                Size hs = MeasureHorizontal();
                _w = Math.Max(60, hs.Width);
                _h = Math.Max(30, hs.Height);
            }
            else
            {
                _w = (int)Math.Round(134 * sc);     // 同构造函数
                _h = CalcHeight(_cfg, sc, BtRowCount());
            }
            Size = new Size(_w, _h);
            Location = ClampToWorkArea(wa0.Right - _w - gapR, wa0.Bottom - _h - gapB);
            SavePosition();
            if (TrayHook != null)
            {
                try { TrayHook(_cfg.Tray); } catch { }
            }
            if (StripHook != null)
            {
                try { StripHook(_cfg.Taskbar); } catch { }
            }
            if (StripRefresh != null) { try { StripRefresh(); } catch { } }
            if (!CardVisible && !_cfg.Tray) ShowCard();   // 托盘关了又没卡片，就再也叫不回来了
            ForceRefresh();
        }

        // 立刻重采一次（设置面板的"刷新"、改完配置后都用它）：不写历史、不写日志
        internal void ForceRefresh()
        {
            try
            {
                Sensors.InvalidateCache();
                _sample = Sensors.Read();
            }
            catch { }
            ApplyHeight();
            try { Render(); } catch { }
            if (SampleChanged != null) { try { SampleChanged(); } catch { } }
        }

        // 背景亮度取上下两端的平均（Rec.601 权重），阈值 140 划分深/浅主题
        private static bool IsLightTheme(Config cfg)
        {
            int lum = (cfg.BgTopR * 299 + cfg.BgTopG * 587 + cfg.BgTopB * 114
                     + cfg.BgBotR * 299 + cfg.BgBotG * 587 + cfg.BgBotB * 114) / 2000;
            return lum > 140;
        }

        // 动态开关鼠标穿透：开 = 窗口能收到鼠标消息（才能显示手型），关 = 完全穿透
        private void SetHittable(bool on)
        {
            if (_hittable == on) return;
            if (!IsHandleCreated) return;
            try
            {
                int cur = GetWindowLong(Handle, GWL_EXSTYLE);
                int next = on ? (cur & ~WS_EX_TRANSPARENT) : (cur | WS_EX_TRANSPARENT);
                if (next == cur) { _hittable = on; return; }
                SetWindowLong(Handle, GWL_EXSTYLE, next);

                // 写完回读确认，万一没生效就别把 _hittable 置真，否则卡片会一直吃掉点击
                int chk = GetWindowLong(Handle, GWL_EXSTYLE);
                bool ok = on ? (chk & WS_EX_TRANSPARENT) == 0 : (chk & WS_EX_TRANSPARENT) != 0;
                if (!ok) return;

                _hittable = on;
                if (on)
                {
                    if (_handCur != IntPtr.Zero) SetCursor(_handCur);   // 立刻生效，不必等鼠标动
                }
                else if (_arrowCur != IntPtr.Zero)
                {
                    SetCursor(_arrowCur);                              // 松手/移出后还原箭头
                }
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 从托盘再显示回来也会走到这里：定时器已经在跑，重画一次就够，
            // 不能把 _timer.Start() 再执行一遍（否则采样节奏会被重置）
            if (_started)
            {
                try { Render(); } catch { }
                return;
            }
            _started = true;
            try { _sample = Sensors.Read(); } catch { }
            ApplyHeight();          // 首次采样后才知道有几台蓝牙设备，先把高度定下来再画
            _animTimer.Start();
            MaybeRecord();          // 启动就先落一个点，短时间运行也能看到曲线
            Render();
            _timer.Start();
            if (!Sensors.LinkReady())
            {
                _warmLeft = 16;          // 最多补 11 秒，之后就算没探出来也不折腾了
                _warmTimer.Start();
            }
        }

        // 预热补拍：每 0.7 秒重读一次，直到"谁连着"有结果
        private void OnWarmTick(object sender, EventArgs e)
        {
            OnTick(null, EventArgs.Empty);
            if (Sensors.LinkReady() || --_warmLeft <= 0) _warmTimer.Stop();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _timer.Stop();
            _warmTimer.Stop();
            _dragTimer.Stop();
            _animTimer.Stop();
            SetHittable(false);   // 退出前恢复穿透 + 箭头，别把光标留在手型
            SavePosition();
            base.OnFormClosing(e);
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { _sample = Sensors.Read(); } catch { }
            // 蓝牙设备连上/断开会让信息行数变化，高度要跟着调（底边不动，向上长或缩）
            ApplyHeight();
            MaybeRecord();
            if (_cfg.Debug) LogTick();
            // 任务栏挪位、分辨率变化、拔掉外接屏后，把卡片拉回工作区
            if (!_dragging) Location = ClampToWorkArea(Location.X, Location.Y);
            Render();
            if (_sample != null && ChargeVisual.Bolt(_sample)) _animTimer.Start();
            if (SampleChanged != null) { try { SampleChanged(); } catch { } }
        }

        // ── 充电动画：真在进电时每 350ms 重画一拍，闪电的透明度按相位呼吸 ────
        // 只在"卡片可见 + 真在进电"时跑；拔掉电源或隐藏卡片就自己停，不空转
        private void OnAnimTick()
        {
            try
            {
                if (!Visible) { _animTimer.Stop(); return; }
                Sample s = _sample;
                if (s == null || !ChargeVisual.Bolt(s)) { _animTimer.Stop(); return; }
                Render();
            }
            catch { }
        }

        // ── Ctrl + 悬停：蓝牙设备详情卡 ──────────────────────────────────────
        // 和窄条那张提示卡是同一个控件、同一份文案（BtTip），两处长得一样。
        // 条件是"按住 Ctrl"：平时划过卡片不该弹框，那会挡视线
        private StripTip _rowTip;
        private string _rowTipSig = "";

        private void OnTipTick()
        {
            try
            {
                if (!Visible) { HideRowTip(); return; }
                if ((Control.ModifierKeys & Keys.Control) == 0) { HideRowTip(); return; }

                Point cur = Cursor.Position;
                Point p = PointToClient(cur);
                List<BtHit> hits = BtHits;
                BtHit hit = null;
                for (int i = 0; i < hits.Count; i++)
                {
                    if (hits[i].Rect.Contains(p.X, p.Y)) { hit = hits[i]; break; }
                }
                if (hit == null) { HideRowTip(); return; }

                if (_rowTip == null) _rowTip = new StripTip();
                string title = BtTip.Title(hit.Dev);
                string[] lines = BtTip.Lines(hit.Dev);
                string sig = title + "\u0001" + string.Join("\u0002", lines);
                if (sig != _rowTipSig)
                {
                    _rowTipSig = sig;
                    _rowTip.SetContent(_light, _cfg.Scale <= 0 ? 1f : _cfg.Scale, title, lines);
                }

                int tw = _rowTip.Width, th = _rowTip.Height;
                Rectangle wa;
                try { wa = Screen.FromPoint(cur).WorkingArea; }
                catch { wa = Screen.PrimaryScreen.WorkingArea; }
                int gp = (int)Math.Round(6 * (_cfg.Scale <= 0 ? 1f : _cfg.Scale));

                // 默认贴光标右下；右边/下边放不下就翻到另一侧，别越出工作区。
                // 往左翻的时候直接贴到卡片左边外侧 —— 卡片本来就贴屏幕右边，翻过来的框
                // 如果只是"光标左边那一点"，会正好压住卡片上的设备行，反而看不见
                int x = cur.X + gp;
                int y = cur.Y + gp;
                if (x + tw > wa.Right - gp)
                {
                    x = Left - tw - gp;
                    if (x < wa.Left + gp) x = cur.X - gp - tw;
                }
                if (y + th > wa.Bottom - gp) y = cur.Y - gp - th;
                if (x < wa.Left + gp) x = wa.Left + gp;
                if (y < wa.Top + gp) y = wa.Top + gp;

                _rowTip.PlaceAt(x, y);
                _rowTip.ShowNoActivate();
            }
            catch { }
        }

        private void HideRowTip()
        {
            _rowTipSig = "";
            try { if (_rowTip != null && _rowTip.Visible) _rowTip.Hide(); } catch { }
        }

        // 每 RecordMinutes 分钟记一个点（采样本身是 5 秒一次，这里只做节流）。
        // 本机没电池时也别整条跳过：蓝牙设备同样要统计，交给 History 那边分开判断
        private void MaybeRecord()
        {
            if (_sample == null) return;
            bool hasBt = _sample.Bt != null && _sample.Bt.Count > 0;
            if (_sample.Percent < 0 && !hasBt) return;
            DateTime now = DateTime.Now;
            if (_lastRecAt != DateTime.MinValue &&
                (now - _lastRecAt).TotalMinutes < _cfg.RecordMinutes - 0.01) return;
            History.Append(_sample);
            _lastRecAt = now;
        }

        // 诊断日志：确认真的是"每 5 秒都在读"，而不是程序停了
        private void LogTick()
        {
            try
            {
                string temp = TempText();
                string state = StateLineText();
                List<string> bt = BtLines();
                string shown = DrawPercentText()
                    + (temp.Length > 0 ? " | " + temp : "")
                    + (state.Length > 0 ? " | " + state : "");
                for (int i = 0; i < bt.Count; i++) shown += " | " + bt[i];
                if (shown == _lastLogText && (DateTime.Now - _lastLogAt).TotalSeconds < 60) return;

                string devLog = "-";
                if (_sample.Bt != null && _sample.Bt.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < _sample.Bt.Count; i++)
                    {
                        BtDevice d = _sample.Bt[i];
                        if (i > 0) sb.Append('/');
                        sb.Append(d.Label).Append(':').Append(d.Percent).Append('@').Append(d.Address);
                        if (d.Model.Length > 0) sb.Append('(').Append(d.Model).Append(')');
                        // vol = 这台设备自己的播放端点音量；box = 容器 ID，配不上端点时对比用
                        if (d.Kind == BtKind.Headphone || d.Kind == BtKind.Speaker)
                            sb.Append(" vol=").Append(Audio.VolumeFor(d.Container, d.Model));
                        if (d.Container.Length > 8) sb.Append(" box=").Append(d.Container.Substring(0, 8));
                    }
                    devLog = sb.ToString();
                }

                string line = string.Format(CultureInfo.InvariantCulture,
                    "{0} pct={1,-4} plug={2} chg={3} full={4} temp={5} watt={6} bt={7} dev={8} link={9} drop={10} >> {11} | audio={12}",
                    DateTime.Now.ToString("HH:mm:ss"),
                    _sample.Percent,
                    _sample.AcOnline ? 1 : 0,
                    _sample.Charging ? 1 : 0,
                    _sample.Full ? 1 : 0,
                    double.IsNaN(_sample.TempC) ? "-" : _sample.TempC.ToString("0", CultureInfo.InvariantCulture),
                    double.IsNaN(_sample.Watts)
                        ? "-"
                        : _sample.Watts.ToString("0.0", CultureInfo.InvariantCulture),
                    bt.Count == 0 ? "-" : string.Join("/", bt.ToArray()),
                    devLog,
                    Sensors.LinkLog(),
                    Sensors.BtDiag(),
                    shown,
                    Audio.Diag());

                _lastLogText = shown;
                _lastLogAt = DateTime.Now;

                // 超过 256 KB 就只留最后 300 行，防止无限膨胀
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 262144)
                {
                    string[] all = File.ReadAllLines(_logPath);
                    int keep = Math.Min(all.Length, 300);
                    string[] tail = new string[keep];
                    Array.Copy(all, all.Length - keep, tail, 0, keep);
                    File.WriteAllLines(_logPath, tail);
                }
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch { }
        }

        // 把窗口夹进"工作区"：WorkingArea 本身不含任务栏，所以既拖不上去也盖不住任务栏
        private Point ClampToWorkArea(int x, int y)
        {
            Rectangle wa;
            try { wa = Screen.FromPoint(new Point(x + _w / 2, y + _h / 2)).WorkingArea; }
            catch { return new Point(x, y); }

            if (x + _w > wa.Right) x = wa.Right - _w;
            if (y + _h > wa.Bottom) y = wa.Bottom - _h;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            return new Point(x, y);
        }

        // 轮询式拖动：窗口带 WS_EX_TRANSPARENT 收不到鼠标消息，只能看全局按键状态
        private void OnDragTick(object sender, EventArgs e)
        {
            POINT p;
            if (!GetCursorPos(out p)) return;
            bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            Rectangle r = new Rectangle(Location.X, Location.Y, _w, _h);
            bool inside = r.Contains(p.X, p.Y);
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

            // 光标状态与是否按键无关，必须在下面的提前 return 之前更新
            UpdateCursor(inside, ctrl);

            if (_dragging)
            {
                if (!down)
                {
                    _dragging = false;
                    SavePosition();
                    UpdateCursor(inside, ctrl);
                    return;
                }
                Point np = ClampToWorkArea(p.X - _grabDx, p.Y - _grabDy);
                if (np.X != Location.X || np.Y != Location.Y) Location = np;
                return;
            }

            if (!down)
            {
                _pressOutside = false;
                _wasDown = false;
                return;
            }

            // 这一轮按下是从卡片外面开始的 → 整段按住期间都不抢窗口
            if (!_wasDown && !inside) _pressOutside = true;

            bool ctrlOk = !_cfg.DragNeedsCtrl || ctrl;
            // 需要 Ctrl 时，允许"先按下、再补按 Ctrl"；不需要 Ctrl 时只认刚按下的那一下，
            // 否则鼠标按住从别处滑过卡片就会把窗口粘走
            bool canStart = ctrlOk && !_pressOutside && !_menuOpen && (_cfg.DragNeedsCtrl || !_wasDown);

            if (canStart && inside)
            {
                _dragging = true;
                _grabDx = p.X - Location.X;
                _grabDy = p.Y - Location.Y;
            }
            _wasDown = down;
        }

        // 悬停 + Ctrl → 手型光标；其余情况回到穿透 + 箭头
        private void UpdateCursor(bool inside, bool ctrl)
        {
            if (_cfg.DragNeedsCtrl)
            {
                bool want = _dragging || (ctrl && inside);
                SetHittable(want);
            }
            else if (inside && _handCur != IntPtr.Zero)
            {
                // 不需要 Ctrl 的模式下窗口必须保持全穿透，只能硬设光标兜底（手型会轻微闪）
                SetCursor(_handCur);
            }
        }

        // 开机自启动：写 HKCU\...\Run，值和任务管理器「启动」页看到的完全一致
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "Battery Monitor";
        private const string OldRunValueName = "BatteryFloat";   // 改名前写进去的那个，迁移完就删

        private static string AutoStartValue()
        {
            return "\"" + Application.ExecutablePath + "\"";
        }

        private static bool IsAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (k == null) return false;
                    string v = k.GetValue(RunValueName) as string;
                    if (string.IsNullOrEmpty(v)) return false;

                    string s = v.Trim();
                    if (s.StartsWith("\""))
                    {
                        int e = s.IndexOf('"', 1);
                        if (e > 1) s = s.Substring(1, e - 1);
                    }
                    else
                    {
                        int sp = s.IndexOf(' ');
                        if (sp > 0) s = s.Substring(0, sp);
                    }

                    return string.Equals(Path.GetFullPath(s), Path.GetFullPath(Application.ExecutablePath),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValueName, AutoStartValue(), RegistryValueKind.String);
                    else k.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }

        // 改名迁移：旧值 BatteryFloat 指向的是 BatteryFloat.exe，改名后 IsAutoStart 会因为
        // "路径不等于当前 exe"判成没开，用户的开机自启动就静默失效了。这里把值搬到新名下，
        // 再删掉旧值，任务管理器「启动」页不会同时出现两条。
        internal static void MigrateAutoStart()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return;
                    string old = k.GetValue(OldRunValueName) as string;
                    if (string.IsNullOrEmpty(old)) return;
                    if (k.GetValue(RunValueName) == null)
                        k.SetValue(RunValueName, AutoStartValue(), RegistryValueKind.String);
                    k.DeleteValue(OldRunValueName, false);
                }
            }
            catch { }
        }

        // 位置文件：x,y,工作区左,上,右,下
        // 后四个数是"保存这一拍屏幕长什么样"。老版本只存了 x,y（两个字段），
        // 读不出来就当作没保存过 —— 谁也不知道那个坐标属于哪块屏幕，宁可回到右下角。
        private bool TryLoadPosition(Rectangle wa, out Point pos)
        {
            pos = Point.Empty;
            try
            {
                if (!File.Exists(_posPath)) return false;
                string[] parts = File.ReadAllText(_posPath).Split(',');

                // 老文件只有 6 段（绝对左上角 + 工作区），跟现在的卡片宽高对不上 ——
                // 宽度从 122 改成 130 之后，旧 x 加上新宽度就顶出屏幕，夹紧后右边贴边、
                // 底边还留着一段，正是"两边间距不一样"的来源。这种记录直接不认，
                // 按默认的右下角边距重新摆，启动时会用新格式写回
                if (parts.Length < 8) return false;

                int x, y, l, t, r, b, gapR, gapB;
                if (!int.TryParse(parts[0].Trim(), out x)) return false;
                if (!int.TryParse(parts[1].Trim(), out y)) return false;
                if (!int.TryParse(parts[2].Trim(), out l)) return false;
                if (!int.TryParse(parts[3].Trim(), out t)) return false;
                if (!int.TryParse(parts[4].Trim(), out r)) return false;
                if (!int.TryParse(parts[5].Trim(), out b)) return false;
                if (!int.TryParse(parts[6].Trim(), out gapR)) return false;
                if (!int.TryParse(parts[7].Trim(), out gapB)) return false;

                // 屏幕动过（换分辨率、接投影、远程桌面改了尺寸）就不认旧坐标
                if (l != wa.Left || t != wa.Top || r != wa.Right || b != wa.Bottom) return false;

                // 末两段是"距工作区右边 / 底边"的距离。按间距反算左上角，
                // 卡片宽度高度怎么改，右下角那一圈缝都还是原样
                pos = new Point(wa.Right - _w - gapR, wa.Bottom - _h - gapB);
                return true;
            }
            catch { return false; }
        }

        private void SavePosition()
        {
            try
            {
                Rectangle wa = Screen.FromPoint(new Point(Location.X + _w / 2, Location.Y + _h / 2)).WorkingArea;
                // 前 6 段（左上角 + 工作区）只是留个可读的现场；真正用来定位的是后两段：
                // 离工作区右边、底边各多少像素。存绝对坐标的话，卡片一改宽高，
                // 旧坐标就不再是"右下角"了 —— 右边被顶出屏幕夹成 0，底边却还留着一段
                File.WriteAllText(_posPath, string.Format("{0},{1},{2},{3},{4},{5},{6},{7}",
                    Location.X, Location.Y, wa.Left, wa.Top, wa.Right, wa.Bottom,
                    wa.Right - (Location.X + _w), wa.Bottom - (Location.Y + _h)));
            }
            catch { }
        }
    }
}
