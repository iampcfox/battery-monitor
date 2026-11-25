# -*- coding: utf-8 -*-
# 全部蓝牙节点：枚举器 / 名称 / 容器ID / 电量 / 状态 —— 查"键盘为什么不显示"
import ctypes
from ctypes import wintypes as w

setupapi = ctypes.WinDLL('setupapi.dll')

DIGCF_PRESENT = 0x02
DIGCF_ALLCLASSES = 0x04
SPDRP_FRIENDLYNAME = 0x0C


class GUID(ctypes.Structure):
    _fields_ = [('Data1', w.DWORD), ('Data2', w.WORD), ('Data3', w.WORD),
                ('Data4', ctypes.c_ubyte * 8)]


class SP_DEVINFO_DATA(ctypes.Structure):
    _fields_ = [('cbSize', w.DWORD), ('ClassGuid', GUID), ('DevInst', w.DWORD),
                ('Reserved', ctypes.c_void_p)]


class DEVPROPKEY(ctypes.Structure):
    _fields_ = [('fmtid', GUID), ('pid', w.DWORD)]


def guid(s):
    g = GUID()
    ctypes.windll.ole32.CLSIDFromString(ctypes.c_wchar_p(s), ctypes.byref(g))
    return g


BATT = guid('{104EA319-6EE2-4701-BD47-8DDBF425BBE5}')
DEVSTAT = guid('{4340A6C5-93FA-4706-972C-7B648008A5A7}')
CONTAINER = guid('{8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}')

setupapi.SetupDiGetClassDevsW.restype = w.HANDLE
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.c_void_p, w.LPCWSTR, w.HWND, w.DWORD]
setupapi.SetupDiEnumDeviceInfo.argtypes = [w.HANDLE, w.DWORD, ctypes.POINTER(SP_DEVINFO_DATA)]
setupapi.SetupDiGetDeviceInstanceIdW.argtypes = [w.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA),
                                                w.LPWSTR, w.DWORD, ctypes.POINTER(w.DWORD)]
setupapi.SetupDiGetDevicePropertyW.argtypes = [w.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA),
                                               ctypes.POINTER(DEVPROPKEY),
                                               ctypes.POINTER(w.DWORD), ctypes.c_void_p,
                                               w.DWORD, ctypes.POINTER(w.DWORD), w.DWORD]
setupapi.SetupDiGetDeviceRegistryPropertyW.argtypes = [w.HANDLE, ctypes.POINTER(SP_DEVINFO_DATA),
                                                       w.DWORD, ctypes.POINTER(w.DWORD),
                                                       ctypes.c_void_p, w.DWORD,
                                                       ctypes.POINTER(w.DWORD)]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [w.HANDLE]


def prop(h, d, key):
    t = w.DWORD()
    need = w.DWORD()
    buf = ctypes.create_string_buffer(4096)
    ok = setupapi.SetupDiGetDevicePropertyW(h, ctypes.byref(d), ctypes.byref(key),
                                            ctypes.byref(t), buf, 4096,
                                            ctypes.byref(need), 0)
    if not ok:
        return None, None
    if t.value == 3:
        return buf.raw[0], t.value
    if t.value == 5:
        return int.from_bytes(buf.raw[:2], 'little'), t.value
    if t.value == 7:
        return int.from_bytes(buf.raw[:4], 'little'), t.value
    if t.value == 0x2012:
        return buf.raw[:need.value].decode('utf-16-le', 'ignore'), t.value
    if t.value == 0x0D:  # GUID
        g = GUID.from_buffer_copy(buf.raw[:16])
        tail = ''.join('%02X' % b for b in g.Data4)
        return '%08X-%04X-%04X-%s' % (g.Data1, g.Data2, g.Data3, tail), t.value
    return None, t.value


def name_of(h, d):
    t = w.DWORD()
    need = w.DWORD()
    buf = ctypes.create_string_buffer(1024)
    ok = setupapi.SetupDiGetDeviceRegistryPropertyW(h, ctypes.byref(d), SPDRP_FRIENDLYNAME,
                                                    ctypes.byref(t), buf, 1024,
                                                    ctypes.byref(need))
    if not ok:
        return ''
    return buf.raw[:need.value].decode('utf-16-le', 'ignore').rstrip('\0')


rows = []
for enumr in ('BTHENUM', 'BTHLE', 'BTHLEDEVICE'):
    h = setupapi.SetupDiGetClassDevsW(None, enumr, None, DIGCF_PRESENT | DIGCF_ALLCLASSES)
    if not h or h == w.HANDLE(-1).value:
        continue
    i = 0
    while True:
        d = SP_DEVINFO_DATA()
        d.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
        if not setupapi.SetupDiEnumDeviceInfo(h, i, ctypes.byref(d)):
            break
        i += 1
        sb = ctypes.create_unicode_buffer(512)
        n = w.DWORD()
        setupapi.SetupDiGetDeviceInstanceIdW(h, ctypes.byref(d), sb, 512, ctypes.byref(n))
        iid = sb.value
        nm = name_of(h, d)
        pct = prop(h, d, DEVPROPKEY(BATT, 2))[0]
        st = prop(h, d, DEVPROPKEY(DEVSTAT, 2))[0]
        cid = prop(h, d, DEVPROPKEY(CONTAINER, 2))[0]
        rows.append((enumr, iid, nm, pct, st, cid))
    setupapi.SetupDiDestroyDeviceInfoList(h)

print('total nodes: %d' % len(rows))
for enumr, iid, nm, pct, st, cid in rows:
    print('-' * 78)
    print('%-11s %s' % ('[' + enumr + ']', nm))
    print('   iid  : %s' % iid)
    print('   pct  : %s   status: %s' % (pct, '0x%08X' % st if st is not None else 'None'))
    print('   cont : %s' % cid)

print()
print('=== 按容器分组 ===')
by = {}
for enumr, iid, nm, pct, st, cid in rows:
    key = cid if cid else '<none>'
    by.setdefault(key, []).append((enumr, nm, pct))
for k, v in by.items():
    print('容器 %s  (%d 节点)' % (k, len(v)))
    for enumr, nm, pct in v:
        print('    [%s] %-45s pct=%s' % (enumr, nm[:45], pct))
