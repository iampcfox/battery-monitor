# -*- coding: utf-8 -*-
# 用真实设备数据跑一遍 C# 里的聚合/分类逻辑（容器聚合 + 通用名反推类型）
import ctypes
from ctypes import wintypes as w

s = ctypes.WinDLL('setupapi.dll')


class GUID(ctypes.Structure):
    _fields_ = [('Data1', w.DWORD), ('Data2', w.WORD), ('Data3', w.WORD),
                ('Data4', ctypes.c_ubyte * 8)]


class SPD(ctypes.Structure):
    _fields_ = [('cbSize', w.DWORD), ('ClassGuid', GUID), ('DevInst', w.DWORD),
                ('Reserved', ctypes.c_void_p)]


class K(ctypes.Structure):
    _fields_ = [('fmtid', GUID), ('pid', w.DWORD)]


def guid(x):
    g = GUID()
    ctypes.windll.ole32.CLSIDFromString(ctypes.c_wchar_p(x), ctypes.byref(g))
    return g


CH = guid('{4340A6C5-93FA-4706-972C-7B648008A5A7}')
BT = guid('{104EA319-6EE2-4701-BD47-8DDBF425BBE5}')
BOX = guid('{8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C}')

s.SetupDiGetClassDevsW.restype = w.HANDLE
s.SetupDiEnumDeviceInfo.argtypes = [w.HANDLE, w.DWORD, ctypes.POINTER(SPD)]
s.SetupDiGetDevicePropertyW.argtypes = [w.HANDLE, ctypes.POINTER(SPD), ctypes.POINTER(K),
                                        ctypes.POINTER(w.DWORD), ctypes.c_void_p, w.DWORD,
                                        ctypes.POINTER(w.DWORD), w.DWORD]
s.SetupDiGetDeviceRegistryPropertyW.argtypes = [w.HANDLE, ctypes.POINTER(SPD), w.DWORD,
                                                ctypes.POINTER(w.DWORD), ctypes.c_void_p,
                                                w.DWORD, ctypes.POINTER(w.DWORD)]
s.SetupDiGetDeviceInstanceIdW.argtypes = [w.HANDLE, ctypes.POINTER(SPD), w.LPWSTR, w.DWORD,
                                          ctypes.POINTER(w.DWORD)]


def prop(h, d, key):
    t = w.DWORD(); n = w.DWORD(); b = ctypes.create_string_buffer(64)
    ok = s.SetupDiGetDevicePropertyW(h, ctypes.byref(d), ctypes.byref(key), ctypes.byref(t),
                                     b, 64, ctypes.byref(n), 0)
    if not ok:
        return None, None
    if t.value == 3: return b.raw[0], t.value
    if t.value == 7: return int.from_bytes(b.raw[:4], 'little'), t.value
    if t.value == 13: return b.raw[:16].hex().upper(), t.value
    return None, t.value


def name_of(h, d):
    t = w.DWORD(); n = w.DWORD(); b = ctypes.create_string_buffer(1024)
    ok = s.SetupDiGetDeviceRegistryPropertyW(h, ctypes.byref(d), 0x0C, ctypes.byref(t),
                                             b, 1024, ctypes.byref(n))
    return b.raw[:n.value].decode('utf-16-le', 'ignore').rstrip('\0') if ok else ''


def iid_of(h, d):
    b = ctypes.create_unicode_buffer(512); n = w.DWORD()
    s.SetupDiGetDeviceInstanceIdW(h, ctypes.byref(d), b, 512, ctypes.byref(n))
    return b.value


nodes = []
for e in ('BTHENUM', 'BTHLE', 'BTHLEDEVICE'):
    h = s.SetupDiGetClassDevsW(None, e, None, 0x02 | 0x04)
    if not h or h == w.HANDLE(-1).value:
        continue
    i = 0
    while True:
        d = SPD(); d.cbSize = ctypes.sizeof(SPD)
        if not s.SetupDiEnumDeviceInfo(h, i, ctypes.byref(d)):
            break
        i += 1
        pct = prop(h, d, K(BT, 2))[0]
        st = prop(h, d, K(CH, 2))[0]
        box = prop(h, d, K(BOX, 2))[0]
        if box == '0000000000000000FFFFFFFFFFFFFFFF':
            box = None
        nodes.append(dict(iid=iid_of(h, d), name=name_of(h, d),
                          pct=pct if isinstance(pct, int) else -1,
                          status=st if isinstance(st, int) else -1,
                          box=box, enum=e))
    s.SetupDiDestroyDeviceInfoList(h)


def hexc(c):
    return c in '0123456789abcdefABCDEF'


def addr_of(iid):
    best = ''
    i = 0
    n = len(iid)
    while i + 12 <= n:
        if i > 0 and hexc(iid[i - 1]):
            i += 1; continue
        if all(hexc(iid[i + k]) for k in range(12)) and (i + 12 >= n or not hexc(iid[i + 12])):
            best = iid[i:i + 12].upper(); i += 12; continue
        i += 1
    return best


aggs, by_box = [], {}
for n in nodes:
    key = n['box'] or n['iid']
    a = by_box.get(key)
    if a is None:
        a = dict(box=key, enum=n['enum'], names=[], model='', pct=-1, online=False, addr='', kind='Unknown')
        by_box[key] = a; aggs.append(a)
    if n['name'].strip():
        a['names'].append(n['name'].strip())
    if not a['addr']:
        a['addr'] = addr_of(n['iid'])
    if a['pct'] < 0 and 0 <= n['pct'] <= 100:
        a['pct'] = n['pct']
    st = n['status']
    if st < 0 or (not (st & 0x400) and (st & 0x8)):
        a['online'] = True
    if not a['model'] and n['name'].strip() and not n['name'].strip().lower().startswith('bluetooth'):
        a['model'] = n['name'].strip()


def classify(txt):
    t = txt.lower()
    if 'keyboard' in t or '键盘' in txt: return 'Keyboard'
    if 'mouse' in t or '鼠标' in txt: return 'Mouse'
    for k in ('controller', 'gamepad', 'game pad', 'joystick', 'dualshock', 'dualsense', 'xbox', '8bitdo'):
        if k in t: return 'Gamepad'
    if '手柄' in txt: return 'Gamepad'
    for k in ('hands-free', 'handsfree', 'avrcp', 'headphone', 'headset', 'buds', 'airpod', 'earbud', 'audio'):
        if k in t: return 'Headphone'
    if '耳机' in txt: return 'Headphone'
    return 'Unknown'


RANK = {'Headphone': 0, 'Keyboard': 1, 'Gamepad': 2, 'Mouse': 3, 'Unknown': 4}
NAME = {'Headphone': '耳机', 'Keyboard': '键盘', 'Gamepad': '手柄', 'Mouse': '鼠标', 'Unknown': '蓝牙设备'}

out, hints, pend = [], {}, {}
for a in aggs:
    if not a['online']:
        continue
    txt = ' '.join(a['names'])
    kind = classify(txt)
    generic = txt.strip().lower().startswith('bluetooth')
    if a['pct'] < 0:
        if kind == 'Unknown':
            continue
        if generic:
            hints.setdefault(a['enum'], []).append(dict(kind=kind, addr=a['addr']))
            continue
        a['kind'] = kind
        out.append(a)
    else:
        if kind == 'Unknown':
            pend.setdefault(a['enum'], []).append(a)
            continue
        a['kind'] = kind
        out.append(a)

for enum, ps in pend.items():
    hs = hints.get(enum, [])
    if len(ps) != len(hs):
        print('!! 线索数与设备数不一致，放弃配对:', enum, len(ps), len(hs))
        continue
    ps.sort(key=lambda x: x['addr'])
    hs.sort(key=lambda x: x['addr'])
    for i, p in enumerate(ps):
        p['kind'] = hs[i]['kind']
        out.append(p)

out.sort(key=lambda a: (RANK[a['kind']], a['addr']))
total, seq = {}, {}
for a in out:
    total[a['kind']] = total.get(a['kind'], 0) + 1
for a in out:
    if total[a['kind']] > 1:
        seq[a['kind']] = seq.get(a['kind'], 0) + 1
        a['label'] = NAME[a['kind']] + str(seq[a['kind']])
    else:
        a['label'] = NAME[a['kind']]

print('=' * 74)
for a in out:
    txt = ('%s %d%%' % (a['label'], a['pct'])) if a['pct'] >= 0 else (a['label'] + ' 已连接')
    print('%-16s %-14s %-22s [%s]' % (txt, a['addr'], a['model'] or '-', a['kind']))
print('=' * 74)
print('卡片行数 =', len(out))
print('被丢弃/仅作线索的容器：')
for a in aggs:
    if a in out:
        continue
    print('   box=%s enum=%-8s pct=%-4s online=%-5s names=%s' %
          (a['box'][:8], a['enum'], a['pct'], a['online'], ' | '.join(a['names']) or '(无名)'))
