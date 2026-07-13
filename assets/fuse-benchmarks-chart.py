#!/usr/bin/env python3
"""Generate the Fuse benchmark figure (self-contained SVG) for the README and social sharing.

Pure Python stdlib, no dependencies. Light theme, one card per section. Four measured,
reproducible panels (tests/benchmarks/results,
site/content/docs/project/benchmarks.mdx): check honesty, the loop referendum, .NET wiring,
and change scoping. Localize and agent recall are not on the figure; they live on the
benchmarks page. Edit the data in compose() or the theme, re-run:

    python fuse-benchmarks-chart.py                 # writes fuse-benchmarks.svg
    python fuse-benchmarks-chart.py path/to/x.svg   # custom path

To rasterize the SVG to the PNG (fuse-benchmarks.png), run from the site/ directory (which has
sharp installed), so the bare `sharp` import resolves:

    cd site && node -e "import('sharp').then(s=>s.default(require('fs').readFileSync('../assets/fuse-benchmarks.svg'),{density:144}).png().toFile('../assets/fuse-benchmarks.png'))"

density 144 renders the SVG at 2x. If sharp is unavailable, headless Chrome also works:
chrome --headless=new --window-size=<W>,<H> --screenshot=fuse-benchmarks.png fuse-benchmarks.svg
"""
import sys
from html import escape

# --- theme (light) ---------------------------------------------------------
BG, CARD, BORDER = "#ffffff", "#f7f8fb", "#e5e8ef"
TEXT, MUTED, FAINT = "#171a21", "#566072", "#8a93a3"
TRACK = "#eceef3"
EMBER, EMBER2 = "#ea580c", "#fb923c"
TEAL, TEAL2 = "#0d9488", "#22c3ac"
BASE = "#c0c7d4"          # baseline bars (bare tools, grep)
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

def line(x1, y1, x2, y2, stroke, sw=2):
    el(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" '
       f'stroke="{stroke}" stroke-width="{sw}" stroke-linecap="round"/>')

def circle(cx, cy, r, fill):
    el(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{r}" fill="{fill}"/>')

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

def wrap(s, max_w, *, size, char_w=0.54):
    # Greedy word wrap to fit max_w pixels, estimating glyph advance as size * char_w.
    per = max(1, int(max_w / (size * char_w)))
    words, lines, cur = s.split(), [], ""
    for word in words:
        cand = word if not cur else cur + " " + word
        if len(cand) <= per:
            cur = cand
        else:
            if cur:
                lines.append(cur)
            cur = word
    if cur:
        lines.append(cur)
    return lines or [""]

def draw_card(y, num, title, subtitle, subtexts, conclusion, body_h, body_draw,
              *, accent=EMBER, accent2=EMBER2, pill_label=None):
    # Shared card shell: outer frame, heading block, a pluggable body, and the result box.
    # body_h is the height the body_draw callback needs between the subtexts and the result.
    cx, cw = PAD, W - 2 * PAD
    o_title = 38
    o_sub = o_title + 22
    o_sub0 = o_sub + 21
    st_h = 17
    o_body = o_sub0 + len(subtexts) * st_h + 12
    o_concl = o_body + body_h + 18
    concl_text_x = CPAD + 78
    concl_avail = cw - 2 * CPAD - 78 - 16
    concl_lines = wrap(conclusion, concl_avail, size=13.5)
    concl_lh = 18
    concl_h = max(38, 14 + len(concl_lines) * concl_lh)
    card_h = o_concl + concl_h + 24

    rrect(cx, y, cw, card_h, 16, CARD, stroke=BORDER)

    text(cx + CPAD, y + o_title, num, size=12, fill=accent, weight="700")
    text(cx + CPAD + 30, y + o_title, title, size=18, fill=TEXT, weight="700")
    if pill_label:
        pill(cx + CPAD + 30 + len(title) * 10.4 + 16, y + o_title - 2, pill_label, "#8a5a12", "#fbecd6")
    text(cx + CPAD, y + o_sub, subtitle, size=13, fill=MUTED, font=SANS, weight="500")
    for i, st in enumerate(subtexts):
        text(cx + CPAD, y + o_sub0 + i * st_h, st, size=12.5, fill=FAINT, font=SANS)

    body_draw(cx, y + o_body, cw, accent, accent2)

    cyc = y + o_concl
    on_ember = accent == EMBER
    box_bg = "#fff4ec" if on_ember else "#eafcf8"
    box_stroke = "#f3d8c4" if on_ember else "#bfeee4"
    rrect(cx + CPAD, cyc, cw - 2 * CPAD, concl_h, 9, box_bg, stroke=box_stroke)
    rrect(cx + CPAD, cyc, 4, concl_h, 2, accent)
    block_top = cyc + (concl_h - len(concl_lines) * concl_lh) / 2 + 14
    text(cx + CPAD + 18, block_top - 1, "Result", size=10.5, fill=accent, weight="700", spacing="0.08em")
    for i, ln in enumerate(concl_lines):
        text(cx + concl_text_x, block_top - 1 + i * concl_lh, ln, size=13.5, fill=TEXT, font=SANS, weight="600")
    return y + card_h + CARD_GAP

def make_bars(rows, dmax, *, base_tag="without Fuse", fuse_tag="with Fuse"):
    # Horizontal bars in a track. Each row is (label, value, vlabel, kind); kind "base" is gray.
    body_h = len(rows) * (BAR_H + ROW_GAP) - ROW_GAP
    def draw(cx, by0, cw, accent, accent2):
        track_x = cx + CPAD + LABEL_W
        track_w = cw - 2 * CPAD - LABEL_W
        for i, (label, value, vlabel, kind) in enumerate(rows):
            by = by0 + i * (BAR_H + ROW_GAP)
            cy = by + BAR_H / 2
            is_base = kind == "base"
            tag = base_tag if is_base else fuse_tag
            text(track_x - 16, cy - 2, label, size=13, fill="#2b3140", anchor="end", weight="500")
            text(track_x - 16, cy + 12, tag, size=10, fill=(FAINT if is_base else accent), anchor="end", spacing="0.03em")
            rrect(track_x, by, track_w, BAR_H, 6, TRACK)
            bw = track_w * (value / dmax)
            fill = BASE if is_base else grad(accent, accent2)
            rrect(track_x, by, bw, BAR_H, 6, fill)
            if bw >= 84:
                text(track_x + bw - 12, cy + 4.5, vlabel, size=13, fill=(INK_ON_BASE if is_base else INK_ON_BAR), anchor="end", weight="700")
            else:
                text(track_x + bw + 11, cy + 4.5, vlabel, size=13, fill=TEXT, weight="700")
    return body_h, draw

def make_dotwhisker(rows):
    # Point estimate with a 95% CI interval, the honest encoding for a reduced-scope
    # proportion. Each row is (label, value, vlabel, ci=(lo,hi), false_done).
    body_h = len(rows) * (BAR_H + ROW_GAP) - ROW_GAP
    def draw(cx, by0, cw, accent, accent2):
        track_x = cx + CPAD + LABEL_W
        track_w = cw - 2 * CPAD - LABEL_W
        for i, (label, value, vlabel, ci, fdone) in enumerate(rows):
            by = by0 + i * (BAR_H + ROW_GAP)
            cy = by + BAR_H / 2
            text(track_x - 16, cy - 2, label, size=13, fill="#2b3140", anchor="end", weight="500")
            text(track_x - 16, cy + 12, f"false-done {fdone}", size=10, fill=FAINT, anchor="end", spacing="0.03em")
            rrect(track_x, cy - 3, track_w, 6, 3, TRACK)          # 0-100 scale guide
            lo = track_x + track_w * (ci[0] / 100)
            hi = track_x + track_w * (ci[1] / 100)
            px = track_x + track_w * (value / 100)
            rrect(lo, cy - 2, hi - lo, 4, 2, accent2)             # CI interval band
            line(lo, cy - 7, lo, cy + 7, accent, 2)               # CI caps
            line(hi, cy - 7, hi, cy + 7, accent, 2)
            circle(px, cy, 6, accent)                             # point estimate
            text(px, cy - 12, vlabel, size=12.5, fill=TEXT, weight="700", anchor="middle")
    return body_h, draw

def make_tiles(tiles, footer_bar_label):
    # Big count tiles so a zero is legible at a glance (never a bar drawn to zero), with a
    # full-width proof bar underneath filled to 100% (the positive framing of the same fact).
    tile_h = 92
    gap = 16
    body_h = tile_h + 14 + 26
    def draw(cx, by0, cw, accent, accent2):
        x0 = cx + CPAD
        avail = cw - 2 * CPAD
        n = len(tiles)
        tw = (avail - gap * (n - 1)) / n
        for i, (big, l1, l2) in enumerate(tiles):
            tx = x0 + i * (tw + gap)
            rrect(tx, by0, tw, tile_h, 12, BG, stroke=BORDER)
            text(tx + tw / 2, by0 + 50, big, size=44, fill=accent, weight="800", anchor="middle")
            text(tx + tw / 2, by0 + 71, l1, size=13, fill=TEXT, weight="700", anchor="middle", font=SANS)
            text(tx + tw / 2, by0 + 87, l2, size=11, fill=FAINT, anchor="middle", font=SANS)
        bar_y = by0 + tile_h + 14
        rrect(x0, bar_y, avail, 26, 6, TRACK)
        rrect(x0, bar_y, avail, 26, 6, grad(accent, accent2))
        text(x0 + avail / 2, bar_y + 17.5, footer_bar_label, size=12.5, fill=INK_ON_BAR, weight="700", anchor="middle", font=SANS)
    return body_h, draw

def header():
    text(PAD, 52, "FUSE", size=23, fill=EMBER, weight="800", spacing="1.5")
    text(PAD + 78, 52, "/  MCP Server for AI Coding Agents on .NET", size=13, fill=MUTED, font=SANS, weight="600")
    text(PAD, 92, "Measured on Real .NET Codebases.", size=30, fill=TEXT, weight="800", spacing="-0.5")
    lines = [
        "Fuse typechecks a proposed edit against the .NET compiler before an agent writes it, resolves how the code is",
        "actually wired from a Roslyn graph, and scopes a change to its blast radius. Every answer carries a grade, and",
        "Fuse abstains when it cannot answer at compiler grade.",
    ]
    for i, ln in enumerate(lines):
        text(PAD, 120 + i * 19, ln, size=13.5, fill=MUTED, font=SANS)
    ly = 120 + len(lines) * 19 + 8
    rrect(PAD, ly - 11, 13, 13, 3, BASE)
    text(PAD + 20, ly, "Baseline (bare tools, grep)", size=12, fill=MUTED, font=SANS)
    rrect(PAD + 232, ly - 11, 13, 13, 3, EMBER)
    text(PAD + 252, ly, "Fuse", size=12, fill=MUTED, font=SANS)
    text(PAD + 296, ly, "Numbers from tests/benchmarks/results; fuse eval checkgate | loop | semantics | review.", size=11, fill=FAINT, font=SANS)
    return ly + 22

def compose():
    y = header()

    body_h, draw = make_tiles(
        [("0", "false green", "broken edit called clean"),
         ("0", "false red", "clean edit called broken"),
         ("1,000", "labeled edits", "500 breaking, 500 neutral")],
        "1,000 of 1,000 compiler verdicts correct, plus 8 curated cases")
    y = draw_card(
        y, "01", "Verifying an Edit Honestly",
        "fuse_check over compiler-labeled single-file edits: does it ever call a broken edit clean?",
        ["Roslyn rewriters generate breaking and neutral edits; the compiler labels each one, not a human.",
         "A false green (a broken edit called clean) would let an agent commit a build break. The dangerous one."],
        "Zero false green and zero false red over 1,000 compiler-verified mutation edits (500 breaking, 500 neutral) plus 8 curated cases. When it cannot answer at compiler grade, fuse_check abstains (checkgate.json).",
        body_h, draw, accent=TEAL, accent2=TEAL2)

    body_h, draw = make_dotwhisker(
        [("Fuse", 89, "89%", (82, 95), 8),
         ("Native tools", 82, "82%", (74, 90), 9)])
    y = draw_card(
        y, "02", "Finishing the Task",
        "True pass@1 from a gold-test oracle: does the agent's finished edit actually pass the changed tests?",
        ["One driver model (claude-sonnet-4-6), 234 scored rollouts, reported with confidence intervals.",
         "95% CI: Fuse 82-95, native 74-90. Build round-trips were about equal (3.1 vs 3.2); that miss is shown, not hidden."],
        "Scored by the tests, not the transcript: Fuse 89% true pass@1 to native's 82%, with fewer silent wrong answers (false-done 8 vs 9). Build round-trips did not drop, and that is reported too (loop.json).",
        body_h, draw, accent=EMBER, accent2=EMBER2, pill_label="LIMITED SAMPLE")

    body_h, draw = make_bars(
        [("Edge recall", 100, "100%", "fuse"),
         ("Edge precision", 100, "100%", "fuse")],
        100)
    y = draw_card(
        y, "03", "Resolving .NET Wiring",
        "The extracted semantic graph versus hand-built edge ground truth on the wiring fixture.",
        ["DI registration and constructor injection, MediatR request-to-handler, ASP.NET route-to-action, options binding.",
         "Deterministic: the edges are read from Roslyn. No model is involved, and the same index gives the same answer."],
        "Every wiring edge in the fixture resolved correctly: 24 of 24, recall and precision 1.0 across the wiring catalog (semantics.json).",
        body_h, draw, accent=TEAL, accent2=TEAL2)

    body_h, draw = make_bars(
        [("Changed-file recall", 100, "100%", "fuse"),
         ("Blast-radius precision", 93, "93%", "fuse"),
         ("grep recall", 67, "67%", "base"),
         ("grep precision", 8, "8%", "base")],
        100, base_tag="grep baseline")
    y = draw_card(
        y, "04", "Scoping a Change",
        "fuse review over 69 real merged pull requests from pinned open-source .NET repositories, 25,000-token budget.",
        ["Changed files are must-keep; the semantic blast radius is added: callers, DI consumers, handlers, tests.",
         "The whole review arrives in a median 1,026 tokens. Recall is 100% by construction, so precision is the signal."],
        "100% of changed files kept at 93.4% precision, a median 1,026 returned tokens per review; a grep baseline reaches 67% recall at 8% precision (review.json).",
        body_h, draw, accent=EMBER, accent2=EMBER2)

    return y

total = compose()
H = total + 30
head = [
    f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" font-family="{SANS}">',
    f'<rect x="0" y="0" width="{W}" height="{H}" fill="{BG}"/>',
]
foot = [
    f'<text x="{PAD}" y="{H-20}" font-family="{MONO}" font-size="11" fill="{FAINT}">'
    f'github.com/Litenova-Solutions/Fuse  .  Apache 2.0  .  o200k_base tokens  .  four suites reproducible via fuse eval, tests/benchmarks/results</text>',
    "</svg>",
]
path = sys.argv[1] if len(sys.argv) > 1 else "fuse-benchmarks.svg"
with open(path, "w", encoding="utf-8") as f:
    f.write("\n".join(head + el_list + foot))
print("wrote", path, "size", W, "x", H)
