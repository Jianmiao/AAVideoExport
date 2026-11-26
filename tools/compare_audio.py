import sys
import numpy as np

a = np.fromfile(sys.argv[1], dtype=np.int16).astype(np.int32)
b = np.fromfile(sys.argv[2], dtype=np.int16).astype(np.int32)
print('shape', a.shape, b.shape)
if a.shape != b.shape:
    raise SystemExit(0)
d = a - b
print('equal', np.array_equal(a, b), 'maxdiff', int(np.abs(d).max()), 'meanabs', float(np.abs(d).mean()), 'nonzero', int(np.count_nonzero(d)))
x = a[:48000 * 5].astype(np.float64)
y = b[:48000 * 5].astype(np.float64)
for lag in (-480, -240, -120, -60, -30, -10, 0, 10, 30, 60, 120, 240, 480):
    xa = x[max(0, lag):min(len(x), len(y) + lag)]
    yb = y[max(0, -lag):min(len(y), len(x) - lag)]
    denom = np.sqrt(np.dot(xa, xa) * np.dot(yb, yb))
    print('lag', lag, 'corr', float(np.dot(xa, yb) / denom) if denom else 0.0)
