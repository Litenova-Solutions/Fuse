import cytoscape from "cytoscape";
import { GraphDto } from "../host/protocol";

// The graph webview script. It runs inside the VS Code webview (browser context), receives a GraphDto from the
// extension host over postMessage, and renders it with Cytoscape (bundled, no CDN). Nodes are sized by
// centrality and colored by token cost; clicking a node asks the extension to open that file.

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };

const vscodeApi = acquireVsCodeApi();

let currentDetail: GraphDto["detail"] = "Directories";

function render(graph: GraphDto): void {
  currentDetail = graph.detail;
  const maxTokens = Math.max(1, ...graph.nodes.map((n) => n.tokenCost));
  const elements: cytoscape.ElementDefinition[] = [];
  for (const node of graph.nodes) {
    elements.push({
      data: {
        id: node.path,
        label: node.declaredTypes[0] ?? node.path,
        size: 20 + Math.round(node.centrality * 60),
        // When a scope is active, color by role so the user sees what a fusion includes; otherwise map token
        // cost to a hue from green (cheap) to red (expensive) for at-a-glance budget reading.
        color: node.role
          ? roleColor(node.role)
          : `hsl(${Math.round(120 - (120 * node.tokenCost) / maxTokens)}, 70%, 45%)`,
      },
    });
  }
  for (const edge of graph.edges) {
    elements.push({ data: { id: `${edge.from}->${edge.to}`, source: edge.from, target: edge.to } });
  }

  const cy = cytoscape({
    container: document.getElementById("graph"),
    elements,
    style: [
      {
        selector: "node",
        style: {
          width: "data(size)",
          height: "data(size)",
          "background-color": "data(color)",
          label: "data(label)",
          "font-size": "8px",
          color: "#ccc",
        },
      },
      {
        selector: "edge",
        style: { width: 1, "line-color": "#555", "target-arrow-color": "#555", "target-arrow-shape": "triangle", "curve-style": "bezier" },
      },
    ],
    layout: { name: "cose", animate: false },
  });

  cy.on("tap", "node", (event) => {
    // At the directory level a tap expands the supernode into its files; at the file level it opens the file.
    const type = currentDetail === "Directories" ? "expand" : "open";
    vscodeApi.postMessage({ type, path: event.target.id() });
  });
}

// Role colors for the scope overlay: a seed is the anchor, a changed file is the diff target, a dependency is
// pulled in by the graph. Distinct hues so the included set reads at a glance.
function roleColor(role: string): string {
  switch (role) {
    case "Seed":
      return "#4080ff";
    case "Changed":
      return "#e0852f";
    default:
      return "#9aa0a6";
  }
}

window.addEventListener("message", (event: MessageEvent) => {
  const message = event.data as { type: string; graph?: GraphDto };
  if (message.type === "graph" && message.graph) {
    render(message.graph);
  }
});

// Tell the extension the webview is ready to receive the graph.
vscodeApi.postMessage({ type: "ready" });
