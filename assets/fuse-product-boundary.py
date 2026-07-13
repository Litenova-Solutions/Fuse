#!/usr/bin/env python3
"""Generate a responsive light/dark SVG of Fuse's local product boundary."""
import sys
from html import escape

W, H = 1000, 560
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif"
MONO = "ui-monospace,'Cascadia Code','SF Mono',Consolas,monospace"
items = []

STYLE = """
<style>
:root{--bg:#fff;--panel:#f7f8fb;--border:#dfe3eb;--ink:#171a21;--muted:#566072;--brand:#6d4aff;--soft:#efeaff;--line:#8b79e8;--safe:#137a68}
@media(prefers-color-scheme:dark){:root{--bg:#0d1117;--panel:#161b22;--border:#30363d;--ink:#e6edf3;--muted:#9aa5b1;--brand:#a78bfa;--soft:#211b34;--line:#8b78d3;--safe:#45c7ae}}
.bg{fill:var(--bg)}.panel{fill:var(--panel);stroke:var(--border);stroke-width:1.5}.fuse{fill:var(--soft);stroke:var(--line);stroke-width:1.5}
.boundary{fill:none;stroke:var(--border);stroke-width:2;stroke-dasharray:7 6}.ink{fill:var(--ink)}.muted{fill:var(--muted)}.brand{fill:var(--brand)}
.safe{fill:var(--safe)}.edge{stroke:var(--line);stroke-width:2;fill:none}.head{fill:var(--line)}
</style>
"""


def text(x, y, value, size=13, cls="ink", font=SANS, weight="400", anchor="start"):
    items.append(
        f'<text x="{x}" y="{y}" font-family="{font}" font-size="{size}" '
        f'class="{cls}" font-weight="{weight}" text-anchor="{anchor}">{escape(value)}</text>'
    )


def box(x, y, w, h, cls="panel", dashed=False):
    extra = ' stroke-dasharray="6 5"' if dashed else ""
    items.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" class="{cls}"{extra}/>')


def arrow(x1, y1, x2, y2):
    items.append(f'<line x1="{x1}" y1="{y1}" x2="{x2 - 9}" y2="{y2}" class="edge"/>')
    items.append(f'<polygon points="{x2 - 10},{y2 - 5} {x2 - 10},{y2 + 5} {x2},{y2}" class="head"/>')


def compose():
    items.append(f'<rect width="{W}" height="{H}" class="bg"/>')
    text(50, 50, "The local product boundary", 25, "ink", SANS, "800")
    text(50, 78, "Fuse runs beside the repository. No model is fetched, required, or shipped.", 13.5, "muted")

    items.append('<rect x="35" y="100" width="930" height="410" rx="18" class="boundary"/>')
    text(58, 128, "YOUR MACHINE", 11.5, "muted", MONO, "700")

    box(70, 165, 220, 104)
    text(180, 199, "AI client", 17, "ink", SANS, "700", "middle")
    text(180, 225, "Cursor, Claude, Copilot", 12, "muted", SANS, "400", "middle")
    text(180, 248, "calls MCP tools over stdio", 11.5, "muted", MONO, "400", "middle")

    box(390, 145, 270, 270, "fuse")
    text(525, 184, "Fuse local process", 19, "brand", SANS, "800", "middle")
    text(525, 215, "collection and reduction", 12, "muted", MONO, "400", "middle")
    text(525, 241, "Roslyn syntax and semantic graph", 12, "muted", MONO, "400", "middle")
    text(525, 267, "graded compiler verification", 12, "muted", MONO, "400", "middle")
    text(525, 293, "deterministic lexical retrieval", 12, "muted", MONO, "400", "middle")
    box(425, 324, 200, 58)
    text(525, 349, ".fuse/fuse.db", 13, "ink", MONO, "700", "middle")
    text(525, 369, "local SQLite index", 11.5, "muted", SANS, "400", "middle")

    box(760, 165, 170, 104)
    text(845, 199, "Repository", 17, "ink", SANS, "700", "middle")
    text(845, 225, "source and project files", 11.5, "muted", SANS, "400", "middle")
    text(845, 248, "read for analysis", 11.5, "safe", MONO, "700", "middle")

    box(760, 315, 170, 100)
    text(845, 347, ".NET toolchain", 16, "ink", SANS, "700", "middle")
    text(845, 373, "compiler and tests", 11.5, "muted", SANS, "400", "middle")
    text(845, 395, "invoked when required", 11.5, "muted", MONO, "400", "middle")

    arrow(290, 217, 390, 217)
    arrow(660, 217, 760, 217)
    arrow(660, 365, 760, 365)

    box(70, 330, 220, 85, "panel", True)
    text(180, 358, "Working-tree writes", 14, "ink", SANS, "700", "middle")
    text(180, 382, "only fuse_workspace apply", 11.5, "brand", MONO, "700", "middle")
    text(180, 401, "dry run unless write=true", 11.5, "muted", MONO, "400", "middle")

    text(500, 540, "Local inputs stay local unless the AI client sends returned context elsewhere.", 12.5, "muted", SANS, "600", "middle")


def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-product-boundary.svg"
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="auto" '
        f'viewBox="0 0 {W} {H}" role="img" aria-label="Fuse local product boundary">'
        + STYLE + "".join(items) + "</svg>"
    )
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(svg)
    print("wrote", path, W, "x", H)


main()
