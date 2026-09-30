# Reads an in-game frame-stats .bin written by OSU_FRAME_STATS=1 and prints the frame-time stats, an attribution table for
# slow frames, a 64 Hz check, the per-second GC numbers and (v3) the input-delay stats. Reads the v3 and v2 formats (see README "Measuring") and the old
# headerless v1 files (float32 little-endian ms gaps only; only the whole-session stats are available then).
# Optionally lines the game's numbers up against a CapFrameX capture JSON.
# Usage: python tools/framestats_analyse.py "<file.bin>" ["<CapFrameX-osu!.exe-....json>"]
# Needs numpy.
import json, struct, sys
import numpy as np

MAGIC = b'OSUFRMST'
TYPES = {'f32': '<f4', 'f64': '<f8', 'u64': '<u8', 'u32': '<u4', 'u8': 'u1'}


def load_bin(path):
    raw = open(path, 'rb').read()
    if raw[:8] != MAGIC:
        return {'version': 1, 'frame': {'gap_ms': np.frombuffer(raw[:len(raw) // 4 * 4], '<f4').astype(np.float64)},
                'update': {}, 'second': {}, 'first_frame': -1, 'first_update': -1, 'offset_ms': 0.0, 'flags': 0}
    (version, hsize, n, u, s, first_frame, first_update, flags) = struct.unpack_from('<IIIIIiiI', raw, 8)
    (offset_ms,) = struct.unpack_from('<d', raw, 40)
    n_in, in_flags, first_obj_ms = (0, 0, -1.0)
    if version >= 3:
        n_in, in_flags = struct.unpack_from('<II', raw, 48)
        (first_obj_ms,) = struct.unpack_from('<d', raw, 56)
    (ll,) = struct.unpack_from('<I', raw, 64)
    fields = raw[68:68 + ll].decode('utf-8')
    counts = {'frame': n, 'update': u, 'second': s, 'input': n_in}
    pos = hsize
    out = {'version': version, 'first_frame': first_frame, 'first_update': first_update, 'offset_ms': offset_ms,
           'flags': flags, 'input_flags': in_flags, 'first_object_ms': first_obj_ms, 'frame': {}, 'update': {}, 'second': {}, 'input': {}}
    for section in fields.split(';'):
        name, cols = section.rstrip(']').split('[')
        cnt = counts[name]
        for col in cols.split(','):
            cname, ctype = col.split(':')
            dt = np.dtype(TYPES[ctype])
            out[name][cname] = np.frombuffer(raw, dt, cnt, pos).astype(np.float64 if ctype != 'u8' else np.uint8)
            pos += cnt * dt.itemsize
    return out


def load_cfx(path):
    d = json.load(open(path, encoding='utf-8-sig'))
    return np.array(d['Runs'][0]['CaptureData']['MsBetweenPresents'], dtype=np.float64)


def stats(ft):
    good = ft[ft > 0]
    out = {'frames': len(good), 'span s': round(float(good.sum()) / 1000, 1)}
    if not len(good):
        return out
    out['avg fps'] = round(len(good) / (float(good.sum()) / 1000), 1)
    s = np.sort(good)[::-1]
    for p in (0.01, 0.001):
        out[f'{p*100:g}% low fps'] = round(1000 / float(s[:max(1, int(len(s) * p))].mean()), 1)
    out['max ms'] = round(float(s[0]), 2)
    for th in (1, 2, 5, 20):
        out[f'frames > {th} ms'] = int((good > th).sum())
    return out


def show(title, d):
    print(title)
    for k, v in d.items():
        print(f'{k:>16}: {v}')
    print()


BANDS = [(0.3, 0.4), (0.4, 0.5), (0.5, 0.7), (0.7, 1), (1, 2), (2, float('inf'))]


def attribution(data, lo):
    f = data['frame']
    gap = f['gap_ms'][lo:]
    cols = {k: f[k][lo:] for k in ('ready_wait_ms', 'update_wait_ms', 'draw_ms', 'swap_ms')}
    other = gap - sum(cols.values())
    gc = f['gc0'][lo:].astype(bool)
    upd_len = update_around(data, lo)

    def row(label, m):
        c = int(m.sum())
        if not c:
            print(f'{label:>10} {0:>9}')
            return
        ul = upd_len[m]
        ul = ul[~np.isnan(ul)]
        print(f'{label:>10} {c:>9} {100*c/len(gap):>6.2f}% {gap[m].mean():>8.3f} {cols["ready_wait_ms"][m].mean():>8.3f} '
              f'{cols["update_wait_ms"][m].mean():>8.3f} {cols["draw_ms"][m].mean():>8.3f} {cols["swap_ms"][m].mean():>8.3f} '
              f'{other[m].mean():>8.3f} {100*gc[m].mean():>6.2f}% {(ul.mean() if len(ul) else float("nan")):>9.3f}')

    print(f'{"band ms":>10} {"frames":>9} {"share":>7} {"gap":>8} {"ready":>8} {"upd-wait":>8} {"draw":>8} {"swap":>8} {"other":>8} {"gen0 GC":>7} {"upd frame":>9}')
    row('typical<.3', gap < 0.3)
    for a, b in BANDS:
        label = f'{a:g}-{b:g}' if b != float('inf') else f'>{a:g}'
        row(label, (gap >= a) & (gap < b))
    print('(means in ms; "upd frame" = length of the update frame that was running when the frame was presented)')
    print()


def update_around(data, lo):
    """Length of the update-thread frame running at each draw frame's present time (NaN where unknown)."""
    gap = data['frame']['gap_ms']
    upd = data['update'].get('frame_ms')
    out = np.full(len(gap), np.nan)
    if upd is None or not len(upd):
        return out[lo:]
    pt = np.cumsum(gap)
    starts = data['offset_ms'] + np.concatenate(([0.0], np.cumsum(upd)))
    j = np.searchsorted(starts, pt, side='right') - 1
    ok = (j >= 0) & (j < len(upd))
    out[ok] = upd[j[ok]]
    return out[lo:]


def check64(data, lo):
    gap = data['frame']['gap_ms']
    pt = np.cumsum(gap)[lo:]
    g = gap[lo:]
    slow = pt[g > 0.3]
    if len(slow) < 100:
        print('too few slow frames for a rhythm check\n')
        return
    t0 = pt[0]
    dt = 0.5  # ms per bin
    nb = int((pt[-1] - t0) / dt) + 1
    series = np.bincount(((slow - t0) / dt).astype(int), minlength=nb).astype(np.float64)
    series -= series.mean()
    power = np.abs(np.fft.rfft(series)) ** 2
    freq = np.fft.rfftfreq(nb, dt / 1000)
    sel = (freq >= 5) & (freq <= 500)
    k = np.argmax(power[sel])
    fpk = freq[sel][k]
    ratio = power[sel][k] / np.median(power[sel])
    i64 = np.argmin(np.abs(freq - 64))
    near = power[max(0, i64 - 2):i64 + 3].max() / np.median(power[sel])
    print(f'FFT of the "frame > 0.3 ms" series (0.5 ms bins, 5-500 Hz): peak {fpk:.2f} Hz, {ratio:.0f}x the median power')
    print(f'  power within 2 bins of 64 Hz: {near:.0f}x the median power')
    period = 15.625
    nph = 16
    ph = ((slow - t0) % period) / period
    h = np.bincount((ph * nph).astype(int), minlength=nph)[:nph]
    print(f'phase-folded counts at {period} ms ({nph} bins, mean {h.mean():.1f}): {h.tolist()}')
    print(f'  max/min bin {h.max() / max(1, h.min()):.2f}, max/mean {h.max() / h.mean():.2f} (flat = about 1.0)\n')


def seconds(data):
    s = data['second']
    if not s or not len(s.get('elapsed_s', [])):
        print('no per-second counters in this file\n')
        return
    dur = np.diff(np.concatenate(([0.0], s['elapsed_s'])))
    mb = s['alloc_bytes'] / 1048576
    rate = mb / np.maximum(dur, 1e-9)
    print(f'per second: {len(dur)} samples over {dur.sum():.1f} s')
    print(f'  allocated: mean {mb.sum() / dur.sum():.2f} MB/s, max {rate.max():.2f} MB/s, total {mb.sum():.1f} MB')
    print(f'  GC pause: total {s["gc_pause_ms"].sum():.2f} ms, max in one second {s["gc_pause_ms"].max():.2f} ms, '
          f'seconds with any pause {(s["gc_pause_ms"] > 0).sum()}')
    print(f'  GCs: gen0 {int(s["gen0"].sum())}, gen1 {int(s["gen1"].sum())}, gen2 {int(s["gen2"].sum())} '
          f'(gen0 counts every GC, gen1 counts gen1+gen2)')
    worst = np.argsort(s['gc_pause_ms'])[::-1][:3]
    for i in worst:
        if s['gc_pause_ms'][i] > 0:
            print(f'  worst: at {s["elapsed_s"][i]:.0f} s: pause {s["gc_pause_ms"][i]:.2f} ms, {rate[i]:.2f} MB/s, gen0 +{int(s["gen0"][i])}')
    print()


def _pct(v):
    if not len(v):
        return 'n/a'
    return f'mean {v.mean():.3f} p50 {np.percentile(v, 50):.3f} p99 {np.percentile(v, 99):.3f} max {v.max():.3f}'


def input_delay(data):
    """Input delay per input (v3): os = SDL event timestamp, pump = input thread, update = consumed by the update thread,
    present = end of the first swap that drew that update frame. NaN values (not available) are left out of each metric."""
    i = data['input']
    if not i or not len(i.get('kind', [])):
        print('no input-delay records in this file\n')
        return
    if data['input_flags'] & 1:
        print('WARNING: an input buffer filled up, later inputs are missing')
    if data['input_flags'] & 2:
        print('WARNING: some inputs were lost before the update thread consumed them')
    os_p, p_u, u_p = i['os_to_pump_ms'], i['pump_to_update_ms'], i['update_to_present_ms']
    kind = i['kind']
    t_upd = i['pump_ms'] + p_u
    first = data['first_object_ms']
    print('input delay (ms; keys use Windows message times for the os stamp, so os->pump is coarse for keys, see README)')
    for title, sel_time in (('from first object', first >= 0 and t_upd >= first), ('whole session', np.ones(len(kind), bool))):
        if title == 'from first object' and first < 0:
            print('  first object was never marked: no "from first object" input stats')
            continue
        print(f'  {title}')
        for name, m in (('keys+buttons', kind != 4), ('mouse move', kind == 4)):
            m = m & sel_time
            print(f'    {name}: n={int(m.sum())}')
            cols = (('os->pump', os_p[m]), ('pump->update', p_u[m]), ('os->update', (os_p + p_u)[m]),
                    ('os->present', (os_p + p_u + u_p)[m]), ('update->present', u_p[m]))
            for label, v in cols:
                v = v[~np.isnan(v)]
                print(f'      {label:>16}: {_pct(v)}')
    print()


def what_if(data, lo):
    """Replace every frame flagged gen0 GC by the median frame time, then recompute the lows."""
    f = data['frame']
    if 'gc0' not in f:
        return
    gap = f['gap_ms'][lo:]
    gc = f['gc0'][lo:].astype(bool)
    if not gc.any() or gc.all():
        print('what-if GC frames were typical: no GC frames (or all frames are GC frames), nothing to replace')
        return
    med = float(np.median(gap[~gc]))
    fixed = np.where(gc, med, gap)
    before, after = stats(gap), stats(fixed)
    print(f'what-if GC frames were typical: {int(gc.sum())} frames with a GC ({100 * gc.mean():.2f}%) replaced by the median non-GC frame ({med:.3f} ms)')
    for k in ('avg fps', '1% low fps', '0.1% low fps', 'max ms'):
        print(f'  {k:>14}: {before[k]}  ->  {after[k]}')
    print()


def gc_text(path):
    """Prints the GC reason and gen0-size lines the game wrote into the .txt next to the .bin (newer files only)."""
    import os
    txt = os.path.splitext(path)[0] + '.txt'
    if not os.path.exists(txt):
        return
    lines = [l.rstrip() for l in open(txt, encoding='utf-8', errors='replace')]
    found = [l for l in lines if l.startswith('[framestats] gc reasons') or l.startswith('[framestats] gen0 size')]
    if found:
        print('GC reasons and gen0 size (from the .txt)')
        for l in found:
            print('  ' + l.replace('[framestats] ', ''))
        print()
    else:
        print('no GC reason lines in the .txt (file from before the reason recorder)')


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    data = load_bin(sys.argv[1])
    gap = data['frame']['gap_ms']
    print(f'format v{data["version"]}, {len(gap)} frames' + (f', {len(data["update"].get("frame_ms", []))} update frames' if data['version'] > 1 else ''))
    if data['flags'] & 1:
        print('WARNING: the draw buffer filled up, recording stopped early')
    if data['flags'] & 2:
        print('WARNING: the update buffer filled up, update frames after that are missing')
    print()

    game = stats(gap)
    show('whole session', game)

    ff = data['first_frame']
    lo = 0
    if data['version'] > 1:
        if ff >= 0:
            lo = ff
            show('from first object (the first hit object becoming visible: StartTime - TimePreempt for osu!/catch, StartTime for other rulesets; '
                 'files recorded before 2026-09-30 marked the first hit time instead)', stats(gap[ff:]))
        else:
            print('first object was never marked: the tables below use the whole session\n')

        upd = data['update'].get('frame_ms')
        if upd is not None and len(upd):
            uf = data['first_update']
            u = upd[uf:] if ff >= 0 and uf >= 0 else upd
            print(f'update thread{" (from first object)" if ff >= 0 and uf >= 0 else ""}: {len(u)} frames, mean {u.mean():.3f} ms, '
                  f'max {u.max():.2f} ms, >1 ms {(u > 1).sum()}, >5 ms {(u > 5).sum()}\n')

        print(f'attribution of slow frames ({"from first object" if lo else "whole session"})')
        attribution(data, lo)
        print(f'64 Hz check ({"from first object" if lo else "whole session"})')
        check64(data, lo)
        what_if(data, lo)
        seconds(data)
        if data['version'] >= 3:
            input_delay(data)
        gc_text(sys.argv[1])
    else:
        print('v1 file: no breakdown, first-object marker or GC data\n')

    if len(sys.argv) >= 3:
        cfx = stats(load_cfx(sys.argv[2]))
        print(f'{"":>16}  {"game":>12}  {"CapFrameX":>12}')
        for k in game:
            print(f'{k:>16}  {game[k]:>12}  {cfx.get(k, "-"):>12}')


main()
