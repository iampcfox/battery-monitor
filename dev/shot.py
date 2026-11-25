# -*- coding: utf-8 -*-
"""纯 ctypes 截屏：BitBlt 一块屏幕区域到 DIB，最近邻放大后写 PNG。
不编译 C#、不碰 Add-Type（这两条在这台机器上都被安全策略拦着）。
用法：python dev/shot.py x y w h [scale] [out]
"""
import ctypes as C
import struct
import sys
import zlib

user32 = C.windll.user32
gdi32 = C.windll.gdi32

SRCCOPY = 0x00CC0020
BI_RGB = 0
DIB_RGB_COLORS = 0


class BITMAPINFOHEADER(C.Structure):
    _fields_ = [("biSize", C.c_uint32), ("biWidth", C.c_int32), ("biHeight", C.c_int32),
                ("biPlanes", C.c_uint16), ("biBitCount", C.c_uint16),
                ("biCompression", C.c_uint32), ("biSizeImage", C.c_uint32),
                ("biXPelsPerMeter", C.c_int32), ("biYPelsPerMeter", C.c_int32),
                ("biClrUsed", C.c_uint32), ("biClrImportant", C.c_uint32)]


class BITMAPINFO(C.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", C.c_uint32 * 3)]


def grab(x, y, w, h):
    hdc_screen = user32.GetDC(None)
    mdc = gdi32.CreateCompatibleDC(hdc_screen)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = C.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h          # 负数 = 自上而下，省得再翻
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = BI_RGB
    bits = C.c_void_p()
    hbmp = gdi32.CreateDIBSection(hdc_screen, C.byref(bmi), DIB_RGB_COLORS,
                                  C.byref(bits), None, 0)
    gdi32.SelectObject(mdc, hbmp)
    gdi32.BitBlt(mdc, 0, 0, w, h, hdc_screen, x, y, SRCCOPY)
    gdi32.GdiFlush()
    buf = C.string_at(bits, w * h * 4)
    gdi32.DeleteObject(hbmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(None, hdc_screen)
    return buf


def zoom(buf, w, h, s):
    out = bytearray()
    row = w * 4
    for y in range(h):
        line = buf[y * row:(y + 1) * row]
        wide = bytearray()
        for i in range(0, row, 4):
            px = line[i:i + 4]
            wide += px * s
        out += wide * s
    return bytes(out), w * s, h * s


def save_png(path, buf, w, h):
    rows = []
    for y in range(h):
        r = bytearray(buf[y * w * 4:(y + 1) * w * 4])
        r[0::4], r[2::4] = r[2::4], r[0::4]      # BGRA -> RGBA
        r[3::4] = b"\xff" * w                    # 屏幕截图没有透明，alpha 拉满
        rows.append(bytes(r))
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))
    open(path, "wb").write(png)


def main():
    a = sys.argv[1:]
    x, y, w, h = (int(a[0]), int(a[1]), int(a[2]), int(a[3])) if len(a) >= 4 else (0, 0, 1920, 1080)
    s = int(a[4]) if len(a) >= 5 else 1
    out = a[5] if len(a) >= 6 else "dev/shots/shot.png"
    buf = grab(x, y, w, h)
    if s > 1:
        buf, w, h = zoom(buf, w, h, s)
    save_png(out, buf, w, h)
    print("saved %s  %dx%d" % (out, w, h))


if __name__ == "__main__":
    main()
