# CLI Reference

Fuse is invoked as `fuse [command] [options]`. All fusion commands share global options from `CommandBase`. Subcommands add template-specific or reduction-specific options.

Config file values (`fuse.json`, `.fuserc`) merge with precedence: explicit CLI flag > config file > built-in default.

---

## Commands

| Command | Description |
|---------|-------------|
| `fuse` | Generic fusion. No template preset unless extensions are specified. |
| `fuse dotnet` | .NET fusion with C# reduction and agentic scoping. |
| `fuse wiki` | Azure DevOps wiki (Markdown only). |
| `fuse init` | Create `fuse.json` in the current directory. |
| `fuse serve` | Start the MCP server on stdio. |

---

## Global options

Available on `fuse`, `fuse dotnet`, and `fuse wiki`.

### Directory and output

| Option | Default | Description |
|--------|---------|-------------|
| `--directory` | Current directory | Root directory to scan |
| `--output` | My Documents/Fuse | Output directory |
| `--name` | Auto-generated | Custom output filename without extension |
| `--overwrite` | true | Overwrite existing output files |

### Extensions

| Option | Description |
|--------|-------------|
| `--include-extensions` | Add extensions on top of template defaults |
| `--exclude-extensions` | Remove extensions from template defaults |
| `--only-extensions` | Process only these extensions; ignores template defaults |

### Exclusions

| Option | Default | Description |
|--------|---------|-------------|
| `--exclude-directories` | | Directory names to skip |
| `--exclude-files` | | Specific file names to exclude |
| `--exclude-patterns` | | Glob patterns to exclude |
| `--exclude-empty-files` | true | Skip zero-byte files |
| `--exclude-auto-generated` | true | Skip auto-generated marker files |
| `--exclude-test-projects` | false | Exclude all test project directories |
| `--respect-git-ignore` | true | Honor `.gitignore` rules |

### Search and performance

| Option | Default | Description |
|--------|---------|-------------|
| `--recursive` | true | Search subdirectories |
| `--max-file-size` | 0 | Maximum file size in KB (0 = unlimited) |
| `--ignore-binary` | true | Skip binary files |
| `--parallelism` | Processor count | Max parallelism for pipeline stages |

### Content and metadata

| Option | Default | Description |
|--------|---------|-------------|
| `--include-metadata` | false | Include file size and modification date |

### Token management

| Option | Default | Description |
|--------|---------|-------------|
| `--max-tokens` | | Hard stop at token count |
| `--split-tokens` | 800000 | Split into new files at this count |
| `--show-token-count` | true | Display token count on completion |
| `--track-top-token-files` | false | Display top 5 token-consuming files |
| `--no-manifest` | false | Disable manifest header (manifest is ON by default) |
| `--git-stats` | false | Include git churn stats in manifest |
| `--provenance` | false | Annotate dependency-expanded entries with inclusion chain |
| `--format` | xml | Output format: xml, markdown, or json |
| `--tokenizer` | o200k_base | OpenAI-compatible encoding via Microsoft.ML.Tokenizers (`o200k_base`, `cl100k_base`, or model alias) |

### Security

| Option | Default | Description |
|--------|---------|-------------|
| `--no-redact` | false | Disable secret redaction (redaction is ON by default) |
| `--redact-report` | false | Append redaction count summary |

### Change scoping

| Option | Default | Description |
|--------|---------|-------------|
| `--changed-since` | | Git ref to scope fusion to changed files |
| `--include-dependents` | true | Include first-degree dependents of changed files |

### Cache and watch

| Option | Default | Description |
|--------|---------|-------------|
| `--no-cache` | false | Disable per-file reduction cache |
| `--clear-cache` | false | Clear `.fuse/cache` before running |
| `--watch` | false | Re-run fusion on file changes (disabled under MCP stdio) |

---

## fuse (generic)

No template preset. When no extension flags are set, all file extensions are eligible.

### Examples

```bash
fuse --directory ./project
fuse --directory ./frontend --only-extensions .ts,.tsx,.css
fuse --directory ./project --max-tokens 100000 --format markdown
```

---

## fuse dotnet

Applies the `DotNet` template. Adds C# reduction and agentic options.

### Additional options

| Option | Default | Description |
|--------|---------|-------------|
| `--remove-csharp-namespaces` | false | Remove namespace declarations |
| `--remove-csharp-comments` | false | Remove all C# comments |
| `--remove-csharp-regions` | false | Remove #region directives |
| `--remove-csharp-usings` | false | Remove using directives |
| `--aggressive` | false | Remove attributes, compress auto-properties |
| `--minify-xml-files` | true | Minify .csproj, .xml, .props, .targets |
| `--minify-html-and-razor` | true | Minify .html, .cshtml ( .razor always minified) |
| `--exclude-unit-test-projects` | false | Exclude unit test dirs only |
| `--all` | false | Enable all C# reduction options |
| `--skeleton` | false | Emit structural skeleton only |
| `--semantic-markers` | false | Prepend structural annotation comments |
| `--focus` | | Type name, filename, or path for dependency scoping |
| `--depth` | 1 | Dependency traversal depth (focus or query) |
| `--query` | | BM25 query for relevance-scoped fusion |
| `--query-top` | 10 | Top-ranked seed files for query scoping |
| `--route-map` | false | Prepend ASP.NET route map |
| `--public-api` | false | Skeleton with public/protected members only |
| `--project-graph` | false | Prepend solution/project reference graph |
| `--pattern-summary` | false | Append cross-codebase pattern summary |

Focus, query, and change scoping are mutually exclusive.

### Examples

```bash
# Standard .NET fusion
fuse dotnet --directory ./src

# Maximum token savings
fuse dotnet --directory ./src --all

# Architecture overview
fuse dotnet --directory ./src --all --skeleton

# Feature drill-down
fuse dotnet --directory ./src --focus OrderService --depth 2

# Query-scoped fusion
fuse dotnet --directory ./src --query "payment gateway" --query-top 10

# PR review scope
fuse dotnet --directory ./src --changed-since main

# With manifest, provenance, and git stats
fuse dotnet --directory ./src --provenance --git-stats

# JSON output with custom tokenizer
fuse dotnet --directory ./src --format json --tokenizer cl100k_base
```

---

## fuse wiki

Applies the `AzureDevOpsWiki` template. Includes only `.md` files.

```bash
fuse wiki --directory ./wiki-repo
fuse wiki --directory ./wiki-repo --include-metadata
```

---

## fuse init

Creates `fuse.json` in the current directory if one does not exist.

```bash
fuse init
```

Default scaffold:

```json
{
  "directory": ".",
  "output": "./fuse-output",
  "format": "xml",
  "tokenizer": "o200k_base",
  "noManifest": false,
  "provenance": false
}
```

---

## fuse serve

Starts the Fuse MCP server on stdio. Logging goes to stderr.

```bash
fuse serve
```

See [mcp.md](mcp.md) for the tool catalog and [mcp-integration.md](mcp-integration.md) for client setup.

---

## Exit behavior

- Fusion completes and writes output: exit code 0
- No files match criteria (non-change mode): error message, exit code 0
- Validation failure: error messages displayed
- Unrecoverable runtime error: error message displayed

---

## Output filename convention

| Scenario | Pattern |
|----------|---------|
| Default | `{ProjectName}_{date}_{time}_{tokens}.txt` |
| With `--all` | `{ProjectName}_all_{date}_{time}_{tokens}.txt` |
| Custom name | `{name}_{tokens}.txt` |
| Multi-part | `{ProjectName}_part{N}_{tokens}.txt` |

Token counts in filenames use a `k` suffix for values >= 1000 (e.g., `554k`).

---

## Config file reference

`fuse.json` or `.fuserc` keys (all optional):

| Key | Type | Description |
|-----|------|-------------|
| `directory` | string | Source directory |
| `output` | string | Output directory |
| `name` | string | Output filename |
| `format` | string | xml, markdown, or json |
| `tokenizer` | string | Tokenizer model name |
| `noManifest` | bool | Disable manifest header |
| `provenance` | bool | Enable inclusion provenance |
| `gitStats` | bool | Include git stats in manifest |
| `maxTokens` | int | Hard token limit |
| `splitTokens` | int | Split threshold |
| `recursive` | bool | Recursive scan |
| `includeMetadata` | bool | Include file metadata |

CLI flags override config values when both are set.
