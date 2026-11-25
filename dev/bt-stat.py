# -*- coding: utf-8 -*-
# 枚举蓝牙设备的电量 + DevNodeStatus，确认"已连接/已断开"该怎么判
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
CHILD = guid('{4340A6C5-93FA-4706-972C-7B648008A5A7}')

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

DN_ROOT_ENUMERATED = 0x01
DN_DRIVER_LOADED = 0x02
DN_ENUM_LOADED = 0x04
DN_STARTED = 0x08
DN_MANUAL = 0x10
DN_NEED_TO_ENUM = 0x20
DN_DISABLEABLE = 0x200
DN_HAS_PROBLEM = 0x400
DN_PRIVATE_PROBLEM = 0x8000
DN_PHANTOM = 0x800


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


def enum(h):
    for i in range(400):
        d = SP_DEVINFO_DATA()
        d.cbSize = ctypes.sizeof(SP_DEVINFO_DATA)
        if not setupapi.SetupDiEnumDeviceInfo(h, i, ctypes.byref(d)):
            break
        yield d


def status_of(h, d):
    """DEVPKEY_Device_DevNodeStatus = {4340A6C5-93FA-4706-972C-7B648008A5A7}, 2"""
    k = DEVPROPKEY(CHILD, 2)
    v, t = prop(h, d, k)
    return v


rows = []
for enumr in ('BTHENUM', 'BTHLE', 'BTHLEDEVICE'):
    h = setupapi.SetupDiGetClassDevsW(None, enumr, None, DIGCF_PRESENT | DIGCF_ALLCLASSES)
    if not h or h == w.HANDLE(-1).value:
        print('no handle', enumr)
        continue
    for d in enum(h):
        sb = ctypes.create_unicode_buffer(512)
        n = w.DWORD()
        setupapi.SetupDiGetDeviceInstanceIdW(h, ctypes.byref(d), sb, 512, ctypes.byref(n))
        iid = sb.value
        nm = name_of(h, d)
        pct, ptype = prop(h, d, DEVPROPKEY(BATT, 2))
        st = status_of(h, d)
        kids = prop(h, d, DEVPROPKEY(CHILD, 10))[0]
        if pct is not None or (st is not None and st & DN_HAS_PROBLEM):
            rows.append((enumr, iid, nm, pct, ptype, st, kids))
    setupapi.SetupDiDestroyDeviceInfoList(h)


def flagstr(st):
    if st is None:
        return 'None'
    out = []
    for nm, bit in (('ROOT', DN_ROOT_ENUMERATED), ('DRV', DN_DRIVER_LOADED),
                    ('ENUM', DN_ENUM_LOADED), ('STARTED', DN_STARTED),
                    ('MANUAL', DN_MANUAL), ('DISABLEABLE', DN_DISABLEABLE),
                    ('PROBLEM', DN_HAS_PROBLEM), ('PRIVPROB', DN_PRIVATE_PROBLEM),
                    ('PHANTOM', DN_PHANTOM)):
        if st & bit:
            out.append(nm)
    return '0x%04X:%s' % (st, ','.join(out) if out else '-')


for r in rows:
    enumr, iid, nm, pct, ptype, st, kids = r
    print('=' * 70)
    print('[%s] %s' % (enumr, nm))
    print('   iid   : %s' % iid)
    print('   pct   : %s (type %s)' % (pct, ptype))
    print('   status: %s' % flagstr(st))
    print('   kids  : %s' % (kids if kids else '-'))
