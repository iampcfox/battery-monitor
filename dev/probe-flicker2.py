# -*- coding: utf-8 -*-
# 诊断窄条"闪烁"（第二轮）：抓帧。
# 以 25ms 采样窄条自己那块像素的哈希，哈希一变就把周边 400x220 的整块存成 PNG，
# 这样能看出到底是"条没了 / 条挪了 / 条被盖住 / 只是箭头换了图"。
import ctypes, ctypes.wintypes as wt, sys, time, hashlib, zlib, struct, os

u32 = ctypes.WinDLL('user32', use_last_error=True)
g32 = ctypes.WinDLL('gdi32', use_last_error=True)
try:
    u32.SetProcessDPIAware()
except Exception:
    pass


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


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


def save_png(path, w, h, bgra):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        row = bgra[y * w * 4:(y + 1) * w * 4]
        for x in range(w):
            b, g, r = row[x * 4], row[x * 4 + 1], row[x * 4 + 2]
            raw += bytes((r, g, b))

    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 6))
    png += chunk(b'IEND', b'')
    open(path, 'wb').write(png)


def click(x, y):
    u32.SetCursorPos(x, y)
    u32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(0x0004, 0, 0, 0, 0)


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 9.0
    do_click = '--click' in sys.argv
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'shots')
    if not os.path.isdir(out):
        os.makedirs(out)

    found = []

    def cb(h, p):
        if 'BatteryMonitorStrip' in title(h):
            found.append(h)
        return True

    u32.EnumWindows(EnumProc(cb), 0)
    if not found:
        print('strip not found')
        return
    strip = found[0]
    t0 = time.time()
    print('strip=%s rect=%s' % (hex(strip), rect(strip)))

    last = None
    n = 0
    click_at = [1.0, 3.5]
    while time.time() - t0 < secs:
        now = time.time() - t0
        while click_at and now >= click_at[0]:
            print('%6.2f CLICK' % now)
            click(1002, 748)
            click_at.pop(0)
        r = rect(strip)
        w, h = r[2] - r[0], r[3] - r[1]
        data = grab(r[0], r[1], w, h)
        hh = hashlib.md5(data).hexdigest()[:8]
        if hh != last:
            n += 1
            path = os.path.join(out, 'flick%02d.png' % n)
            # 存周边一大块：以窄条为基准往左/上各扩，能看清有没有被盖、有没有挪
            gx, gy = max(0, r[0] - 60), max(0, r[1] - 130)
            gw, gh = min(1366 - gx, 300), min(768 - gy, 200)
            wide = grab(gx, gy, gw, gh)
            save_png(path, gw, gh, wide)
            print('%6.2f PIX %s strip=%s -> %s' % (now, hh, r, os.path.basename(path)))
            last = hh
        time.sleep(0.025)
    print('done')


main()
