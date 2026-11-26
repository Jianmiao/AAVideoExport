import hashlib
import json
import os

root = r'E:\aamod\AAVE-zero-per-mod-20261121\artifacts\rvl-1.0.0\g6-c4'


def sha(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def frame_png_mean(path, crop=None):
    from PIL import Image
    import numpy as np
    a = np.array(Image.open(path))[:, :, :3]
    if crop:
        top, bottom, left, right = crop
        a = a[top:bottom, left:right]
    return [round(float(v), 3) for v in a.reshape(-1, 3).mean(axis=0)]


def read_end(name):
    path = os.path.join(root, name, 'runtime-trace.jsonl')
    lines = [line for line in open(path, encoding='utf-8') if '"name":"end"' in line]
    return json.loads(lines[-1]) if lines else None


product_paths = {
    'product-normal': ['AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-double': ['run-01/AAVE-OfficialAuthoring-20261030.mov', 'run-02/AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-passive': ['AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-clock': ['AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-reexport': ['run-02/AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-owner-01': ['AAVE-OfficialAuthoring-20261030.mov'],
    'product-final-owner-02': ['AAVE-OfficialAuthoring-20261030.mov'],
}
movs = {}
for name, relatives in product_paths.items():
    for rel in relatives:
        path = os.path.join(root, name, rel)
        movs[f'{name}/{rel}'] = {'sha256': sha(path), 'bytes': os.path.getsize(path)}

media_hashes = {}
media_file = os.path.join(root, 'g6-media-hashes.jsonl')
for line in open(media_file, encoding='utf-8'):
    item = json.loads(line)
    media_hashes[item['path']] = {
        'frames': item['frames'], 'bytes': item['size'],
        'video_framemd5': item['video_framemd5'], 'audio_md5': item['audio_md5']
    }

evidence = {
    'schema': 'aave-g6-evidence-v1',
    'generated_at': '2026-11-22T16:57:00+08:00',
    'build': {
        'plugin_sha256': sha(r'F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\1.0.0\AAVideoExport.dll'),
        'core_sha256': sha(r'F:\AzureArchive_AAVE03Lab\mods\AAVideoExport\1.0.0\AAVideoExport.Core.dll'),
        'version': '1.0.0',
    },
    'normal': {'end': read_end('product-normal'), 'movs': movs['product-normal/AAVE-OfficialAuthoring-20261030.mov']},
    'double': {
        'end_run_01': read_end('product-final-double'),
        'movs': {k: v for k, v in movs.items() if k.startswith('product-final-double/')},
    },
    'cancel': {'end': read_end('product-final-cancel')},
    'reexport': {'end_run_01': read_end('product-final-reexport'), 'end_run_02': read_end('product-final-reexport')},
    'passive': {'end': read_end('product-final-passive'), 'movs': movs['product-final-passive/AAVE-OfficialAuthoring-20261030.mov']},
    'clock': {
        'end': read_end('product-final-clock'),
        'events': 45,
        'movs': movs['product-final-clock/AAVE-OfficialAuthoring-20261030.mov'],
    },
    'owner': {
        'end_run_01': read_end('product-final-owner-01'),
        'movs': {
            k: v for k, v in movs.items()
            if k.startswith('product-final-owner-0')
        },
        'frame_300_mean_rgb': {
            'owner-01': frame_png_mean(os.path.join(root, 'product-final-owner-01', 'f300.png')),
            'owner-02': frame_png_mean(os.path.join(root, 'product-final-owner-02', 'f300.png')),
        },
    },
    'product_normal_frame_300': {
        'png': os.path.join(root, 'product-normal', 'f300.png'),
        'png_sha256': sha(os.path.join(root, 'product-normal', 'f300.png')),
        'background_region_mean_rgb': frame_png_mean(
            os.path.join(root, 'product-normal', 'f300.png'), (30, 90, 40, 1240)),
    },
    'media_hashes': media_hashes,
    'identity': {
        'shared_product_sha256': '276338D340A32A6E300374A17FD1BDF91A606FB581F6B49FE446940173C10BF3',
        'shared_video_framemd5': 'edd987aee2c903ddcf726ac94c080a17',
        'shared_audio_md5': 'MD5=8fce23a616861fcda262758b39b9e8be',
        'owner_sha256': '93B6B4CA91120F2E23C753FBB04A4787AA61588CB299A3E180EAC8C3B3143E2C',
    }
}

out = os.path.join(root, 'g6-evidence.json')
with open(out, 'w', encoding='utf-8') as f:
    json.dump(evidence, f, ensure_ascii=False, indent=2)
print(out, os.path.getsize(out))
