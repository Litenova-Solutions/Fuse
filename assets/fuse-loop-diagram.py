#!/usr/bin/env python3
"""Generate the Fuse verify-loop diagram (self-contained, light/dark-adaptive SVG).

Pure Python stdlib. The diagram shows the graded verification paths: oracle grade uses a
resident or captured compilation, build grade runs a scoped project build, and abstention
reports that neither path can run. Repair packets appear only for supported diagnostics.
fuse_test either runs selected covering test types or returns selection-only. ASCII text only.

    python fuse-loop-diagram.py                 # writes fuse-loop-diagram.svg
    python fuse-loop-diagram.py path/to/x.svg   # custom path

Light/dark is driven by a prefers-color-scheme media query inside the SVG, so the same file
adapts on the fuse.codes landing page and in GitHub markdown (rendered via <img>).
"""
import sys
from html import escape

W, H = 1000, 680
el_list = []
def el(s): el_list.append(s)

SANS = "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif"
MONO = "ui-monospace,'Cascadia Code','SF Mono','JetBrains Mono',Consolas,monospace"

STYLE = """
<style>
  :root{
    --bg:#ffffff; --panel:#f7f8fb; --border:#e5e8ef; --ink:#171a21; --muted:#566072;
    --faint:#8a93a3; --brand:#6d4aff; --brand-soft:#efeaff; --brand-bd:#c9bcff;
    --dim:#c0c7d4; --dim-bg:#f1f3f7;
  }
  @media (prefers-color-scheme: dark){
    :root{
      --bg:#0d1117; --panel:#161b22; --border:#30363d; --ink:#e6edf3; --muted:#9aa5b1;
      --faint:#7d8590; --brand:#a78bfa; --brand-soft:#211b34; --brand-bd:#5b4b8a;
      --dim:#484f58; --dim-bg:#161b22;
    }
  }
  .bg{fill:var(--bg);}
  .box{fill:var(--panel);stroke:var(--border);stroke-width:1.5;}
  .box-fuse{fill:var(--brand-soft);stroke:var(--brand-bd);stroke-width:1.5;}
  .box-dim{fill:var(--dim-bg);stroke:var(--border);stroke-width:1.5;stroke-dasharray:6 5;}
  .ink{fill:var(--ink);}
  .muted{fill:var(--muted);}
  .faint{fill:var(--faint);}
  .brand{fill:var(--brand);}
  .arrow{stroke:var(--muted);stroke-width:2;fill:none;}
  .arrowhead{fill:var(--muted);}
  .loop{stroke:var(--brand);stroke-width:2;fill:none;}
  .loophead{fill:var(--brand);}
  .dimstroke{stroke:var(--dim);stroke-width:2;fill:none;}
</style>
"""

def text(x, y, s, *, size=13, cls="ink", font=SANS, anchor="start", weight="400", spacing=None):
    sp = f' letter-spacing="{spacing}"' if spacing else ""
    el(f'<text x="{x:.1f}" y="{y:.1f}" font-family="{font}" font-size="{size}" class="{cls}" '
       f'text-anchor="{anchor}" font-weight="{weight}"{sp}>{escape(s)}</text>')

def box(x, y, w, h, cls="box"):
    el(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" rx="12" class="{cls}"/>')

def arrow_down(x, y1, y2, cls="arrow", head="arrowhead"):
    el(f'<line x1="{x:.1f}" y1="{y1:.1f}" x2="{x:.1f}" y2="{y2-7:.1f}" class="{cls}"/>')
    el(f'<polygon class="{head}" points="{x-5:.1f},{y2-8:.1f} {x+5:.1f},{y2-8:.1f} {x:.1f},{y2:.1f}"/>')

def arrow_right(x1, x2, y, cls="arrow", head="arrowhead"):
    el(f'<line x1="{x1:.1f}" y1="{y:.1f}" x2="{x2-7:.1f}" y2="{y:.1f}" class="{cls}"/>')
    el(f'<polygon class="{head}" points="{x2-8:.1f},{y-5:.1f} {x2-8:.1f},{y+5:.1f} {x2:.1f},{y:.1f}"/>')

def arrow_left(x1, x2, y, cls="arrow", head="arrowhead"):
    el(f'<line x1="{x1:.1f}" y1="{y:.1f}" x2="{x2+7:.1f}" y2="{y:.1f}" class="{cls}"/>')
    el(f'<polygon class="{head}" points="{x2+8:.1f},{y-5:.1f} {x2+8:.1f},{y+5:.1f} {x2:.1f},{y:.1f}"/>')

def compose():
    el(f'<rect x="0" y="0" width="{W}" height="{H}" class="bg"/>')

    # heading
    text(60, 54, "The verify loop", size=24, cls="ink", weight="800", spacing="-0.3")
    text(60, 80, "Each answer names its verification grade. The fallback path may run a scoped project build.",
         size=13.5, cls="muted")

    lx, lw = 60, 360
    rx, rw = 590, 350
    cx = lx + lw / 2

    # 1. propose
    box(lx, 110, lw, 56)
    text(cx, 144, "Agent proposes an edit", size=16, cls="ink", weight="700", anchor="middle")

    # 2. fuse_check
    arrow_down(cx, 166, 196)
    box(lx, 196, lw, 78, cls="box-fuse")
    text(lx + 22, 228, "fuse_check", size=17, cls="brand", weight="800", font=MONO)
    text(lx + 22, 252, "typecheck one proposed file before it lands", size=12.5, cls="muted")

    # Conditional repair loop belongs to diagnostics from fuse_check.
    box(rx, 196, rw, 78)
    text(rx + 20, 225, "Repair packet, when supported", size=15, cls="ink", weight="700")
    text(rx + 20, 250, "API-shape diagnostics may include an applicable fix.", size=11.5, cls="muted")
    arrow_right(lx + lw, rx, 218, cls="loop", head="loophead")
    arrow_left(rx, lx + lw, 258, cls="loop", head="loophead")
    text(505, 210, "diagnostic", size=11, cls="brand", anchor="middle")
    text(505, 274, "apply and re-check", size=11, cls="brand", anchor="middle")

    # Three honest outcomes from fuse_check.
    outcomes = [
        (60, "ORACLE", "captured or resident compilation", "no build"),
        (370, "SCOPED BUILD", "owning project via dotnet build", "build-grade fallback"),
        (680, "ABSTAIN", "compiler and toolchain unavailable", "no clean verdict"),
    ]
    for ox, title, line1, line2 in outcomes:
        box(ox, 310, 260, 88, cls="box")
        text(ox + 18, 338, title, size=12, cls="brand", weight="800", font=MONO)
        text(ox + 18, 362, line1, size=11.5, cls="muted")
        text(ox + 18, 381, line2, size=11.5, cls="faint")
    el('<path d="M240 274 V290 H810" class="arrow"/>')
    arrow_down(240, 290, 310)
    arrow_down(500, 290, 310)
    arrow_down(810, 290, 310)

    # Test selection has two outcomes.
    box(lx, 420, lw, 76, cls="box-fuse")
    text(lx + 22, 450, "fuse_test", size=17, cls="brand", weight="800", font=MONO)
    text(lx + 22, 474, "select tests linked to the changed symbol", size=12.5, cls="muted")
    arrow_down(190, 398, 420)
    el('<path d="M500 398 V408 H290 V413" class="arrow"/>')
    el('<polygon class="arrowhead" points="285,412 295,412 290,420"/>')
    arrow_down(cx, 496, 530)
    box(60, 530, 410, 70)
    text(80, 558, "COVERAGE FOUND", size=12, cls="brand", weight="800", font=MONO)
    text(80, 582, "run the selected test types at build grade", size=12.5, cls="muted")
    box(530, 530, 410, 70)
    text(550, 558, "NO COVERAGE EDGE", size=12, cls="brand", weight="800", font=MONO)
    text(550, 582, "selection-only; no whole-suite fallback", size=12.5, cls="muted")

    box(60, 620, W - 120, 40, cls="box-dim")
    text(80, 645, "Fuse adds graded checks and targeted selection; normal builds and broader test runs remain available.",
         size=12.5, cls="faint")

    return

def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-loop-diagram.svg"
    head = (f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
            f'viewBox="0 0 {W} {H}" font-family="{SANS}">{STYLE}')
    with open(path, "w", encoding="utf-8") as f:
        f.write(head + "".join(el_list) + "</svg>")
    print("wrote", path, W, "x", H)

main()
