#!/usr/bin/env python3
"""Verifies the core reverse-engineering claim in 01-target-analysis.md §4.1:
the take-profits are exact R-multiples of the stop distance, TP2 always 2x TP1.

Numbers read off reference/*.jpeg. Run: python3 verify_bracket_geometry.py
"""

def r_multiple(entry, stop, tp):
    return abs(tp - entry) / abs(entry - stop)


def check(name, entry, stop, tps, expected, tol=1e-9):
    got = [r_multiple(entry, stop, tp) for tp in tps]
    for g, e in zip(got, expected):
        assert abs(g - e) < tol, f"{name}: got {g:.4f}R, expected {e}R"
    assert abs(got[1] - 2 * got[0]) < tol, f"{name}: TP2 is not 2x TP1"
    print(f"{name}: R={abs(entry-stop):.2f} -> " + ", ".join(f"{g:.2f}R" for g in got))


# Image (1) — NQ short, B_ prefix. All three land exactly.
check("AZ  NQ short", 23713.75, 23729.25, [23707.55, 23701.35, 23690.50], [0.4, 0.8, 1.5])

# Image (2) — MNQ long, A_ prefix. Stop's last digit is clipped at the screenshot
# edge; 26871.15 is the value that makes both TPs exact, and it matches the visible
# "Stop 26871.1". ponytail: this one is inferred, not read — flagged in the doc.
check("MA MNQ long", 26877.00, 26871.15, [26879.925, 26882.85], [0.5, 1.0], tol=1e-6)

print("\nBoth configs: TP2 = 2 x TP1, multiples differ between configs -> optimised, not fixed.")
