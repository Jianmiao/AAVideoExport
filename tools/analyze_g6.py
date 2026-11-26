import hashlib
import json
import os
import subprocess
import sys

root = sys.argv[1]
for dirpath, _, files in os.walk(root):
    for name in files:
        if not name.endswith('.mov'):
            continue
        path = os.path.join(dirpath, name)
        rel = os.path.relpath(path, root)
        probe = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                                '-show_entries', 'stream=nb_frames,width,height,r_frame_rate,duration',
                                '-of', 'json', path], capture_output=True, text=True)
        info = json.loads(probe.stdout)['streams'][0]
        v = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-map', '0:v:0', '-f', 'framemd5', '-'],
                           capture_output=True)
        vhash = hashlib.md5(v.stdout).hexdigest()
        a = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-map', '0:a:0', '-f', 'md5', '-'],
                           capture_output=True, text=True)
        ahash = a.stdout.strip()
        print(json.dumps({'path': rel, 'frames': info.get('nb_frames'), 'size': os.path.getsize(path),
                          'video_framemd5': vhash, 'audio_md5': ahash}, ensure_ascii=False))
