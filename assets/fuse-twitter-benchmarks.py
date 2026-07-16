#!/usr/bin/env python3
"""Generate the 1200 by 628 Fuse benchmark image for a Twitter launch post.

The image summarizes recorded results from performance.json, reduce.json, checkgate.json,
and semantics.json. Re-run from the assets directory:

    python fuse-twitter-benchmarks.py
"""
import math
import sys
from html import escape

W, H = 1200, 628
BG = "#f8f9fc"
CARD = "#ffffff"
TEXT = "#171a21"
MUTED = "#566072"
BRAND = "#6d4aff"
BRAND_SOFT = "#ddd7ff"
TRACK = "#e9ebf1"
BORDER = "#dfe3eb"
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif"

elements = []


def text(x, y, value, *, size, fill=TEXT, anchor="start", weight="400", spacing=None):
    letter_spacing = f' letter-spacing="{spacing}"' if spacing else ""
    elements.append(
        f'<text x="{x:.1f}" y="{y:.1f}" font-family="{SANS}" font-size="{size}" '
        f'fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{letter_spacing}>'
        f'{escape(value)}</text>'
    )


def rect(x, y, width, height, radius, fill, *, stroke=None, stroke_width=1):
    stroke_attrs = f' stroke="{stroke}" stroke-width="{stroke_width}"' if stroke else ""
    elements.append(
        f'<rect x="{x:.1f}" y="{y:.1f}" width="{width:.1f}" height="{height:.1f}" '
        f'rx="{radius}" fill="{fill}"{stroke_attrs}/>'
    )


def line(x1, y1, x2, y2, *, stroke, stroke_width=1):
    elements.append(
        f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
        f'stroke="{stroke}" stroke-width="{stroke_width}"/>'
    )


def circle(cx, cy, radius, fill, *, stroke=None, stroke_width=1):
    stroke_attrs = f' stroke="{stroke}" stroke-width="{stroke_width}"' if stroke else ""
    elements.append(
        f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{radius}" fill="{fill}"{stroke_attrs}/>'
    )


def logo_mark(x, y, size):
    scale = size / 72
    elements.append(
        f'<g transform="translate({x:.1f},{y:.1f}) scale({scale:.5f})">'
        '<rect width="72" height="72" rx="16" fill="#6d4aff"/>'
        '<g transform="scale(2.25)" fill="none" stroke="#ffffff" stroke-width="2.4" '
        'stroke-linecap="round" stroke-linejoin="round">'
        '<path d="M7 9h5" opacity="0.7"/><path d="M7 16h4"/>'
        '<path d="M7 23h5" opacity="0.7"/><path d="M12 9c3.5 0 1.5 7 5 7" opacity="0.7"/>'
        '<path d="M12 23c3.5 0 1.5-7 5-7" opacity="0.7"/><path d="M19 16h6"/>'
        '</g><circle cx="39.4" cy="36" r="6.1" fill="#ffffff"/></g>'
    )


def latency_x(value):
    left = 390
    width = 535
    maximum = 150
    return left + math.log10(value) / math.log10(maximum) * width


def compose():
    elements.append(f'<rect width="{W}" height="{H}" fill="{BG}"/>')

    # Header.
    logo_mark(52, 27, 48)
    text(116, 61, "Fuse", size=27, fill=BRAND, weight="800")
    line(205, 30, 205, 73, stroke=BORDER, stroke_width=2)
    text(230, 59, "Recorded .NET benchmark snapshot", size=31, weight="800", spacing="-0.4")
    text(230, 86, "Recorded results with each sample named; timings depend on the machine.", size=14, fill=MUTED)

    # Warm indexed-operation latency.
    rect(50, 108, 1100, 210, 18, CARD, stroke=BORDER)
    text(78, 141, "Warm indexed operations", size=21, weight="800")
    text(78, 165, "NodaTime semantic index / 14,760 symbols / 25 warm runs", size=13, fill=MUTED)
    text(1115, 141, "P50 / P95", size=13, fill=MUTED, anchor="end", weight="700")

    latency_rows = [
        ("Exact symbol lookup", 1.8, 2.3, "1.8 / 2.3 ms"),
        ("Task localization", 15.7, 22.3, "15.7 / 22.3 ms"),
        ("Incremental syntax re-index", 22.0, 24.2, "22.0 / 24.2 ms"),
        ("Review planning", 106.3, 131.1, "106.3 / 131.1 ms"),
    ]
    for row, (label, p50, p95, value) in enumerate(latency_rows):
        y = 198 + row * 29
        text(78, y + 5, label, size=16, weight="600")
        line(390, y, 925, y, stroke=TRACK, stroke_width=6)
        x50 = latency_x(p50)
        x95 = latency_x(p95)
        line(x50, y, x95, y, stroke=BRAND, stroke_width=6)
        circle(x50, y, 6, BRAND)
        circle(x95, y, 6, CARD, stroke=BRAND, stroke_width=3)
        text(1115, y + 5, value, size=15, anchor="end", weight="700")

    for tick, label in [(1, "1 ms"), (10, "10 ms"), (100, "100 ms")]:
        x = latency_x(tick)
        line(x, 300, x, 305, stroke=MUTED, stroke_width=1)
        text(x, 315, label, size=10, fill=MUTED, anchor="middle")
    text(925, 315, "log scale", size=10, fill=MUTED, anchor="end")

    # Repository-level source reduction.
    rect(50, 336, 530, 240, 18, CARD, stroke=BORDER)
    text(78, 369, "Skeleton token reduction", size=21, weight="800")
    text(78, 392, "Four repositories / o200k_base token counts", size=13, fill=MUTED)

    reduction_rows = [
        ("NodaTime", 38),
        ("Scrutor", 42),
        ("Specification", 44),
        ("eShopOnWeb", 42),
    ]
    base_y = 522
    for index, (label, value) in enumerate(reduction_rows):
        x = 112 + index * 116
        bar_height = value * 2
        rect(x, base_y - bar_height, 62, bar_height, 8, BRAND_SOFT)
        rect(x, base_y - bar_height, 62, 10, 8, BRAND)
        text(x + 31, base_y - bar_height - 9, f"{value}%", size=17, fill=BRAND, anchor="middle", weight="800")
        text(x + 31, 544, label, size=13, anchor="middle", weight="600")
    line(94, base_y, 546, base_y, stroke=BORDER, stroke_width=2)
    text(315, 566, "Measured names retained: types 100%; methods 96.3-99.4%.", size=12, fill=MUTED, anchor="middle")

    # Compiler-labeled proposed-file checks.
    rect(600, 336, 550, 240, 18, CARD, stroke=BORDER)
    text(628, 369, "Proposed C# edit checks", size=21, weight="800")
    text(628, 392, "1,000 compiler-labeled single-file edits / OrderingApp", size=13, fill=MUTED)

    check_rows = [
        ("Breaking edits reported", "500 / 500"),
        ("Neutral edits accepted", "500 / 500"),
    ]
    for index, (label, value) in enumerate(check_rows):
        y = 428 + index * 52
        text(628, y + 5, label, size=15, weight="600")
        rect(815, y - 12, 305, 24, 12, TRACK)
        rect(815, y - 12, 305, 24, 12, BRAND)
        text(1102, y + 5, value, size=15, fill="#ffffff", anchor="end", weight="800")

    rect(628, 523, 230, 34, 17, "#eeebff")
    text(743, 546, "0 false green", size=15, fill=BRAND, anchor="middle", weight="800")
    rect(884, 523, 236, 34, 17, "#eeebff")
    text(1002, 546, "0 false red", size=15, fill=BRAND, anchor="middle", weight="800")

    # One compact graph result and the verification link.
    line(50, 594, 1150, 594, stroke=BORDER, stroke_width=2)
    text(50, 616, "24 / 24 .NET wiring edges matched on OrderingApp; 0 false positives", size=13, fill=TEXT, weight="700")
    text(1150, 616, "Methods and limits: fuse.codes/docs/project/benchmarks", size=13, fill=MUTED, anchor="end")


def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-twitter-benchmarks.svg"
    head = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
        f'viewBox="0 0 {W} {H}" font-family="{SANS}">'
    )
    with open(path, "w", encoding="utf-8") as output:
        output.write(head + "".join(elements) + "</svg>")
    print("wrote", path, W, "x", H)


main()
