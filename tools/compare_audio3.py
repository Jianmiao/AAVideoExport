import sys
import numpy as np

a = np.fromfile(sys.argv[1], dtype=np.int16).astype(np.float64)[0::2]
b = np.fromfile(sys.argv[2], dtype=np.int16).astype(np.float64)[0::2]
seg = 48000 * 10
best = None
for lag in range(-2000, 2001):
    xa = a[max(0, lag):min(seg, seg + lag)]
    yb = b[max(0, -lag):min(seg, seg - lag)]
    denom = np.sqrt(np.dot(xa, xa) * np.dot(yb, yb))
    corr = float(np.dot(xa, yb) / denom) if denom else 0.0
    if best is None or corr > best[1]:
        best = (lag, corr)
print('best', best, 'ms', best[0] / 48.0)
lag = best[0]
if lag >= 0:
    aa = a[lag:]; bb = b[:len(aa)]
else:
    bb = b[-lag:]; aa = a[:len(bb)]
print('aligned equal', np.array_equal(aa, bb), 'maxdiff', int(np.abs(aa - bb).max()), 'nonzero', int(np.count_nonzero(aa - bb)))
print('aligned md5?', __import__('hashlib').md5(aa.astype(np.int16).tobytes()).hexdigest(), __import__('hashlib').md5(bb.astype(np.int16).tobytes()).hexdigest())
