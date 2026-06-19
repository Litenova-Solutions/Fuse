# CLI Reference

Fuse is invoked as `fuse [command] [options]`. All commands share a common set of global options defined on `CommandBase`. Subcommands add template-specific or reduction-specific options.

---

## Commands Overview

| Command | Description |
|---------|-------------|
| `fuse` | Generic fusion. No template preset; scans all extensions unless `--only-extensions` is set. |
| `fuse dotnet` | Fusion optimized for .NET projects with C# reduction options. |
| `fuse wiki` | Fusion for Azure DevOps wiki repositories (Markdown only). |
| `fuse serve` | Start the MCP server on stdio for AI agent integration. |

---

## Global Options

These options are available on `fuse`, `fuse dotnet`, and `fuse wiki`.

### Directory and Output

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--directory` | | Current directory | Root directory to scan |
| `--output` | | My Documents/Fuse | Output directory for fused files |
| `--name` | | Auto-generated | Custom output filename without extension |
| `--overwrite` | | `true` | Overwrite existing output files |

### Extensions

| Option | Default | Description |
|--------|---------|-------------|
| `--include-extensions` | | Add extensions on top of template defaults (e.g., `.txt,.log`) |
| `--exclude-extensions` | | Remove extensions from template defaults (e.g., `.xml,.md`) |
| `--only-extensions` | | Process only these extensions, ignoring all template defaults. Cannot be combined with a template preset. |

### Exclusions

| Option | Default | Description |
|--------|---------|-------------|
| `--exclude-directories` | | Directory names to skip (e.g., `Migrations`, `wwwroot`) |
| `--exclude-files` | | Specific file names to exclude |
| `--exclude-patterns` | | Glob patterns to exclude (e.g., `**/Migrations/**`, `**/*.min.js`) |
| `--exclude-empty-files` | `true` | Skip zero-byte files |
| `--exclude-auto-generated` | `true` | Skip files with an auto-generated marker in the first few lines |
| `--exclude-test-projects` | `false` | Exclude all test project directories |
| `--respect-git-ignore` | `true` | Honor `.gitignore` rules in the directory tree |

### Search

| Option | Default | Description |
|--------|---------|-------------|
| `--recursive` | `true` | Search subdirectories |
| `--max-file-size` | `0` | Maximum file size in KB (`0` = unlimited) |
| `--ignore-binary` | `true` | Skip binary files |

### Content and Metadata

| Option | Default | Description |
|--------|---------|-------------|
| `--include-metadata` | `false` | Include file size and modification date in output |

### Token Management

| Option | Default | Description |
|--------|---------|-------------|
| `--max-tokens` | | Hard stop when global token count is reached |
| `--split-tokens` | `800000` | Split output into new files when this count is exceeded |
| `--show-token-count` | `true` | Display estimated token count on completion |
| `--track-top-token-files` | `false` | Display the top 5 files consuming the most tokens |

---

## fuse (Generic)

Default command. No template is applied. When no `--only-extensions` or `--include-extensions` are specified, all file extensions are eligible (`*.*`).

### Examples

```bash
# Fuse all text files in a directory
fuse --directory ./project

# Fuse only TypeScript and CSS
fuse --directory ./frontend --only-extensions .ts,.tsx,.css

# Fuse with a token limit
fuse --directory ./project --max-tokens 100000
```

---

## fuse dotnet

Applies the `DotNet` template automatically. Adds C#-specific reduction options.

### Additional Options

| Option | Default | Description |
|--------|---------|-------------|
| `--remove-csharp-namespaces` | `false` | Remove `namespace` declarations from C# files |
| `--remove-csharp-comments` | `false` | Remove single-line, multi-line, and XML doc comments |
| `--remove-csharp-regions` | `false` | Remove `#region` / `#endregion` directives |
| `--remove-csharp-usings` | `false` | Remove `using` directives |
| `--aggressive` | `false` | Remove attributes, redundant keywords, compress auto-properties |
| `--minify-xml-files` | `true` | Minify `.csproj`, `.xml`, `.props`, `.targets` |
| `--minify-html-and-razor` | `true` | Minify `.html`, `.cshtml`, `.razor` |
| `--exclude-unit-test-projects` | `false` | Exclude only unit test directories (keeps integration tests) |
| `--all` | `false` | Enable all C# reduction options at once |

### Examples

```bash
# Standard .NET fusion
fuse dotnet --directory ./src

# Maximum token savings
fuse dotnet --directory ./src --all

# Strip comments and usings only
fuse dotnet --directory ./src --remove-csharp-comments --remove-csharp-usings

# Exclude unit tests but keep integration tests
fuse dotnet --directory ./src --exclude-unit-test-projects

# Add SQL files to the DotNet template
fuse dotnet --directory ./src --include-extensions .sql

# Split large output at 500k tokens
fuse dotnet --directory ./src --split-tokens 500000
```

---

## fuse wiki

Applies the `AzureDevOpsWiki` template. Includes only `.md` files. Excludes `.git` and `.attachments`.

Accepts all global options. No additional reduction options.

### Examples

```bash
fuse wiki --directory ./wiki-repo

fuse wiki --directory ./wiki-repo --include-metadata
```

---

## fuse serve

Starts the Fuse MCP server. Communicates via stdio using JSON-RPC. All logging goes to stderr so stdout remains clean for protocol messages.

No options beyond standard command-line help.

### Example

```bash
fuse serve
```

Configure your AI client to launch `fuse serve` as a stdio MCP server. See [mcp-integration.md](mcp-integration.md) for setup details.

---

## Exit Behavior

- Fusion completes successfully and writes output: exit code 0
- No files match criteria: error message displayed, exit code 0
- Validation failure (e.g., missing directory, conflicting options): error messages displayed
- Unrecoverable runtime error: error message displayed

---

## Output Filename Convention

| Scenario | Pattern |
|----------|---------|
| Default | `{ProjectName}_{date}_{time}_{tokens}.txt` |
| With `--all` | `{ProjectName}_all_{date}_{time}_{tokens}.txt` |
| Custom name | `{name}_{tokens}.txt` |
| Multi-part | `{ProjectName}_part{N}_{tokens}.txt` |

Token counts in filenames use a `k` suffix for values >= 1000 (e.g., `554k`).
