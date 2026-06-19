# Getting Started with Fuse

Fuse merges source code files into a single, token-optimized output file. It is designed for preparing codebases for LLM context windows, documentation, and code analysis.

---

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later
- Windows, macOS, or Linux

---

## Install as a Global Tool

Fuse ships as a .NET global tool. After installing, the `fuse` command is available from any terminal.

### From a Published Package

When a package is published to NuGet:

```bash
dotnet tool install -g Fuse
```

### From Source (Windows)

Clone the repository, then run the install script from the repo root:

```cmd
install.bat
```

The script packs `Fuse.Cli`, uninstalls any previous global install, and installs from the local nupkg output.

### From Source (Any OS)

```bash
# Pack the tool
dotnet pack src/Fuse.Cli/Fuse.Cli.csproj -c Release

# Install globally from local source
dotnet tool install -g Fuse --add-source src/Fuse.Cli/nupkg
```

### Verify Installation

```bash
fuse --help
```

You should see the command list: default `fuse`, `fuse dotnet`, `fuse wiki`, and `fuse serve`.

---

## Build from Source

To work on Fuse without installing the global tool:

```bash
git clone https://github.com/your-org/Fuse.git
cd Fuse
dotnet build Fuse.sln
dotnet run --project src/Fuse.Cli/Fuse.Cli.csproj -- --help
```

To build, test, and check formatting the way CI does:

```bash
dotnet restore Fuse.sln
dotnet build Fuse.sln --configuration Release
dotnet test Fuse.sln --configuration Release --no-build
dotnet format Fuse.sln --verify-no-changes
```

---

## Your First Fusion

The fastest path is `fuse dotnet`, which applies the DotNet template with sensible defaults for C# projects.

From your project root:

```bash
fuse dotnet --directory ./src
```

This command:

1. Scans `./src` recursively
2. Includes `.cs`, `.razor`, `.cshtml`, `.csproj`, and other DotNet template extensions
3. Skips `bin`, `obj`, `.vs`, and other build artifacts
4. Respects `.gitignore` rules
5. Writes a fused output file to your Documents/Fuse folder

### Add Token Savings

For maximum token reduction on a .NET codebase:

```bash
fuse dotnet --directory ./src --all
```

The `--all` flag removes C# comments, usings, namespaces, regions, and applies aggressive reduction.

### Specify Output Location

```bash
fuse dotnet --directory ./src --output ./output --name my-context
```

### Fuse a Non-.NET Project

Use the generic command with explicit extensions:

```bash
fuse --directory ./my-app --only-extensions .py,.md,.yaml
```

Or use the MCP `fuse_generic` tool with a template name. See [mcp-integration.md](mcp-integration.md).

### Fuse an Azure DevOps Wiki

```bash
fuse wiki --directory ./wiki-repo
```

This includes only `.md` files and excludes `.attachments`.

---

## Output Format

Fuse writes plain `.txt` files. Each source file is wrapped in XML tags:

```xml
<file path="src/Program.cs">
public class Program
{
    public static void Main() { }
}
</file>
```

Output filenames encode metadata:

| Scenario | Example |
|----------|---------|
| Default | `MyProject_2026-02-12_1430_554k.txt` |
| With `--all` | `MyProject_all_2026-02-12_1430_320k.txt` |
| Multi-part | `MyProject_part1_800k.txt`, `MyProject_part2_554k.txt` |

---

## Next Steps

- [CLI Reference](cli-reference.md) for all commands and options
- [Templates](templates.md) for per-language defaults
- [MCP Integration](mcp-integration.md) for AI agent setup
- [Architecture](architecture.md) for how the pipeline works
