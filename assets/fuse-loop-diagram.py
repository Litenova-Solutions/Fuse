#!/usr/bin/env python3
"""Generate the Fuse verify-loop diagram (self-contained, light/dark-adaptive SVG).

Pure Python stdlib. The diagram shows the loop that no bar chart shows: an agent proposes
an edit, fuse_check answers with diagnostics and a repair packet before the edit lands, the
agent repairs and re-checks, then fuse_test runs only the covering tests, and it is done, with the
full dotnet build round-trip drawn as the path this replaces. Verbs match the shipped v4
tool surface (fuse_check, fuse_test). ASCII text only. Re-run:

    python fuse-loop-diagram.py                 # writes fuse-loop-diagram.svg
    python fuse-loop-diagram.py path/to/x.svg   # custom path

Light/dark is driven by a prefers-color-scheme media query inside the SVG, so the same file
adapts on the fuse.codes landing page and in GitHub markdown (rendered via <img>).
"""
import sys
from html import escape

W, H = 1000, 600
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
    text(60, 80, "fuse_check answers with the compiler before the edit lands, so the agent repairs from real diagnostics without a build.",
         size=13.5, cls="muted")

    lx, lw = 60, 360           # left column boxes
    rx, rw = 590, 350          # repair box (right)
    cx = lx + lw / 2

    # 1. propose
    box(lx, 110, lw, 56)
    text(cx, 144, "Agent proposes an edit", size=16, cls="ink", weight="700", anchor="middle")

    # 2. fuse_check
    arrow_down(cx, 166, 196)
    box(lx, 196, lw, 92, cls="box-fuse")
    text(lx + 22, 228, "fuse_check", size=17, cls="brand", weight="800", font=MONO)
    text(lx + 22, 252, "typecheck the proposed edit before it lands;", size=12.5, cls="muted")
    text(lx + 22, 270, "oracle grade: checked against the real compilation, no build.", size=12.5, cls="muted")

    # loop with the repair box
    box(rx, 200, rw, 84)
    text(rx + 22, 232, "Agent applies the repair", size=16, cls="ink", weight="700")
    text(rx + 22, 256, "the repair packet names the fix", size=12.5, cls="muted")
    text(rx + 22, 273, "(the member that exists, the type to use).", size=12.5, cls="muted")
    arrow_right(lx + lw, rx, 224, cls="loop", head="loophead")
    text((lx + lw + rx) / 2, 216, "diagnostics + repair packet", size=11.5, cls="brand", anchor="middle", weight="600")
    arrow_left(rx, lx + lw, 260, cls="loop", head="loophead")
    text((lx + lw + rx) / 2, 278, "apply, re-check", size=11.5, cls="brand", anchor="middle", weight="600")

    # 3. fuse_test
    arrow_down(cx, 288, 330)
    text(cx + 14, 313, "clean", size=11.5, cls="faint", anchor="start")
    box(lx, 330, lw, 72, cls="box-fuse")
    text(lx + 22, 361, "fuse_test", size=17, cls="brand", weight="800", font=MONO)
    text(lx + 22, 385, "run only the covering tests, not the whole suite.", size=12.5, cls="muted")

    # 4. done
    arrow_down(cx, 402, 438)
    text(cx + 14, 424, "green", size=11.5, cls="faint", anchor="start")
    box(lx, 438, lw, 56)
    text(cx, 472, "Done: the edit is verified", size=16, cls="ink", weight="700", anchor="middle")

    # avoided path (dim)
    py = 524
    box(60, py, W - 120, 52, cls="box-dim")
    text(80, py + 22, "The round-trip this replaces", size=12.5, cls="faint", weight="700")
    text(80, py + 40, "edit, dotnet build the solution, read the errors, edit again, dotnet test the whole suite; repeat until green.",
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
