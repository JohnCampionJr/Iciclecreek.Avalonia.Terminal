#!/usr/bin/env python3
"""Judge compositor screenshots against buffer dumps.

A cell is judged only when consecutive dumps agree on it, and the screenshot was taken
between them -- the discipline that made the headless detector sound. Verdicts:
  MISSING: both dumps hold a visible glyph, the screenshot's cell box has no ink.
  FOREIGN: both dumps hold blank, the screenshot's cell box has ink.
MISSING+FOREIGN in the same frame at sprite scale = stale/corrupted frame.
"""
import glob, os, re, struct, subprocess, sys, zlib

probe_dir = sys.argv[1] if len(sys.argv) > 1 else "/tmp/probe"

def load_png(path):
    d = open(path, "rb").read(); i = 8; idat = b""; w = h = ct = 0
    while i < len(d):
        ln = struct.unpack(">I", d[i:i+4])[0]; typ = d[i+4:i+8]; data = d[i+8:i+8+ln]; i += 12 + ln
        if typ == b"IHDR": w, h, _, ct = struct.unpack(">IIBB", data[:10])
        elif typ == b"IDAT": idat += data
    raw = zlib.decompress(idat); ch = {0:1,2:3,3:1,4:2,6:4}[ct]; stride = w*ch
    out = bytearray(); prev = bytearray(stride); p = 0
    for y in range(h):
        f = raw[p]; p += 1; line = bytearray(raw[p:p+stride]); p += stride
        for x in range(stride):
            a = line[x-ch] if x >= ch else 0; b = prev[x]; c = prev[x-ch] if x >= ch else 0
            if f == 1: line[x] = (line[x]+a) & 255
            elif f == 2: line[x] = (line[x]+b) & 255
            elif f == 3: line[x] = (line[x]+((a+b)>>1)) & 255
            elif f == 4:
                pp = a+b-c; pa,pb,pc = abs(pp-a),abs(pp-b),abs(pp-c)
                line[x] = (line[x] + (a if pa<=pb and pa<=pc else b if pb<=pc else c)) & 255
        out += line; prev = line
    return w, h, ch, bytes(out)

def parse_dump(path):
    lines = open(path, encoding="utf-8").read().split("\n")
    cw, chh, cols, rows, scale, ox, oy = lines[0].split("|")
    return (float(cw), float(chh), int(cols), int(rows), float(scale), int(ox), int(oy)), lines[1:]

dumps = sorted(glob.glob(os.path.join(probe_dir, "dump-*.txt")))
shots = sorted(glob.glob(os.path.join(probe_dir, "shot-*.png")))
print(f"{len(dumps)} dumps, {len(shots)} screenshots")

def seq(p): return int(re.search(r"(\d+)", os.path.basename(p)).group(1))
dump_by = {seq(p): p for p in dumps}

frames = missing_f = foreign_f = 0
worst = (0, None)
for sp in shots:
    n = seq(sp)
    if n not in dump_by or (n+1) not in dump_by: continue
    meta, a = parse_dump(dump_by[n]); _, b = parse_dump(dump_by[n+1])
    cw, chh, cols, rows, scale, ox, oy = meta
    w, h, ch, px = load_png(sp)
    frames += 1
    missing = foreign = 0
    for r in range(rows):
        ra = a[r] if r < len(a) else ""; rb = b[r] if r < len(b) else ""
        for c in range(cols):
            ca = ra[c] if c < len(ra) else " "; cb = rb[c] if c < len(rb) else " "
            if ca != cb: continue
            # Inset by two device pixels per edge: a neighbouring glyph's antialiased edge
            # bleeds into this cell's box, and without the inset every cell beside text counts
            # as FOREIGN. The glyph itself is far wider than the inset, so MISSING is unaffected.
            inset = 2
            x0 = int(c*cw*scale)+inset; x1 = int((c+1)*cw*scale)-inset
            y0 = int(r*chh*scale)+inset; y1 = int((r+1)*chh*scale)-inset
            if x1 > w or y1 > h: continue
            ink = False
            for y in range(y0, y1):
                row_off = y*w*ch
                for x in range(x0, x1):
                    o = row_off + x*ch
                    if px[o] > 12 or px[o+1] > 12 or px[o+2] > 12: ink = True; break
                if ink: break
            if ca != " " and not ink: missing += 1
            elif ca == " " and ink: foreign += 1
    if missing: missing_f += 1
    if foreign: foreign_f += 1
    if missing + foreign > worst[0]: worst = (missing+foreign, (sp, missing, foreign))
    if missing or foreign:
        print(f"  frame {n}: {missing} MISSING, {foreign} FOREIGN")

print(f"\njudged {frames} frames: {missing_f} with missing glyphs, {foreign_f} with foreign ink")
if worst[1]: print(f"worst: {worst[1]}")
