#!/usr/bin/env python3
"""Generate a responsive light/dark SVG of concrete OrderingApp wiring."""
import sys
from html import escape

W, H = 1000, 520
SANS = "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif"
MONO = "ui-monospace,'Cascadia Code','SF Mono',Consolas,monospace"
items = []

STYLE = """
<style>
:root{--bg:#fff;--panel:#f7f8fb;--border:#dfe3eb;--ink:#171a21;--muted:#566072;--brand:#6d4aff;--soft:#efeaff;--line:#8b79e8}
@media(prefers-color-scheme:dark){:root{--bg:#0d1117;--panel:#161b22;--border:#30363d;--ink:#e6edf3;--muted:#9aa5b1;--brand:#a78bfa;--soft:#211b34;--line:#8b78d3}}
.bg{fill:var(--bg)}.panel{fill:var(--panel);stroke:var(--border);stroke-width:1.5}.source{fill:var(--soft);stroke:var(--line);stroke-width:1.5}
.ink{fill:var(--ink)}.muted{fill:var(--muted)}.brand{fill:var(--brand)}.edge{stroke:var(--line);stroke-width:2;fill:none}.head{fill:var(--line)}
</style>
"""


def text(x, y, value, size=13, cls="ink", font=SANS, weight="400", anchor="start"):
    items.append(
        f'<text x="{x}" y="{y}" font-family="{font}" font-size="{size}" '
        f'class="{cls}" font-weight="{weight}" text-anchor="{anchor}">{escape(value)}</text>'
    )


def box(x, y, w, h, cls="panel"):
    items.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" class="{cls}"/>')


def arrow(x1, y1, x2, y2):
    items.append(f'<line x1="{x1}" y1="{y1}" x2="{x2 - 9}" y2="{y2}" class="edge"/>')
    items.append(f'<polygon points="{x2 - 10},{y2 - 5} {x2 - 10},{y2 + 5} {x2},{y2}" class="head"/>')


def node(x, y, title, detail, target):
    box(x, y, 260, 72, "source")
    text(x + 18, y + 28, title, 13, "brand", MONO, "700")
    text(x + 18, y + 51, detail, 11.5, "muted", MONO)
    arrow(x + 260, y + 36, 550, y + 36)
    box(550, y, 390, 72)
    text(570, y + 29, target, 14, "ink", MONO, "700")


def compose():
    items.append(f'<rect width="{W}" height="{H}" class="bg"/>')
    text(50, 50, "Typed wiring, resolved to code", 25, "ink", SANS, "800")
    text(50, 78, "Concrete edges from the OrderingApp fixture. Roslyn extracts them without a model.", 13.5, "muted")

    node(50, 110, "DI SERVICE", "AddScoped<IOrderService,...>", "IOrderService  ->  OrderService")
    text(570, 164, "Program.cs -> Ordering/OrderService.cs", 11.5, "muted", MONO)

    node(50, 200, "MEDIATR REQUEST", "CreateOrderCommand", "CreateOrderCommand  ->  CreateOrderHandler")
    text(570, 254, "request type -> IRequestHandler implementation", 11.5, "muted", MONO)

    node(50, 290, "CONFIG SECTION", 'GetSection("Orders")', "Orders  ->  OrderOptions")
    text(570, 344, "configuration key -> bound options type", 11.5, "muted", MONO)

    node(50, 380, "HTTP ROUTE", 'MapGet("/health", ...)', "/health  ->  HealthEndpoints.Check")
    text(570, 434, "route pattern -> endpoint method", 11.5, "muted", MONO)

    box(50, 470, 890, 32)
    text(495, 491, "Each edge retains source-file and symbol provenance in the local index.", 12, "muted", SANS, "600", "middle")


def main():
    compose()
    path = sys.argv[1] if len(sys.argv) > 1 else "fuse-typed-wiring.svg"
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="auto" '
        f'viewBox="0 0 {W} {H}" role="img" aria-label="Concrete typed wiring from the OrderingApp fixture">'
        + STYLE + "".join(items) + "</svg>"
    )
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(svg)
    print("wrote", path, W, "x", H)


main()
