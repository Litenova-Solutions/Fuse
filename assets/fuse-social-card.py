#!/usr/bin/env python3
"""Generate the Fuse social preview card (1280x640 SVG) for the GitHub repo social image.

Pure Python stdlib, no dependencies. Brand-matched to the logo (purple #6d4aff) and the
site. All key content stays inside a 40pt safe border so nothing is cropped by social
platforms. Re-run:

    python fuse-social-card.py                  # writes fuse-social-card.svg
"""
import sys
from html import escape

# brand
BG = "#ffffff"
BRAND, BRAND_SOFT = "#6d4aff", "#8f7bff"
TEXT, MUTED, FAINT = "#171a21", "#566072", "#8a93a3"
TRACK = "#eceef3"
BORDER = "#e5e8ef"
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif"
MONO = "ui-monospace,'Cascadia Code','SF Mono','JetBrains Mono',Consolas,monospace"

W, H = 1280, 640
el = []


def text(x, y, s, *, size, fill=TEXT, font=SANS, anchor="start", weight="400", spacing=None, op=None):
    sp = f' letter-spacing="{spacing}"' if spacing else ""
    o = f' opacity="{op}"' if op is not None else ""
    el.append(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{font}" font-size="{size}" '
              f'fill="{fill}" text-anchor="{anchor}" font-weight="{weight}"{sp}{o}>{escape(s)}</text>')


def rrect(x, y, w, h, r, fill, stroke=None, sw=1, op=None):
    s = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    o = f' opacity="{op}"' if op is not None else ""
    el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="{r}"{s}{o} fill="{fill}"/>')


def logo_stacked(center_x, top_y, scale):
    # The full Fuse logo from assets/fuse-logo.svg: the rounded purple mark with the
    # circuit glyph, and the "Fuse" wordmark stacked directly beneath it. Native art is
    # 220 wide with its content centered on x=110, so translate by center_x - 110*scale.
    tx = center_x - 110 * scale
    el.append(f'<g transform="translate({tx:.1f},{top_y}) scale({scale})">'
              '<g transform="translate(74,10)">'
              '<rect width="72" height="72" rx="16" fill="#6d4aff"/>'
              '<g transform="scale(2.25)" fill="none" stroke="#ffffff" stroke-width="2.4" '
              'stroke-linecap="round" stroke-linejoin="round">'
              '<path d="M7 9h5" opacity="0.7"/><path d="M7 16h4"/><path d="M7 23h5" opacity="0.7"/>'
              '<path d="M12 9c3.5 0 1.5 7 5 7" opacity="0.7"/>'
              '<path d="M12 23c3.5 0 1.5-7 5-7" opacity="0.7"/><path d="M19 16h6"/></g>'
              '<circle cx="39.4" cy="36" r="6.1" fill="#ffffff"/></g>'
              '<text x="110" y="132" text-anchor="middle" font-family="' + SANS + '" '
              'font-size="44" font-weight="800" fill="#6d4aff" letter-spacing="0.5">Fuse</text>'
              '</g>')


def compose():
    el.append(f'<rect width="{W}" height="{H}" fill="{BG}"/>')
    cx = W / 2

    # --- logo: mark with the "Fuse" wordmark stacked beneath, centered ---
    logo_stacked(cx, 36, 1.32)

    # --- headline, centered ---
    text(cx, 318, "Typecheck your AI agent's .NET edits", size=54, fill=TEXT, weight="800",
         anchor="middle", spacing="-0.5")
    text(cx, 382, "before they land.", size=54, fill=BRAND, weight="800",
         anchor="middle", spacing="-0.5")

    # --- what the tool does, centered ---
    text(cx, 430, "An MCP server that checks a proposed edit against the compiler,",
         size=25, fill=MUTED, anchor="middle")
    text(cx, 466, "resolves .NET wiring from Roslyn, and assembles Git-seeded context.", size=25, fill=MUTED, anchor="middle")

    # --- stats, centered row (all deterministic, no model-caveat needed) ---
    stats = [
        (cx - 320, "0 false green", "over 1,000 compiler-labeled edits"),
        (cx, "24 of 24", "OrderingApp fixture wiring edges"),
        (cx + 320, "1,026 median tokens", "Git-seeded context, 93.4% precision"),
    ]
    for sx, val, label in stats:
        text(sx, 548, val, size=34, fill=TEXT, weight="800", anchor="middle")
        text(sx, 578, label, size=16, fill=FAINT, anchor="middle")


def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-social-card.svg"
    head = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
            f'viewBox="0 0 {W} {H}" font-family="{SANS}"><defs></defs>')
    with open(path, "w", encoding="utf-8") as f:
        f.write(head + "".join(el) + "</svg>")
    print("wrote", path, W, "x", H)


main()
