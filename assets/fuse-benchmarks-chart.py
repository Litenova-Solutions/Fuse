#!/usr/bin/env python3
"""Generate the Fuse benchmark figure (self-contained SVG) for the README and social sharing.

Pure Python stdlib, no dependencies. Light theme, one card per section, each pairing the
"without Fuse" baseline with the Fuse result and stating the improvement. All four sections
are measured and reproducible (tests/benchmarks/results, docs/project/benchmarks.md): section 4
is the layer-4 context-acquisition scenario. Edit the data in compose() or the theme, re-run:

    python generate_charts.py                      # writes fuse-benchmarks.svg
    python generate_charts.py docs/assets/x.svg    # custom path
"""
import sys
from html import escape

# --- theme (light) ---------------------------------------------------------
BG, CARD, BORDER = "#ffffff", "#f7f8fb", "#e5e8ef"
TEXT, MUTED, FAINT = "#171a21", "#566072", "#8a93a3"
TRACK = "#eceef3"
EMBER, EMBER2 = "#ea580c", "#fb923c"
TEAL, TEAL2 = "#0d9488", "#22c3ac"
BASE = "#c0c7d4"          # "without Fuse" baseline bars
INK_ON_BAR = "#ffffff"    # label on bright (ember/teal) bars
INK_ON_BASE = "#2b3140"   # label on gray baseline bars
MONO = "ui-monospace,'Cascadia Code','SF Mono','JetBrains Mono',Consolas,monospace"
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif"

W = 1120
PAD = 40
CPAD = 28          # padding inside a card
LABEL_W = 214      # left label gutter inside a card
BAR_H = 28
ROW_GAP = 14
CARD_GAP = 20

el_list = []
grad_n = [0]
def el(s): el_list.append(s)

def text(x, y, s, *, size=13, fill=TEXT, font=MONO, anchor="start", weight="400", spacing=None):
    sp = f' letter-spacing="{spacing}"' if spacing else ""
    el(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{font}" font-size="{size}" fill="{fill}" '
       f'text-anchor="{anchor}" font-weight="{weight}"{sp}>{escape(s)}</text>')

def rrect(x, y, w, h, r, fill, stroke=None, sw=1):
    s = f' stroke="{stroke}" stroke-width="{sw}"' if stroke else ""
    el(f'<rect x="{x:.1f}" y="{y:.1f}" width="{max(w,0):.1f}" height="{h:.1f}" rx="{r}"{s} fill="{fill}"/>')

def grad(c1, c2):
    grad_n[0] += 1
    gid = f"g{grad_n[0]}"
    el(f'<linearGradient id="{gid}" x1="0" y1="0" x2="1" y2="0">'
       f'<stop offset="0" stop-color="{c1}"/><stop offset="1" stop-color="{c2}"/></linearGradient>')
    return f"url(#{gid})"

def pill(x, y, label, fg, bg):
    w = 11 + len(label) * 6.6
    rrect(x, y - 12, w, 17, 8.5, bg)
    text(x + w / 2, y, label, size=10.5, fill=fg, anchor="middle", weight="600", spacing="0.04em")
    return w

def card(y, num, title, subtitle, subtexts, rows, dmax, conclusion, *, fuse=EMBER, fuse2=EMBER2, illustrative=False):
    cx, cw = PAD, W - 2 * PAD
    o_title = 38
    o_sub = o_title + 22
    o_sub0 = o_sub + 21
    st_h = 17
    o_bars = o_sub0 + len(subtexts) * st_h + 12
    bars_h = len(rows) * (BAR_H + ROW_GAP) - ROW_GAP
    o_concl = o_bars + bars_h + 18
    concl_h = 38
    card_h = o_concl + concl_h + 24

    rrect(cx, y, cw, card_h, 16, CARD, stroke=BORDER)

    text(cx + CPAD, y + o_title, num, size=12, fill=EMBER, weight="700")
    text(cx + CPAD + 30, y + o_title, title, size=18, fill=TEXT, weight="700")
    if illustrative:
        pill(cx + CPAD + 30 + len(title) * 10.4 + 16, y + o_title - 2, "ILLUSTRATIVE", "#8a5a12", "#fbecd6")
    text(cx + CPAD, y + o_sub, subtitle, size=13, fill=MUTED, font=SANS, weight="500")
    for i, st in enumerate(subtexts):
        text(cx + CPAD, y + o_sub0 + i * st_h, st, size=12.5, fill=FAINT, font=SANS)

    track_x = cx + CPAD + LABEL_W
    track_w = cw - 2 * CPAD - LABEL_W
    for i, (label, value, vlabel, kind) in enumerate(rows):
        by = y + o_bars + i * (BAR_H + ROW_GAP)
        cy = by + BAR_H / 2
        is_base = kind == "base"
        tag = "without Fuse" if is_base else "with Fuse"
        text(track_x - 16, cy - 2, label, size=13, fill="#2b3140", anchor="end", weight="500")
        text(track_x - 16, cy + 12, tag, size=10, fill=(FAINT if is_base else EMBER), anchor="end", spacing="0.03em")
        rrect(track_x, by, track_w, BAR_H, 6, TRACK)
        bw = track_w * (value / dmax)
        fill = BASE if is_base else (grad(fuse, fuse2))
        rrect(track_x, by, bw, BAR_H, 6, fill)
        if bw >= 84:
            text(track_x + bw - 12, cy + 4.5, vlabel, size=13, fill=(INK_ON_BASE if is_base else INK_ON_BAR), anchor="end", weight="700")
        else:
            text(track_x + bw + 11, cy + 4.5, vlabel, size=13, fill=TEXT, weight="700")

    cyc = y + o_concl
    rrect(cx + CPAD, cyc, cw - 2 * CPAD, concl_h, 9, "#fff4ec" if not illustrative else "#f3f5f8", stroke="#f3d8c4" if not illustrative else BORDER)
    rrect(cx + CPAD, cyc, 4, concl_h, 2, fuse)
    text(cx + CPAD + 18, cyc + 24, "Result", size=10.5, fill=fuse, weight="700", spacing="0.08em")
    text(cx + CPAD + 78, cyc + 24, conclusion, size=13.5, fill=TEXT, font=SANS, weight="600")
    return y + card_h + CARD_GAP

def header():
    text(PAD, 52, "FUSE", size=23, fill=EMBER, weight="800", spacing="1.5")
    text(PAD + 78, 52, "/  .NET Codebase Context Optimizer for AI Agents", size=13, fill=MUTED, font=SANS, weight="600")
    text(PAD, 92, "Read Less. Keep the Code.", size=30, fill=TEXT, weight="800", spacing="-0.5")
    lines = [
        "Fuse turns a .NET codebase into a compact, structured payload an AI agent can read in a single call",
        "instead of opening thousands of files. It cuts tokens while keeping the public API intact, scopes to the files",
        "a task actually needs, and trims the round-trips an agent makes to get there.",
    ]
    for i, ln in enumerate(lines):
        text(PAD, 120 + i * 19, ln, size=13.5, fill=MUTED, font=SANS)
    ly = 120 + len(lines) * 19 + 8
    rrect(PAD, ly - 11, 13, 13, 3, BASE)
    text(PAD + 20, ly, "Without Fuse (raw, other tools, baselines)", size=12, fill=MUTED, font=SANS)
    rrect(PAD + 322, ly - 11, 13, 13, 3, EMBER)
    text(PAD + 342, ly, "With Fuse", size=12, fill=MUTED, font=SANS)
    return ly + 22

def compose():
    y = header()
    y = card(
        y, "01", "Tokens to Read a Codebase",
        "Output size versus reading the raw files. Newtonsoft.Json, 945 files, at full public-API fidelity.",
        ["Smaller input leaves more of the model's context window for reasoning and lowers cost per call.",
         "Default mode cuts 7-10%; --all cuts 21-40% across the corpus, all at 99-100% type and method fidelity."],
        [("Raw concatenation", 100.0, "1.47M tokens", "base"),
         ("Repomix (generic packer)", 101.2, "1.49M tokens", "base"),
         ("Fuse --all", 59.9, "880K tokens", "fuse")],
        102.0,
        "40% fewer tokens than the raw codebase, ~100% of the public API kept. About 1.7x less to read.")
    y = card(
        y, "02", "Finding the Files a Change Needs",
        "Recall within a 50,000-token budget across 24 real merged pull requests.",
        ["Higher recall means the agent gets the files a change needs the first time, instead of re-querying.",
         "Every Fuse scoping mode clears the grep baseline; git change-scoping reaches 88%."],
        [("grep", 38, "38%", "base"),
         ("Fuse focus", 43, "43%", "fuse"),
         ("Fuse query", 54, "54%", "fuse"),
         ("Fuse changes", 88, "88%", "fuse")],
        100,
        "Up to 2.3x more of the needed files found than a grep baseline (88% vs 38%).")
    y = card(
        y, "03", "Method Signatures Kept in Skeleton Mode",
        "Public methods preserved when Fuse emits signatures only. Newtonsoft.Json.",
        ["A skeleton is only useful if it is faithful. The regex default collapses on conditional compilation",
         "and partial classes; the opt-in Roslyn precision tier keeps every signature it drops."],
        [("Regex (AOT default)", 4, "4%", "base"),
         ("Fuse Roslyn tier", 100, "100%", "fuse")],
        100,
        "25x more public method signatures kept with the opt-in Roslyn tier (100% vs 4%).",
        fuse=TEAL, fuse2=TEAL2)
    y = card(
        y, "04", "Context for One Task, in One Call",
        "Input tokens to acquire the context a change needs, over 24 real merged pull requests at a 50,000-token budget.",
        ["A blind agent reads files one by one (a lower bound of about 6 round-trips here); a packer and Fuse each take one call.",
         "Fuse query reaches 51% of the changed files; the packer and the blind read include everything (recall 1.00 by construction)."],
        [("Read files blind", 493661, "494K  >= 6 calls", "base"),
         ("Repomix, one dump", 511574, "512K  1 call", "base"),
         ("Fuse --query", 40041, "40K  1 call", "fuse")],
        520000,
        "One call at about 13x fewer tokens than the generic packer (40K vs 512K), recall 51% (measured, query mode).")
    return y

total = compose()
H = total + 30
head = [
    f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="{SANS}">',
    f'<rect x="0" y="0" width="{W}" height="{H}" fill="{BG}"/>',
]
foot = [
    f'<text x="{PAD}" y="{H-20}" font-family="{MONO}" font-size="11" fill="{FAINT}">'
    f'github.com/Litenova-Solutions/Fuse  .  MIT  .  o200k_base tokens  .  all four sections reproducible via tests/benchmarks</text>',
    "</svg>",
]
path = sys.argv[1] if len(sys.argv) > 1 else "fuse-benchmarks.svg"
with open(path, "w", encoding="utf-8") as f:
    f.write("\n".join(head + el_list + foot))
print("wrote", path, "size", W, "x", H)
