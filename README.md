# Fuse

Fuse collects source files from a project directory, reduces their content, and emits a single token-efficient output suitable for LLM context windows, documentation, and code review.

It runs as a .NET global tool (`fuse`) or as an [MCP server](docs/mcp-integration.md) for AI-assisted workflows.

## Quick start

**Prerequisites:** [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later.

```bash
# Install from source (Windows)
install.bat

# Or manually
dotnet pack src/Fuse.Cli/Fuse.Cli.csproj -c Release
dotnet tool install -g Fuse --add-source src/Fuse.Cli/nupkg

# Fuse a .NET project
fuse dotnet --directory ./src

# Maximum C# token reduction
fuse dotnet --directory ./src --all
```

Output is written to `Documents/Fuse` by default. Use `--output` and `--name` to control the destination.

## Commands

| Command | Purpose |
|---------|---------|
| `fuse` | Generic fusion. All extensions unless you set `--only-extensions`. |
| `fuse dotnet` | .NET projects: C# reduction, XML/HTML/Razor minification, DotNet template defaults. |
| `fuse wiki` | Azure DevOps wikis: Markdown only. |
| `fuse serve` | Start the MCP server on stdio. |

Full option lists: [CLI reference](docs/cli-reference.md).

## What Fuse does

Fusion is a three-step pipeline:

1. **Collection** — Scan the source directory, apply filters (extensions, `.gitignore`, binary detection, test projects, globs), and produce a list of source files.
2. **Reduction** — Normalize whitespace and run file-type reducers (C#, HTML, JSON, YAML, and others).
3. **Emission** — Write fused output with token counting, optional splitting, and size-based ordering (largest files first).

### Templates

26 project templates define default extensions and exclusions (DotNet, Python, Go, Rust, Infrastructure, and others). Subcommands apply a template automatically; the generic `fuse` command does not preset one.

Template details: [templates.md](docs/templates.md).

### Token management

- Counts tokens with the `cl100k_base` encoder (GPT-4 / GPT-3.5 class models).
- `--max-tokens` stops emission at a hard limit.
- `--split-tokens` splits into multiple files (default threshold: 800,000).
- `--track-top-token-files` reports the five largest token consumers.

### Filtering

Respects `.gitignore`, skips binary files (null-byte detection), empty files, and auto-generated markers, and supports directory, filename, and glob exclusions. Trivial content (`{}`, `[]`, whitespace-only) is dropped before emission.

## Output format

Each file is wrapped in a path-tagged block:

```xml
<file path="src/Services/OrderService.cs">
public class OrderService { }
</file>
```

Disk output filenames include a token estimate, for example `MyProject_2026-06-19_0130_22k.txt`. Multi-part runs add `_partN` segments.

## MCP integration

Run `fuse serve` and connect from VS Code, Claude Desktop, or any MCP client.

| Tool | CLI equivalent |
|------|----------------|
| `fuse_dotnet` | `fuse dotnet` (always in-memory) |
| `fuse_generic` | Generic fusion with optional template |

Passive reads: `fuse://{template}/{path}` (e.g. `fuse://dotnet/src`).

Setup and parameters: [mcp-integration.md](docs/mcp-integration.md).

## Repository layout

```
src/
  Fuse.Collection/   File discovery, filters, templates
  Fuse.Reduction/    Content reducers and normalization
  Fuse.Emission/     Output writers, token budget
  Fuse.Fusion/       Orchestration and DI
  Fuse.Cli/          CLI and MCP server
tests/               Unit and integration tests
docs/                Full documentation
```

Architecture and design rationale: [architecture.md](docs/architecture.md).

## Development

```bash
dotnet build Fuse.sln --configuration Release
dotnet test Fuse.sln --configuration Release --no-build
dotnet format Fuse.sln --verify-no-changes
```

Contribution workflow and coding standards: [contributing.md](docs/contributing.md).

## Documentation

| Guide | Contents |
|-------|----------|
| [Getting started](docs/getting-started.md) | Install, first run, common workflows |
| [CLI reference](docs/cli-reference.md) | Every command and flag |
| [MCP integration](docs/mcp-integration.md) | Server setup, tools, resources |
| [Templates](docs/templates.md) | Per-template extensions and exclusions |
| [Extending Fuse](docs/extending.md) | Add a template or reducer |
| [Architecture](docs/architecture.md) | Pipeline design and type model |

## License

MIT
