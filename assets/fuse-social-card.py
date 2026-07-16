#!/usr/bin/env python3
"""Generate the Fuse social preview card as SVG.

The 1200 by 628 canvas matches the large website-card ratio used by X. Content stays
inside a 52px safe border. Re-run from the assets directory:

    python fuse-social-card.py
"""
import sys
from html import escape

BG = "#f8f9fc"
BRAND = "#6d4aff"
TEXT = "#171a21"
MUTED = "#566072"
BORDER = "#e2e5ec"
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif"

W, H = 1200, 628
elements = []


def text(x, y, value, *, size, fill=TEXT, font=SANS, anchor="start", weight="400", spacing=None):
    letter_spacing = f' letter-spacing="{spacing}"' if spacing else ""
    elements.append(
        f'<text x="{x:.1f}" y="{y:.1f}" font-family="{font}" font-size="{size}" '
        f'fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{letter_spacing}>'
        f'{escape(value)}</text>'
    )


def rich_text(x, y, spans, *, size, anchor="middle", spacing=None):
    letter_spacing = f' letter-spacing="{spacing}"' if spacing else ""
    content = "".join(
        f'<tspan fill="{fill}" font-weight="{weight}">{escape(value)}</tspan>'
        for value, fill, weight in spans
    )
    elements.append(
        f'<text x="{x:.1f}" y="{y:.1f}" font-family="{SANS}" font-size="{size}" '
        f'text-anchor="{anchor}" xml:space="preserve"{letter_spacing}>{content}</text>'
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


def compose():
    elements.append(f'<rect width="{W}" height="{H}" fill="{BG}"/>')
    center = W / 2

    # Product identity.
    logo_mark(center - 36, 25, 72)
    text(center, 148, "Fuse", size=46, fill=BRAND, weight="800", anchor="middle", spacing="-0.3")

    # Product purpose. Highlight the open-source model and the intended user.
    rich_text(
        center,
        214,
        [
            ("Free and open source", BRAND, "800"),
            (" CLI and MCP", TEXT, "800"),
        ],
        size=40,
        spacing="-0.5",
    )
    rich_text(
        center,
        264,
        [
            ("to speed up ", TEXT, "800"),
            ("AI coding agents", BRAND, "800"),
            (" in .NET codebases", TEXT, "800"),
        ],
        size=40,
        spacing="-0.5",
    )
    text(
        center,
        312,
        "Indexed discovery, reduced source, and compiler-backed C# checks.",
        size=24,
        fill=MUTED,
        weight="600",
        anchor="middle",
    )

    # Recorded, sample-bounded results.
    stats = [
        (270, "1.8 ms", "exact symbol lookup", "median, 14,760 symbols"),
        (600, "38-44%", "skeleton token reduction", "four recorded repositories"),
        (930, "0", "false-green results", "1,000 compiler-labeled edits"),
    ]
    for x, value, label, detail in stats:
        text(x, 398, value, size=32, fill=BRAND, weight="800", anchor="middle")
        text(x, 430, label, size=17, fill=TEXT, weight="700", anchor="middle")
        text(x, 456, detail, size=14, fill=MUTED, anchor="middle")
    line(435, 366, 435, 460, stroke=BORDER, stroke_width=2)
    line(765, 366, 765, 460, stroke=BORDER, stroke_width=2)

    line(center - 310, 492, center + 310, 492, stroke=BORDER, stroke_width=2)
    text(center, 537, "fuse.codes", size=23, fill=TEXT, weight="800", anchor="middle")
    text(center, 577, "github.com/Litenova-Solutions/Fuse", size=20, fill=MUTED, weight="600", anchor="middle")


def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-social-card-v2.svg"
    head = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
        f'viewBox="0 0 {W} {H}" font-family="{SANS}">'
    )
    with open(path, "w", encoding="utf-8") as output:
        output.write(head + "".join(elements) + "</svg>")
    print("wrote", path, W, "x", H)


main()
