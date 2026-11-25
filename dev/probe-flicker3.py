# -*- coding: utf-8 -*-
# 诊断窄条"闪烁"（第三轮）：条到底是"藏了"、"被盖了"还是"变透明了"。
# 每 25ms 采：条自身 rect / IsWindowVisible / 分层样式 / DWM cloak /
# WindowFromPoint(条中心) 落到了谁身上 / 条那块像素哈希。
import ctypes, ctypes.wintypes as wt, sys, time, hashlib

u32 = ctypes.WinDLL('user32', use_last_error=True)
g32 = ctypes.WinDLL('gdi32', use_last_error=True)
dwm = ctypes.WinDLL('dwmapi', use_last_error=True)
try:
    u32.SetProcessDPIAware()
except Exception:
    pass


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


class POINT(ctypes.Structure):
    _fields_ = [('x', ctypes.c_long), ('y', ctypes.c_long)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', ctypes.c_uint32), ('biWidth', ctypes.c_long),
                ('biHeight', ctypes.c_long), ('biPlanes', ctypes.c_ushort),
                ('biBitCount', ctypes.c_ushort), ('biCompression', ctypes.c_uint32),
                ('biSizeImage', ctypes.c_uint32), ('biXPelsPerMeter', ctypes.c_long),
                ('biYPelsPerMeter', ctypes.c_long), ('biClrUsed', ctypes.c_uint32),
                ('biClrImportant', ctypes.c_uint32)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER), ('bmiColors', ctypes.c_uint32 * 3)]


EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
u32.WindowFromPoint.restype = wt.HWND
u32.WindowFromPoint.argtypes = [POINT]


def title(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = RECT()
    if not u32.GetWindowRect(h, ctypes.byref(r)):
        return None
    return (r.left, r.top, r.right, r.bottom)


def grab(x, y, w, h):
    sdc = u32.GetDC(0)
    mdc = g32.CreateCompatibleDC(sdc)
    bmp = g32.CreateCompatibleBitmap(sdc, w, h)
    g32.SelectObject(mdc, bmp)
    g32.BitBlt(mdc, 0, 0, w, h, sdc, x, y, 0x00CC0020)
    bi = BITMAPINFO()
    bi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.bmiHeader.biWidth = w
    bi.bmiHeader.biHeight = -h
    bi.bmiHeader.biPlanes = 1
    bi.bmiHeader.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    g32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)
    g32.DeleteObject(bmp)
    g32.DeleteDC(mdc)
    u32.ReleaseDC(0, sdc)
    return buf.raw


def click(x, y):
    u32.SetCursorPos(x, y)
    u32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(0x0004, 0, 0, 0, 0)


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 9.0
    do_click = '--click' in sys.argv
    found = []

    def cb(h, p):
        if 'BatteryMonitorStrip' in title(h):
            found.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    tb = u32.FindWindowW("Shell_TrayWnd", None)
    taskbar0 = tb
    u32.EnumChildWindows(taskbar0, EnumProc(cb), 0)   # 嵌进任务栏时它不是顶层窗口
    strip = found[0]
    t0 = time.time()
    print('strip=%s rect=%s' % (hex(strip), rect(strip)))

    def chevron():
        # 抽屉箭头 = TrayNotifyWnd 里最左边那个 32 宽的 Button，位置随托盘宽度变，得现算
        tray = u32.FindWindowExW(tb, None, 'TrayNotifyWnd', None)
        best = None
        arr = []

        def c2(h, p):
            arr.append(h)
            return True

        u32.EnumChildWindows(tray, EnumProc(c2), 0)
        for h in arr:
            if cls(h) != 'Button':
                continue
            r = rect(h)
            if r and 20 <= r[2] - r[0] <= 48 and (best is None or r[0] < best[0]):
                best = r
        if not best:
            best = (954, 728, 986, 768)
        return ((best[0] + best[2]) // 2, (best[1] + best[3]) // 2)

    last = None
    click_at = [1.0, 2.5, 4.0, 5.5, 7.0]
    while time.time() - t0 < secs:
        now = time.time() - t0
        while click_at and now >= click_at[0]:
            print('%6.2f CLICK' % now)
            click(*chevron())
            click_at.pop(0)
        r = rect(strip)
        cx, cy = (r[0] + r[2]) // 2, (r[1] + r[3]) // 2
        p = POINT(cx, cy)
        above = u32.WindowFromPoint(p)
        cloaked = ctypes.c_int(0)
        dwm.DwmGetWindowAttribute(strip, 14, ctypes.byref(cloaked), 4)  # DWMWA_CLOAKED
        ex = u32.GetWindowLongW(strip, -20)
        vis = u32.IsWindowVisible(strip)
        hh = hashlib.md5(grab(r[0], r[1], r[2] - r[0], r[3] - r[1])).hexdigest()[:8]
        state = (r, bool(vis), ex & 0x00080000, cloaked.value,
                 hex(above), cls(above) if above != strip else 'SELF', hh)
        if state != last:
            print('%6.2f %s' % (now, state))
            last = state
        time.sleep(0.025)
    print('done')


main()
