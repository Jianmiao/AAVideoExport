import sys
import numpy as np
from scipy.signal import resample_poly

a = np.fromfile(sys.argv[1], dtype=np.int16).astype(np.float32)
b = np.fromfile(sys.argv[2], dtype=np.int16).astype(np.float32)
# stereo interleaved; use left channel
a = a[0::2]
b = b[0::2]
a8 = resample_poly(a, 1, 6)
b8 = resample_poly(b, 1, 6)
n = min(len(a8), len(b8))
a8 = a8[:n]; b8 = b8[:n]
best = None
for lag in range(-4000, 4001, 1):
    xa = a8[max(0, lag):min(n, n + lag)]
    yb = b8[max(0, -lag):min(n, n - lag)]
    denom = np.sqrt(np.dot(xa, xa) * np.dot(yb, yb))
    corr = float(np.dot(xa, yb) / denom) if denom else 0.0
    if best is None or corr > best[1]:
        best = (lag, corr)
print('best lag at 8k', best, 'seconds', best[0] / 8000.0)
# segment RMS comparison
for k in range(0, 21, 2):
    s = k * 48000
    e = min(s + 48000, len(a))
    ra = float(np.sqrt(np.mean(a[s:e] ** 2))) if e > s else 0
    rb = float(np.sqrt(np.mean(b[s:e] ** 2))) if e > s else 0
    print(k, round(ra, 1), round(rb, 1))
