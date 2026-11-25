# -*- coding: utf-8 -*-
# 诊断：任务栏窄条到底能不能被点中。
# 纯 ctypes 调 user32，不编译任何东西。
import ctypes, ctypes.wintypes as wt, subprocess, sys

u32 = ctypes.WinDLL('user32', use_last_error=True)
u32.GetWindowLongW.argtypes = [wt.HWND, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long

GWL_STYLE, GWL_EXSTYLE = -16, -20
WS_CHILD = 0x40000000
WS_VISIBLE = 0x10000000
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
WS_EX_NOACTIVATE = 0x08000000


class RECT(ctypes.Structure):
    _fields_ = [('left', ctypes.c_long), ('top', ctypes.c_long),
                ('right', ctypes.c_long), ('bottom', ctypes.c_long)]


class POINT(ctypes.Structure):
    _fields_ = [('x', ctypes.c_long), ('y', ctypes.c_long)]


EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

u32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(RECT)]
u32.WindowFromPoint.argtypes = [POINT]
u32.WindowFromPoint.restype = wt.HWND
u32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
u32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]


def pid_of(hwnd):
    p = wt.DWORD(0)
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
    return p.value


def cls(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def text(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(hwnd, buf, 256)
    return buf.value


def rect(hwnd):
    r = RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return r


def target_pids():
    out = subprocess.run(['tasklist', '/fi', 'imagename eq BatteryMonitor.exe', '/fo', 'csv', '/nh'],
                         capture_output=True, text=True, encoding='gbk', errors='replace')
    pids = []
    for line in out.stdout.splitlines():
        line = line.strip().strip('"')
        if not line:
            continue
        parts = line.split('","')
        if len(parts) > 1:
            try:
                pids.append(int(parts[1]))
            except ValueError:
                pass
    return pids


pids = target_pids()
print('BatteryMonitor.exe pids =', pids)
if not pids:
    sys.exit(0)

found = []


def walk(hwnd, depth=0):
    p = pid_of(hwnd)
    if p in pids:
        r = rect(hwnd)
        st = u32.GetWindowLongW(hwnd, GWL_STYLE)
        ex = u32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        found.append((hwnd, depth, cls(hwnd), text(hwnd), r, st, ex, p))


def enum_child_cb(hwnd, lparam):
    walk(hwnd)
    return True


def enum_top_cb(hwnd, lparam):
    walk(hwnd)
    u32.EnumChildWindows(hwnd, EnumProc(enum_child_cb), 0)
    return True


u32.EnumWindows(EnumProc(enum_top_cb), 0)

print('%-10s %-6s %-34s %-22s %s' % ('hwnd', 'depth', 'class', 'text', 'rect / style'))
for hwnd, depth, c, t, r, st, ex, p in found:
    flags = []
    if st & WS_CHILD:
        flags.append('CHILD')
    if st & WS_VISIBLE:
        flags.append('VIS')
    exflags = []
    if ex & WS_EX_LAYERED:
        exflags.append('LAYERED')
    if ex & WS_EX_TRANSPARENT:
        exflags.append('TRANSPARENT')
    if ex & WS_EX_NOACTIVATE:
        exflags.append('NOACT')
    print('0x%08X %-6d %-34s %-22s (%d,%d)-(%d,%d) %s %s'
          % (hwnd, depth, c, t, r.left, r.top, r.right, r.bottom, ' '.join(flags), ' '.join(exflags)))

    # 在这块矩形里取几个点，看 WindowFromPoint 实际返回谁
    cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    for name, pt in (('center', (cx, cy)), ('mid-left', (r.left + 4, cy)), ('mid-right', (r.right - 4, cy))):
        hit = u32.WindowFromPoint(POINT(pt[0], pt[1]))
        if hit:
            print('    hit@%-9s (%d,%d) -> 0x%08X pid=%d %s %s'
                  % (name, pt[0], pt[1], hit, pid_of(hit), cls(hit), text(hit)))
        else:
            print('    hit@%-9s (%d,%d) -> none' % (name, pt[0], pt[1]))
