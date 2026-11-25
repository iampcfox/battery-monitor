# -*- coding: utf-8 -*-
# 诊断窄条"闪烁"：以 25ms 为周期同时记录
#   1) 任务栏几何（MSTaskSwWClass / ReBarWindow32 / TrayNotifyWnd / 抽屉箭头 Button）
#   2) 窄条窗口自己的矩形
#   3) 窄条那一块屏幕像素的哈希（像素变了但几何没变 = 重绘闪；几何变了 = 位置跳）
# 可选：--click 在 1s / 3.5s 各模拟一次点击抽屉箭头，自己触发一次现场。
import ctypes, ctypes.wintypes as wt, sys, time, hashlib

u32 = ctypes.WinDLL('user32', use_last_error=True)
g32 = ctypes.WinDLL('gdi32', use_last_error=True)

try:
    ctypes.windll.user32.SetProcessDPIAware()
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


u32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(RECT)]
u32.IsWindowVisible.argtypes = [wt.HWND]
u32.GetParent.argtypes = [wt.HWND]
u32.FindWindowW.restype = wt.HWND
u32.FindWindowExW.restype = wt.HWND

EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def title(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def rect(h):
    r = RECT()
    if not u32.GetWindowRect(h, ctypes.byref(r)):
        return None
    return (r.left, r.top, r.right, r.bottom)


def children(h):
    out = []

    def cb(c, p):
        out.append(c)
        return True

    u32.EnumChildWindows(h, EnumProc(cb), 0)
    return out


def grab(x, y, w, h):
    """截一块屏幕像素，返回 (hash, bytes)"""
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
    data = buf.raw
    return hashlib.md5(data).hexdigest()[:10], data


def click(x, y):
    u32.SetCursorPos(x, y)
    u32.mouse_event(0x0002, 0, 0, 0, 0)   # LEFTDOWN
    time.sleep(0.05)
    u32.mouse_event(0x0004, 0, 0, 0, 0)   # LEFTUP


def main():
    do_click = '--click' in sys.argv
    secs = 9.0
    tb = u32.FindWindowW("Shell_TrayWnd", None)
    if not tb:
        print("no Shell_TrayWnd")
        return
    # 窄条可能是 Shell_TrayWnd 的子窗口，也可能是 SetParent 失败后退回的顶层浮窗
    found = []

    def cb(h, p):
        if 'BatteryMonitorStrip' in title(h):
            found.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    for h in children(tb):
        if 'BatteryMonitorStrip' in title(h):
            found.append(h)
    strip = found[0] if found else None
    print("taskbar=%s strip=%s cls=%s rect=%s" % (hex(tb), strip and hex(strip) or '-',
                                                 strip and cls(strip) or '-', strip and rect(strip)))
    if strip is None:
        return

    def geo():
        d = {}
        for h in children(tb):
            c = cls(h)
            if c in ("MSTaskSwWClass", "ReBarWindow32", "TrayNotifyWnd", "Button"):
                r = rect(h)
                if r:
                    d[c] = r
        d['strip'] = rect(strip)
        return d

    t0 = time.time()
    last_geo = None
    last_hash = None
    click_at = [1.0, 3.5]
    frame = 0
    while time.time() - t0 < secs:
        now = time.time() - t0
        while click_at and now >= click_at[0]:
            g = geo()
            print("%6.2f CLICK" % now)
            bt = g.get("Button") or [986, 728, 1018, 768]
            click((bt[0] + bt[2]) // 2, (bt[1] + bt[3]) // 2)
            click_at.pop(0)
        g = geo()
        r = g['strip']
        hh, _ = grab(r[0], r[1], r[2] - r[0], r[3] - r[1])
        if g != last_geo:
            print("%6.2f GEO  list=%s rebar=%s tray=%s btn=%s strip=%s" % (
                now, g.get("MSTaskSwWClass"), g.get("ReBarWindow32"),
                g.get("TrayNotifyWnd"), g.get("Button"), g.get('strip')))
            last_geo = g
        if hh != last_hash:
            print("%6.2f PIX  %s" % (now, hh))
            last_hash = hh
            frame += 1
        time.sleep(0.025)
    print("done")


main()
