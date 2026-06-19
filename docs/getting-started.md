# Getting Started with Fuse

Fuse merges source files into a single, token-optimized output for LLM context windows, documentation, and code analysis. Version 2.0 adds dependency-aware scoping, secret redaction, and a split MCP tool surface for AI agents.

---

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later
- Windows, macOS, or Linux
- Git (optional, required for change scoping and git stats)

---

## Install

Fuse ships as a .NET global tool published as `Fuse`.

### From NuGet

```bash
dotnet tool install -g Fuse
```

Run without a global install:

```bash
dnx Fuse -- --help
```

### From source (Windows)

Clone the repository and run from the repo root:

```cmd
install.bat
```

### From source (any OS)

```bash
dotnet pack src/Host/Fuse.Cli/Fuse.Cli.csproj -c Release
dotnet tool install -g Fuse --add-source src/Host/Fuse.Cli/nupkg
```

### Verify

```bash
fuse --help
```

You should see: `fuse`, `fuse dotnet`, `fuse wiki`, `fuse init`, and `fuse serve`.

---

## Build from source

To work on Fuse without installing the global tool:

```bash
git clone https://github.com/Litenova-Solutions/Fuse.git
cd Fuse
dotnet build Fuse.slnx
dotnet run --project src/Host/Fuse.Cli/Fuse.Cli.csproj -- --help
```

CI-equivalent checks:

```bash
dotnet restore Fuse.slnx
dotnet build Fuse.slnx --configuration Release
dotnet test Fuse.slnx --configuration Release --no-build
dotnet format Fuse.slnx --verify-no-changes
```

---

## Your first fusion

The fastest path for .NET projects:

```bash
fuse dotnet --directory ./src
```

This command:

1. Scans `./src` recursively with the DotNet template
2. Includes `.cs`, `.razor`, `.cshtml`, `.csproj`, and related extensions
3. Skips `bin`, `obj`, `.vs`, and other build artifacts
4. Respects `.gitignore`
5. Redacts secrets (default ON)
6. Prepends a manifest header (default ON)
7. Writes output to `Documents/Fuse`

### Maximum token savings

```bash
fuse dotnet --directory ./src --all
```

Removes C# comments, usings, namespaces, regions, and applies aggressive reduction.

### Architecture overview

```bash
fuse dotnet --directory ./src --all --skeleton
```

Emits signatures only. Typically 80-90% fewer tokens than full fusion.

### Custom output location

```bash
fuse dotnet --directory ./src --output ./output --name my-context
```

### Project config

Scaffold a config file:

```bash
fuse init
```

Edit `fuse.json` to set defaults, then run commands without repeating flags:

```bash
fuse dotnet
```

Precedence: CLI flag > config file > built-in default.

---

## Non-.NET projects

Use the generic command with a template or explicit extensions:

```bash
fuse --directory ./my-app --only-extensions .py,.md,.yaml
```

Or via MCP:

```
fuse_generic(path="./my-app", template="Python")
```

Template list: [templates.md](templates.md).

### Azure DevOps wiki

```bash
fuse wiki --directory ./wiki-repo
```

Includes only `.md` files.

---

## MCP for AI agents

Start the server:

```bash
fuse serve
```

Add to Cursor (`.cursor/mcp.json`):

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

Recommended agent workflow:

1. `fuse_skeleton` for architecture
2. `fuse_focus` or `fuse_search` to drill down
3. `fuse_changes` for PR review

Details: [mcp.md](mcp.md) and [agentic-workflows.md](agentic-workflows.md).

---

## Output format

Default output is XML with a manifest header:

```xml
<!-- fuse:manifest files=52 tokens=84000 ... -->
<file path="src/Program.cs">
public class Program { }
</file>
```

Other formats: `--format markdown` or `--format json`.

Output filenames encode metadata:

| Scenario | Example |
|----------|---------|
| Default | `MyProject_2026-06-19_1430_554k.txt` |
| With `--all` | `MyProject_all_2026-06-19_1430_320k.txt` |
| Multi-part | `MyProject_part1_800k.txt`, `MyProject_part2_554k.txt` |

---

## Migrating from Fuse 1.x

Key changes in 2.0:

- Default tokenizer is `o200k_base` (token counts will differ)
- Secret redaction and manifest header are ON by default
- MCP tools split into six focused tools

Full migration table: [CHANGELOG.md](../CHANGELOG.md).

---

## Next steps

- [CLI reference](cli-reference.md) for all commands and flags
- [Features](features.md) for tier feature overview
- [Templates](templates.md) for per-language defaults
- [MCP tool catalog](mcp.md) for agent integration
- [Architecture](architecture.md) for pipeline design
