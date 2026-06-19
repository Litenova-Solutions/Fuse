# Fuse

Fuse is a deep .NET-native context optimizer for AI-assisted development. It collects source files, reduces them for token efficiency, and emits a single structured output that agents can consume in one call instead of reading thousands of files individually.

Unlike generic repo packers (Repomix, Code2Prompt, Gitingest), Fuse understands C# structure: dependency graphs, skeleton extraction, BM25 query scoping, git change detection, and convention patterns. It ships as a .NET global tool (`fuse`) and as an MCP server for Cursor, Claude Code, and GitHub Copilot.

Maintained by [Litenova Solutions](https://github.com/Litenova-Solutions).

## Install

**Prerequisites:** [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later.

### From NuGet

```bash
dotnet tool install -g Fuse
```

Run without a global install via the .NET 10 SDK:

```bash
dnx Fuse -- serve
```

### From source (Windows)

```cmd
install.bat
```

### From source (any OS)

```bash
dotnet pack src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release
dotnet tool install -g Fuse --add-source src/Host/Fuse.Cli/nupkg
```

Verify:

```bash
fuse --help
```

## CLI quick start

Fuse a .NET project with the DotNet template:

```bash
fuse dotnet --directory ./src
```

Maximum C# token reduction:

```bash
fuse dotnet --directory ./src --all
```

Architectural overview (signatures only):

```bash
fuse dotnet --directory ./src --all --skeleton
```

PR-scoped fusion:

```bash
fuse dotnet --directory ./src --changed-since main
```

Query-scoped fusion:

```bash
fuse dotnet --directory ./src --query "payment gateway" --query-top 10
```

Initialize a project config file:

```bash
fuse init
```

Output defaults to `Documents/Fuse`. Use `--output` and `--name` to control the destination.

## Commands

| Command | Purpose |
|---------|---------|
| `fuse` | Generic fusion. All extensions unless you set `--only-extensions`. |
| `fuse dotnet` | .NET projects: C# reduction, structural maps, agentic scoping. |
| `fuse wiki` | Azure DevOps wikis: Markdown only. |
| `fuse init` | Create `fuse.json` in the current directory. |
| `fuse serve` | Start the MCP server on stdio. |

Full option lists: [CLI reference](docs/cli-reference.md).

## Recommended agent workflow

Use MCP tools in this order for large .NET codebases:

1. **Skeleton pass** (`fuse_skeleton` or `fuse dotnet --skeleton --all`) for a low-token architecture map.
2. **Drill down** with `fuse_focus` (type/file seed) or `fuse_search` (natural-language query).
3. **PR review** with `fuse_changes` (git diff scoping).
4. **Full control** with `fuse_dotnet` when you need every option combined.

See [agentic workflows](docs/agentic-workflows.md) for token budgets and composition rules.

## MCP setup

Run `fuse serve` and connect from your AI client.

### Claude Code

Add to `.mcp.json` in your project root:

```json
{
  "mcpServers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

Or: `claude mcp add fuse --scope project -- fuse serve`

### Cursor

Add to `.cursor/mcp.json`:

```json
{
  "mcpServers": {
    "fuse": {
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

### GitHub Copilot (VS Code)

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": ["serve"]
    }
  }
}
```

Tool catalog and parameters: [docs/mcp.md](docs/mcp.md). Client setup details: [docs/mcp-integration.md](docs/mcp-integration.md). MCP Registry manifest: [docs/mcp-registry/server.json](docs/mcp-registry/server.json).

## What Fuse does

Fusion is a four-stage pipeline:

1. **Collection** - Scan the source directory, apply filters (extensions, `.gitignore`, binary detection, test projects, globs).
2. **Filtering** - Optional analysis-stage scoping: focus, git changes, or BM25 query with dependency expansion.
3. **Reduction** - Normalize whitespace, run language and format reducers, apply skeleton/markers/redaction.
4. **Emission** - Write fused output with token counting, manifest header, optional splitting, and size-based ordering.

Architecture details: [docs/architecture.md](docs/architecture.md). Feature reference: [docs/features.md](docs/features.md).

## Output format

Each file is wrapped in a path-tagged block (default XML):

```xml
<file path="src/Services/OrderService.cs">
public class OrderService { }
</file>
```

Also supported: `--format markdown` and `--format json`. A manifest header (file tree and token costs) is prepended by default; use `--no-manifest` to disable.

Disk output filenames include a token estimate, for example `MyProject_2026-06-19_0130_22k.txt`.

## Repository layout

```
src/
  Core/                               Pipeline libraries
    Fuse.Collection/                  File discovery, filters, templates
    Fuse.Analysis/                    Dependency graphs, BM25 search, git stats
    Fuse.Reduction/                   Content pipeline, caching, redaction
    Fuse.Emission/                    Output writers, token budget, manifest
    Fuse.Fusion/                      Orchestration and DI
  Host/
    Fuse.Cli/                         CLI and MCP server
  Plugins/                            Extension-keyed capability providers
    Fuse.Plugins.Abstractions/        Capability interfaces (shared contract)
    Fuse.Plugins.Languages.CSharp/    C# language plugin
    Fuse.Plugins.Formats.Web/             Format reducers (HTML, JSON, YAML, etc.)
tests/                                Unit and integration tests
docs/                                 Full documentation
```

## Development

```bash
dotnet build Fuse.slnx --configuration Release
dotnet test Fuse.slnx --configuration Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

Contribution workflow: [contributing.md](docs/contributing.md). Agent instructions: [AGENTS.md](AGENTS.md).

## Documentation

| Guide | Contents |
|-------|----------|
| [Getting started](docs/getting-started.md) | Install, first run, config file |
| [CLI reference](docs/cli-reference.md) | Every command and flag |
| [MCP tool catalog](docs/mcp.md) | Tools, resources, parameters |
| [MCP integration](docs/mcp-integration.md) | Client setup and troubleshooting |
| [Agentic workflows](docs/agentic-workflows.md) | Skeleton, focus, query, change scoping |
| [Features](docs/features.md) | All tier features by category |
| [Templates](docs/templates.md) | Per-template extensions and exclusions |
| [Extending Fuse](docs/extending.md) | Language plugins and templates |
| [Architecture](docs/architecture.md) | Pipeline design and capability model |
| [Performance](docs/performance.md) | Cold start, Native AOT, benchmarking |
| [CHANGELOG](CHANGELOG.md) | Version history and migration notes |

## License

MIT. Copyright © Litenova Solutions 2026.
