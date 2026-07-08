#!/usr/bin/env python3
"""Generate the Fuse benchmark figure (self-contained SVG) for the README and social sharing.

Pure Python stdlib, no dependencies. Light theme, one card per section, each pairing the
"without Fuse" baseline with the Fuse result and stating the improvement. All four sections
are measured and reproducible (tests/benchmarks/results, docs/project/benchmarks.md): sections 4
and 5 are the layer-4 context-acquisition scenario. Edit the data in compose() or the theme, re-run:

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

def card(y, num, title, subtitle, subtexts, rows, dmax, conclusion, *, fuse=EMBER, fuse2=EMBER2, illustrative=False):
    cx, cw = PAD, W - 2 * PAD
    o_title = 38
    o_sub = o_title + 22
    o_sub0 = o_sub + 21
    st_h = 17
    o_bars = o_sub0 + len(subtexts) * st_h + 12
    bars_h = len(rows) * (BAR_H + ROW_GAP) - ROW_GAP
    o_concl = o_bars + bars_h + 18
    # Conclusion text wraps inside the result box; the box grows with the line count
    # so long conclusions (cards 04 and 05) stay within the border instead of bleeding past it.
    concl_text_x = CPAD + 78
    concl_avail = cw - 2 * CPAD - 78 - 16
    concl_lines = wrap(conclusion, concl_avail, size=13.5)
    concl_lh = 18
    concl_h = max(38, 14 + len(concl_lines) * concl_lh)
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
    # Vertically center the wrapped block within the result box.
    block_top = cyc + (concl_h - len(concl_lines) * concl_lh) / 2 + 14
    text(cx + CPAD + 18, block_top - 1, "Result", size=10.5, fill=fuse, weight="700", spacing="0.08em")
    for i, ln in enumerate(concl_lines):
        text(cx + concl_text_x, block_top - 1 + i * concl_lh, ln, size=13.5, fill=TEXT, font=SANS, weight="600")
    return y + card_h + CARD_GAP

def header():
    text(PAD, 52, "FUSE", size=23, fill=EMBER, weight="800", spacing="1.5")
    text(PAD + 78, 52, "/  Roslyn-Backed .NET Semantic Context Engine for AI Agents", size=13, fill=MUTED, font=SANS, weight="600")
    text(PAD, 92, "Resolve What Actually Runs.", size=30, fill=TEXT, weight="800", spacing="-0.5")
    lines = [
        "Fuse keeps a warm, Roslyn-backed semantic index of a .NET workspace and serves precise, provenance-backed",
        "context from it: which implementation is injected, which endpoint handles a route, which handler processes a",
        "request, and what a git diff semantically impacts. Token reduction is how it renders, not what it is.",
    ]
    for i, ln in enumerate(lines):
        text(PAD, 120 + i * 19, ln, size=13.5, fill=MUTED, font=SANS)
    ly = 120 + len(lines) * 19 + 8
    rrect(PAD, ly - 11, 13, 13, 3, BASE)
    text(PAD + 20, ly, "Baseline (bare tools)", size=12, fill=MUTED, font=SANS)
    rrect(PAD + 188, ly - 11, 13, 13, 3, EMBER)
    text(PAD + 208, ly, "Fuse", size=12, fill=MUTED, font=SANS)
    text(PAD + 250, ly, "Numbers from tests/benchmarks/results; fuse eval semantics|review|localize|agent.", size=11, fill=FAINT, font=SANS)
    return ly + 22

def compose():
    y = header()
    y = card(
        y, "01", "Resolving .NET Wiring",
        "The extracted semantic graph versus hand-built edge ground truth on the wiring fixture.",
        ["DI registration and constructor injection, MediatR request-to-handler, ASP.NET route-to-action, options binding.",
         "Deterministic: the edges are read from Roslyn, not guessed. This is the moat."],
        [("Edge recall", 100, "100%", "fuse"),
         ("Edge precision", 100, "100%", "fuse")],
        100,
        "Every wiring edge in the fixture resolved correctly: 23 of 23, recall and precision 1.0 (Suite A).",
        fuse=TEAL, fuse2=TEAL2)
    y = card(
        y, "02", "Scoping a Change",
        "fuse review over 53 real merged pull requests, 25,000-token budget.",
        ["Changed files are kept as must-keep and the semantic blast radius is added: callers, DI consumers, handlers, tests.",
         "Recall is read with tokens: the whole review arrives in a median 958 tokens."],
        [("Changed-file recall", 100, "100%", "fuse"),
         ("Blast-radius precision", 80, "80%", "fuse")],
        100,
        "100% of changed files kept at 79.8% precision, a median 958 returned tokens per review; a grep baseline reaches 53% recall at 14% precision (Suite B).")
    y = card(
        y, "03", "Localizing From a Task Title",
        "fuse localize recall by title signal, no git base, 53 PRs on the rebuilt corpus, dense on by default.",
        ["The hardest mode: a sentence with no diff. Identifier-rich titles localize; vague ones do not.",
         "On a no-signal title Fuse refuses and hands back a navigation map instead of guessing (correct-refusal 100%)."],
        [("Identifier-rich titles", 21, "21%", "fuse"),
         ("Natural-language titles", 17, "17%", "fuse"),
         ("Overall", 15, "15%", "fuse")],
        100,
        "About 15% overall recall (21% on identifier-rich titles), offline and dense by default. The weakest mode, bounded by a mostly-syntax corpus, reported straight (Suite C).")
    y = card(
        y, "04", "Helping a Real Agent",
        "Claude Code (sonnet-4-6) gathering a change's files: bare tools versus the Fuse MCP. 12 PRs.",
        ["Model-dependent and a small sample (wide confidence interval); read recall together with tokens.",
         "Tokens are now comparable across arms, so the difference is recall: the Fuse arm reaches more of the files."],
        [("Fuse MCP recall", 30, "30%", "fuse"),
         ("Bare tools recall", 26, "26%", "base")],
        100,
        "Fuse reached 30% of a change's files versus 26% for bare tools, at comparable token cost (median ~211K vs ~209K), on a small, model-dependent 12-PR sample (Suite D).",
        fuse=TEAL, fuse2=TEAL2, illustrative=True)
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
