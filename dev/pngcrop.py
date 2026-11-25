import sys, zlib, struct

def read_png(path):
    data = open(path,'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    i = 8; idat = b''; w=h=bd=ct=None; plte=None; trns=None
    while i < len(data):
        ln = struct.unpack('>I', data[i:i+4])[0]
        typ = data[i+4:i+8]
        chunk = data[i+8:i+8+ln]
        if typ == b'IHDR':
            w,h,bd,ct,comp,filt,inter = struct.unpack('>IIBBBBB', chunk)
        elif typ == b'IDAT':
            idat += chunk
        elif typ == b'PLTE':
            plte = chunk
        elif typ == b'tRNS':
            trns = chunk
        elif typ == b'IEND':
            break
        i += 12 + ln
    raw = zlib.decompress(idat)
    nch = {0:1, 2:3, 3:1, 4:2, 6:4}[ct]
    assert bd == 8 and inter == 0, (bd, ct, inter)
    stride = w*nch
    out = bytearray(h*stride)
    pos = 0
    prev = bytearray(stride)
    for y in range(h):
        f = raw[pos]; pos += 1
        line = bytearray(raw[pos:pos+stride]); pos += stride
        if f == 1:
            for x in range(nch, stride): line[x] = (line[x] + line[x-nch]) & 255
        elif f == 2:
            for x in range(stride): line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x-nch] if x >= nch else 0
                line[x] = (line[x] + ((a + prev[x])>>1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x-nch] if x >= nch else 0
                b = prev[x]
                c = prev[x-nch] if x >= nch else 0
                p = a+b-c
                pa, pb, pc = abs(p-a), abs(p-b), abs(p-c)
                pr = a if (pa<=pb and pa<=pc) else (b if pb<=pc else c)
                line[x] = (line[x] + pr) & 255
        out[y*stride:(y+1)*stride] = line
        prev = line
    # to RGB
    rgb = bytearray(w*h*3)
    if ct == 2:
        rgb[:] = out
    elif ct == 6:
        for k in range(w*h):
            rgb[k*3:k*3+3] = out[k*4:k*4+3]
    elif ct == 3:
        for k in range(w*h):
            idx = out[k]
            rgb[k*3:k*3+3] = plte[idx*3:idx*3+3]
    elif ct == 0:
        for k in range(w*h):
            v = out[k]; rgb[k*3:k*3+3] = bytes([v,v,v])
    return w,h,rgb

def write_png(path, w, h, rgb):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rgb[y*w*3:(y+1)*w*3]
    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t+d) & 0xffffffff)
    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9))
    png += chunk(b'IEND', b'')
    open(path,'wb').write(png)

def crop_scale(src, x, y, w, h, scale, out):
    W,H,rgb = read_png(src)
    cw, ch = min(w, W-x), min(h, H-y)
    big = bytearray(cw*scale*ch*scale*3)
    for yy in range(ch*scale):
        sy = y + yy//scale
        row = bytearray()
        for xx in range(cw*scale):
            sx = x + xx//scale
            o = (sy*W + sx)*3
            row += rgb[o:o+3]
        big[yy*cw*scale*3:(yy+1)*cw*scale*3] = row
    write_png(out, cw*scale, ch*scale, big)
    print("saved", out, cw*scale, ch*scale)

if __name__ == '__main__':
    a = sys.argv
    crop_scale(a[1], int(a[2]), int(a[3]), int(a[4]), int(a[5]), int(a[6]), a[7])
