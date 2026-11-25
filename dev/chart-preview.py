# -*- coding: utf-8 -*-
"""离线预览历史统计图：用 ctypes 直接调 GDI 画到 DIB 上，再写 BMP。
不编译 C#、不依赖 PIL，只为在交付前确认轴/图例/曲线的位置没有互相压住。
蓝牙那两条是造的假数据（本地历史里还没有蓝牙记录），仅用于看多曲线时的排版。
"""
import ctypes as C
import datetime as dt
import struct

user32 = C.windll.user32
gdi32 = C.windll.gdi32

W, H = 846, 500
PLOT = (56, 42, 846 - 56 - 56, 500 - 42 - 46)   # l, t, w, h

BI_RGB = 0
DIB_RGB_COLORS = 0
TRANSPARENT = 1


class BITMAPINFOHEADER(C.Structure):
    _fields_ = [("biSize", C.c_uint32), ("biWidth", C.c_int32), ("biHeight", C.c_int32),
                ("biPlanes", C.c_uint16), ("biBitCount", C.c_uint16),
                ("biCompression", C.c_uint32), ("biSizeImage", C.c_uint32),
                ("biXPelsPerMeter", C.c_int32), ("biYPelsPerMeter", C.c_int32),
                ("biClrUsed", C.c_uint32), ("biClrImportant", C.c_uint32)]


class BITMAPINFO(C.Structure):
    _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", C.c_uint32 * 3)]


def rgb(r, g, b):
    return r | (g << 8) | (b << 16)


hdc_screen = user32.GetDC(None)
mdc = gdi32.CreateCompatibleDC(hdc_screen)

bmi = BITMAPINFO()
bmi.bmiHeader.biSize = C.sizeof(BITMAPINFOHEADER)
bmi.bmiHeader.biWidth = W
bmi.bmiHeader.biHeight = -H          # 负数 = 自上而下
bmi.bmiHeader.biPlanes = 1
bmi.bmiHeader.biBitCount = 32
bmi.bmiHeader.biCompression = BI_RGB

bits = C.c_void_p()
hbmp = gdi32.CreateDIBSection(hdc_screen, C.byref(bmi), DIB_RGB_COLORS, C.byref(bits), None, 0)
gdi32.SelectObject(mdc, hbmp)

_fonts = {}


def font(px, bold=False):
    key = (px, bold)
    if key in _fonts:
        gdi32.SelectObject(mdc, _fonts[key])
        return
    hf = gdi32.CreateFontW(-px, 0, 0, 0, 700 if bold else 400, 0, 0, 0, 134, 0, 0, 4, 0,
                           "Microsoft YaHei UI")
    _fonts[key] = hf
    gdi32.SelectObject(mdc, hf)


def text(x, y, s, color, px=13, bold=False):
    font(px, bold)
    gdi32.SetBkMode(mdc, TRANSPARENT)
    gdi32.SetTextColor(mdc, rgb(*color))
    gdi32.TextOutW(mdc, int(x), int(y), C.c_wchar_p(s), len(s))


def text_w(s, px=13, bold=False):
    font(px, bold)
    sz = C.wintypes.SIZE()
    gdi32.GetTextExtentPoint32W(mdc, C.c_wchar_p(s), len(s), C.byref(sz))
    return sz.cx, sz.cy


def fill(x, y, w, h, color):
    br = gdi32.CreateSolidBrush(rgb(*color))
    rc = C.wintypes.RECT(int(x), int(y), int(x + w), int(y + h))
    user32.FillRect(mdc, C.byref(rc), br)
    gdi32.DeleteObject(br)


def line(x1, y1, x2, y2, color, width=1):
    pen = gdi32.CreatePen(0, width, rgb(*color))
    gdi32.SelectObject(mdc, pen)
    gdi32.MoveToEx(mdc, int(x1), int(y1), None)
    gdi32.LineTo(mdc, int(x2), int(y2))
    gdi32.DeleteObject(pen)


def polyline(pts, color, width=2):
    pen = gdi32.CreatePen(0, width, rgb(*color))
    gdi32.SelectObject(mdc, pen)
    arr = (C.wintypes.POINT * len(pts))()
    for i, (x, y) in enumerate(pts):
        arr[i].x = int(x)
        arr[i].y = int(y)
    gdi32.Polyline(mdc, arr, len(pts))
    gdi32.DeleteObject(pen)


def dot(x, y, r, color):
    br = gdi32.CreateSolidBrush(rgb(*color))
    pen = gdi32.CreatePen(0, 1, rgb(*color))
    gdi32.SelectObject(mdc, br)
    gdi32.SelectObject(mdc, pen)
    gdi32.Ellipse(mdc, int(x - r), int(y - r), int(x + r), int(y + r))
    gdi32.DeleteObject(br)
    gdi32.DeleteObject(pen)


# ── 读真实历史 ────────────────────────────────────────────────
pts = []
try:
    for ln in open("battery-monitor-history.csv", encoding="utf-8-sig"):
        f = ln.strip().split(",")
        if len(f) < 2:
            continue
        try:
            t = dt.datetime.strptime(f[0], "%Y-%m-%d %H:%M:%S")
        except ValueError:
            continue
        pct = int(f[1])
        temp = float(f[2]) if len(f) > 2 and f[2].strip() else None
        pts.append((t, pct, temp))
except FileNotFoundError:
    pass

pts.sort(key=lambda p: p[0])
import sys
days = int(sys.argv[1]) if len(sys.argv) > 1 else 30
t0 = dt.datetime.combine(dt.date.today(), dt.time()) - dt.timedelta(days=days - 1)
t1 = dt.datetime.now()
pts = [p for p in pts if p[0] >= t0]   # 跟 ChartView.Reload 的 cutoff 一致


def xof(t):
    span = (t1 - t0).total_seconds() or 1
    f = max(0.0, min(1.0, (t - t0).total_seconds() / span))
    return PLOT[0] + f * PLOT[2]


def yof(v):
    v = max(0.0, min(100.0, v))
    return PLOT[1] + PLOT[3] - v / 100.0 * PLOT[3]


temps = [p[2] for p in pts if p[2] is not None]
if temps:
    lo, hi = min(temps), max(temps)
    if hi - lo < 6:
        mid = (hi + lo) / 2
        lo, hi = mid - 3, mid + 3
    pad = max(1.5, (hi - lo) * 0.12)
    tmin, tmax = lo - pad, hi + pad
else:
    tmin, tmax = 20, 45


def yof_temp(c):
    f = max(0.0, min(1.0, (c - tmin) / (tmax - tmin)))
    return PLOT[1] + PLOT[3] - f * PLOT[3]


# ── 画 ────────────────────────────────────────────────────────
fill(0, 0, W, H, (255, 255, 255))

# 图例
legend = [("电量", (34, 176, 92))]
if temps:
    legend.append(("温度", (238, 146, 60)))
legend += [("耳机", (128, 118, 226)), ("键盘 1", (0, 166, 190))]
lx, ly = PLOT[0] - 8, PLOT[1] - 22
for name, col in legend:
    dot(lx + 4, ly + 7, 4, col)
    text(lx + 13, ly - 2, name, (118, 124, 134), 12)
    lx += 13 + text_w(name, 12)[0] + 16

# 网格
for pct in (0, 25, 50, 75, 100):
    y = yof(pct)
    line(PLOT[0], y, PLOT[0] + PLOT[2], y, (208, 213, 222) if pct == 0 else (234, 237, 242))
    label = "%d%%" % pct
    tw, th = text_w(label, 12)
    text(PLOT[0] - 10 - tw, y - th / 2, label, (118, 124, 134), 12)

if temps:
    for i in (0, 1, 2):
        v = tmin + (tmax - tmin) * i / 2.0
        y = PLOT[1] + PLOT[3] - PLOT[3] * i / 2.0
        label = "%d℃" % round(v)
        tw, th = text_w(label, 12)
        text(PLOT[0] + PLOT[2] + 8, y - th / 2, label, (118, 124, 134), 12)

for d in range(days):
    t = t0 + dt.timedelta(days=d)
    x = xof(t)
    line(x, PLOT[1], x, PLOT[1] + PLOT[3], (234, 237, 242))
    if d % 5 == 0 or d == days - 1:
        label = "%d/%d" % (t.month, t.day)
        tw, th = text_w(label, 12)
        text(x - tw / 2, PLOT[1] + PLOT[3] + 8, label, (118, 124, 134), 12)


def segments(series, mapper):
    """与 C# BuildSegments 同样的算法：同列保留上下界，>24h 断开"""
    segs, seg = [], []
    colx, lo, hi = None, 0, 0
    for i, (t, v) in enumerate(series):
        x, y = xof(t), mapper(v)
        if i > 0 and (t - series[i - 1][0]).total_seconds() > 86400:
            if colx is not None:
                seg += [(colx, lo)] if hi - lo < 1.5 else [(colx, lo), (colx, hi)]
            if seg:
                segs.append(seg)
            seg, colx = [], None
        if x != colx:
            if colx is not None:
                seg += [(colx, lo)] if hi - lo < 1.5 else [(colx, lo), (colx, hi)]
            colx, lo, hi = x, y, y
        else:
            lo, hi = min(lo, y), max(hi, y)
    if colx is not None:
        seg += [(colx, lo)] if hi - lo < 1.5 else [(colx, lo), (colx, hi)]
    if seg:
        segs.append(seg)
    return segs


pct_series = [(p[0], p[1]) for p in pts]
for seg in segments(pct_series, yof):
    if len(seg) < 2:
        dot(seg[0][0], seg[0][1], 2.5, (34, 176, 92))
    else:
        polyline(seg, (34, 176, 92), 2)

if temps:
    temp_series = [(p[0], p[2]) for p in pts if p[2] is not None]
    for seg in segments(temp_series, yof_temp):
        if len(seg) < 2:
            dot(seg[0][0], seg[0][1], 2.5, (238, 146, 60))
        else:
            polyline(seg, (238, 146, 60), 2)

# 假造的蓝牙两条，只为看多曲线排版
if pts:
    base = pts[-1][0]
    fake_a = [(base - dt.timedelta(minutes=11 * i), 82 - i * 1.2) for i in range(len(pts))][::-1]
    fake_b = [(base - dt.timedelta(minutes=11 * i), 96 - i * 0.4) for i in range(len(pts) // 2)][::-1]
    for seg in segments(fake_a, yof):
        if len(seg) > 1:
            polyline(seg, (128, 118, 226), 2)
    for seg in segments(fake_b, yof):
        if len(seg) > 1:
            polyline(seg, (0, 166, 190), 2)

# ── 存 BMP ────────────────────────────────────────────────────
buf = C.string_at(bits, W * H * 4)
row = W * 4
# DIB 是 BGRA，BMP 也用 BGRA，直接写，但 alpha 会被当保留位，统一刷成 0
rows = []
for y in range(H):
    r = bytearray(buf[y * row:(y + 1) * row])
    r[3::4] = b"\x00" * W
    rows.append(bytes(r))
pixels = b"".join(rows)

hdr = struct.pack("<2sIHHI", b"BM", 54 + len(pixels), 0, 0, 54)
info = struct.pack("<IiiHHIIiiII", 40, W, -H, 1, 32, 0, len(pixels), 2835, 2835, 0, 0)
open("dev/shots/chart-preview-%dd.bmp" % days, "wb").write(hdr + info + pixels)

# 顺手写一份 PNG（BMP 有些查看器不认，预览面板也更愿意直接显示 png）
import zlib
png_rows = []
for y in range(H):
    r = bytearray(buf[y * row:(y + 1) * row])
    # DIB 是 BGRA，PNG 要 RGBA：交换 R/B
    r[0::4], r[2::4] = r[2::4], r[0::4]
    r[3::4] = b"\xff" * W
    png_rows.append(bytes(r))
raw = b"".join(b"\x00" + r for r in png_rows)
def chunk(tag, data):
    return (struct.pack(">I", len(data)) + tag + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
png = (b"\x89PNG\r\n\x1a\n"
       + chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
       + chunk(b"IDAT", zlib.compress(raw, 6))
       + chunk(b"IEND", b""))
open("dev/shots/chart-preview-%dd.png" % days, "wb").write(png)
print("ok", W, H, "points", len(pts), "temps", len(temps))
