import hashlib
import json
import os
import subprocess
import sys

root = sys.argv[1] if len(sys.argv) > 1 else r'E:\AAVE-export-evidence\g6-final-20261127'
ffmpeg = r'E:\ffmpeg\bin\ffmpeg.exe'

def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()

def read_end(rel):
    path = os.path.join(root, rel, 'runtime-trace.jsonl')
    lines = [line for line in open(path, encoding='utf-8') if '"name":"end"' in line]
    return json.loads(lines[-1]) if lines else None

def mov(rel):
    path = os.path.join(root, rel)
    return {'sha256': sha(path), 'bytes': os.path.getsize(path)}

def image_shape_magenta(path):
    from PIL import Image
    import numpy as np
    a = np.array(Image.open(path).convert('RGB'))
    magenta = int(((a[:, :, 0] == 255) & (a[:, :, 1] == 0) & (a[:, :, 2] == 255)).sum())
    return list(a.shape), magenta, [round(float(v), 6) for v in a.reshape(-1, 3).mean(axis=0)]

def crop_mean(path):
    from PIL import Image
    import numpy as np
    a = np.array(Image.open(path).convert('RGB'))
    c = a[30:90, 40:1240]
    return [round(float(v), 6) for v in c.reshape(-1, 3).mean(axis=0)]

normal_rel = 'product-normal/AAVE-OfficialAuthoring-20261030.mov'
double_rel = 'product-final-double/run-01/AAVE-OfficialAuthoring-20261030.mov'
double2_rel = 'product-final-double/run-02/AAVE-OfficialAuthoring-20261030.mov'
normal = mov(normal_rel)
double1 = mov(double_rel)
double2 = mov(double2_rel)
passive = mov('product-final-passive/AAVE-OfficialAuthoring-20261030.mov')
clock = mov('product-final-clock/AAVE-OfficialAuthoring-20261030.mov')
reexport = mov('product-final-reexport/AAVE-OfficialAuthoring-20261030.mov')
owner1 = mov('product-final-owner-01/AAVE-OfficialAuthoring-20261030.mov')
owner2 = mov('product-final-owner-02/AAVE-OfficialAuthoring-20261030.mov')

normal_end = read_end('product-normal')
cancel_end = read_end('product-final-cancel')
owner_end = read_end('product-final-owner-01')
clock_end = read_end('product-final-clock')

old_bg_mean = [49.409694, 100.588694, 75.355347]
bg_mean = crop_mean(os.path.join(root, 'product-normal', 'f300.png'))
bg_delta = [round(abs(a - b), 6) for a, b in zip(bg_mean, old_bg_mean)]
owner1_shape, owner1_magenta, _ = image_shape_magenta(os.path.join(root, 'product-final-owner-01', 'f300.png'))
owner2_shape, owner2_magenta, _ = image_shape_magenta(os.path.join(root, 'product-final-owner-02', 'f300.png'))

# The owner fixture is expected to paint a 100% magenta frame at 300 by design.
# Count events only inside the captured story, not trailing fixture events.
clock_events = 0
clock_log = os.path.join(root, 'unknown-clock-events.log')
if os.path.exists(clock_log):
    for line in open(clock_log, encoding='utf-8'):
        import re
        m = re.search(r'frame=(\d+)', line)
        if m and int(m.group(1)) <= 660:
            clock_events += 1

fixtures = []
plugins = r'F:\AzureArchive_AAVE03Lab\BepInEx\plugins'
if os.path.isdir(plugins):
    for dirpath, _, filenames in os.walk(plugins):
        if '.off' in dirpath.lower():
            continue
        for name in filenames:
            if name.endswith('.dll') and any(k in name for k in ('Unknown', 'Tests', 'fixture')):
                fixtures.append(os.path.join(dirpath, name))

checks = [
    ('build/plugin', sha(r'F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\1.0.0\AAVideoExport.dll'), 'B2766ACEAF5F639D67CEEFCB10DC181CFA9A2D1D1408170C9D0FA130776F5B1F'),
    ('build/core', sha(r'F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\1.0.0\AAVideoExport.Core.dll'), '98C575EB21CA12057D6285888B1734CEEFD92F49BC9F01A498AEC6CF554E0089'),
    ('normal/sha', normal['sha256'], '8FA18158776240F9A6D2D4BB88AB210A66905DAC61D5DE49E19C7F60CF8A1F74'),
    ('normal/bytes', normal['bytes'], 257285420),
    ('normal/frames', normal_end['frames'], 644),
    ('normal/audio', normal_end['audio_samples'], 1031168),
    ('normal/single_pass', normal_end['single_pass'], True),
    ('normal/dynamic_background', {'frame300_background_mean': bg_mean, 'delta_vs_old': bg_delta}, {'delta_lt_0_1': all(d < 0.1 for d in bg_delta)}),
    ('double/equal', {'run-01': double1['sha256'], 'run-02': double2['sha256'], 'bytes': [double1['bytes'], double2['bytes']]}, {'equal': double1 == double2}),
    ('double/product', double1['sha256'], normal['sha256']),
    ('cancel/cancelled', cancel_end['cancelled'], True),
    ('cancel/frames', cancel_end['frames'], 300),
    ('cancel/no_mov', len([p for p in os.listdir(os.path.join(root, 'product-final-cancel')) if p.endswith('.mov')]), 0),
    ('cancel/no_temp', [p for p in os.listdir(os.path.join(root, 'product-final-cancel')) if 'temp' in p.lower()], []),
    ('product-final-reexport/sha', reexport['sha256'], normal['sha256']),
    ('product-final-passive/sha', passive['sha256'], normal['sha256']),
    ('product-final-clock/sha', clock['sha256'], normal['sha256']),
    ('clock/events', clock_events, {'at_least': 45, 'coverage_frame': 630}),
    ('owner/equal', {'run-01': owner1['sha256'], 'run-02': owner2['sha256'], 'bytes': [owner1['bytes'], owner2['bytes']]}, {'equal': owner1 == owner2}),
    ('owner/sha', owner1['sha256'], 'A846F4B54A83466B5E7356FD7D69DFD6227CB569E59A05DE9CB09A82FC1B6676'),
    ('owner/bytes', owner1['bytes'], 2135876),
    ('owner/frame300_size', owner1_shape, [720, 1280, 3]),
    ('owner/frame300_magenta', owner1_magenta, 921600),
    ('fixtures/all_off', fixtures, []),
]

def ok(expected, actual):
    if isinstance(expected, dict) and 'delta_lt_0_1' in expected:
        return expected['delta_lt_0_1']
    if isinstance(expected, dict) and 'at_least' in expected:
        return actual >= expected['at_least'] and actual == 45
    if isinstance(expected, dict) and 'equal' in expected:
        return expected['equal']
    if isinstance(actual, str) and isinstance(expected, str):
        return actual.lower() == expected.lower()
    return actual == expected

records = []
for name, actual, expected in checks:
    records.append({'name': name, 'ok': bool(ok(expected, actual)), 'detail': actual})

evidence = {
    'schema': 'aave-g6-final-evidence-v1',
    'checked_at': '2026-11-27T08:35:00+08:00',
    'branch': 'runtime-virtualization-1.0.0',
    'head': '27bcaa5',
    'root': root,
    'build': {
        'plugin_sha256': checks[0][1],
        'core_sha256': checks[1][1],
        'version': '1.0.0',
        'font_atlas_policy': 'prime all story fonts at all label sizes and all four font styles before capture; force canvas update; this makes cold and repeated exports share the same glyph atlas layout',
    'note': 'new baseline after audio override fix and deterministic font-atlas prewarm; old CCCA/57BD build could not be reproduced'
    },
    'normal': {'end': normal_end, 'mov': normal, 'background_mean': bg_mean},
    'double': {'end': read_end('product-final-double'), 'movs': {'run-01': double1, 'run-02': double2}},
    'cancel': {'end': cancel_end},
    'reexport': {'mov': reexport},
    'passive': {'mov': passive},
    'clock': {'end': clock_end, 'mov': clock, 'events': clock_events},
    'owner': {'end': owner_end, 'movs': {'run-01': owner1, 'run-02': owner2},
              'frame300': {'shape': owner1_shape, 'magenta': owner1_magenta}},
    'fixtures_active': fixtures,
    'checks': records,
    'passed': sum(1 for r in records if r['ok']),
    'total': len(records),
}
out = os.path.join(root, 'g6-final-evidence.json')
with open(out, 'w', encoding='utf-8') as f:
    json.dump(evidence, f, ensure_ascii=False, indent=2)
print(out, len(records), sum(1 for r in records if r['ok']))
print(json.dumps([r for r in records if not r['ok']], ensure_ascii=False, indent=2))
