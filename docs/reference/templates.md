---
title: Templates
description: The project templates that set default file extensions and directory exclusions for each language or repository type.
---

A template is a named set of defaults that tells Fuse which file extensions to collect and which directories and file patterns to skip for a given project type. Selecting the right template is the difference between collecting a clean set of source files and collecting build output, lock files, and editor metadata alongside them. Fuse ships 26 templates covering common languages and repository types.

This page is for engineers choosing a template for a fusion and maintainers who need the exact defaults each one applies.

## Purpose And Scope

A template supplies three lists: the file extensions to include, the directories to exclude by name, and an optional set of file patterns to exclude. Collection applies these lists when it scans the source directory, before any reduction or scoping runs.

How a template is chosen depends on how Fuse is invoked:

| Invocation | Template selected |
|------------|-------------------|
| `fuse dotnet` | DotNet |
| `fuse wiki` | AzureDevOpsWiki |
| `fuse` (generic command) | A named template you pass |
| `fuse_generic` MCP tool | A named template the client passes |

The extension list a template provides is a default. You can add, remove, or replace it per run with `--include-extensions`, `--exclude-extensions`, and `--only-extensions`, documented in the [Options reference](options.md).

## Template Defaults

Each row lists the file extensions a template collects and the directories it excludes by name. Extension and directory lists are reproduced exactly as the template defines them.

| Template | Extensions | Excluded Directories |
|----------|------------|----------------------|
| AzureDevOpsWiki | `.md` | `.git` `.attachments` |
| Clojure | `.clj` `.cljs` `.cljc` `.edn` | `target` `.cpcache` `.git` |
| CppCSharp | `.cpp` `.hpp` `.h` `.c` `.cc` `.cs` `.csproj` `.sln` | `bin` `obj` `Debug` `Release` `x64` `x86` `.vs` `.git` |
| Dart | `.dart` `.yaml` `.lock` | `build` `.dart_tool` `.pub-cache` `.git` |
| DotNet | `.cs` `.razor` `.cshtml` `.xaml` `.csproj` `.props` `.targets` `.config` `.json` `.xml` `.yml` `.yaml` `.md` `.scss` `.css` `.html` `.htm` `.editorconfig` | `bin` `obj` `.vs` `.git` `.idea` `node_modules` `TestResults` `packages` `artifacts` |
| Elixir | `.ex` `.exs` `.eex` `.leex` `mix.exs` | `_build` `deps` `.git` |
| Erlang | `.erl` `.hrl` `.app.src` `rebar.config` | `_build` `.rebar3` `.git` |
| Fsharp | `.fs` `.fsi` `.fsx` `.fsproj` `.config` `.sln` | `bin` `obj` `.vs` `packages` `node_modules` `.git` |
| Generic | `.txt` `.md` `.json` `.xml` `.yaml` `.yml` | `.git` `.svn` `.hg` `node_modules` `.vscode` `.idea` |
| Go | `.go` `.mod` `.sum` | `vendor` `bin` `.git` |
| Haskell | `.hs` `.lhs` `.cabal` `.hs-boot` | `dist` `dist-newstyle` `.stack-work` `.git` |
| Infrastructure | `.tf` `.tfvars` `.yaml` `.yml` `.json` `.md` `.sh` `.ps1` `.hcl` `.tpl` `.env` `.properties` `.conf` `.config` | `.terraform` `node_modules` `.git` `.vs` `.idea` `bin` `obj` `dist` `build` `.pytest_cache` `__pycache__` `tmp` `temp` `logs` |
| JavaScript | `.js` `.jsx` `.json` `.ts` `.tsx` `.html` `.css` `.scss` `.less` `.mjs` | `node_modules` `dist` `build` `coverage` `.next` `.nuxt` `.git` |
| Java | `.java` `.gradle` `.xml` `.properties` `.jar` `.jsp` `.jspx` `.class` | `build` `target` `.gradle` `.mvn` `node_modules` `.git` |
| Kotlin | `.kt` `.kts` `.java` `.xml` `.gradle` | `build` `.gradle` `.idea` `.git` |
| Lua | `.lua` `.rockspec` | `.git` |
| Perl | `.pl` `.pm` `.t` | `blib` `_build` `.git` |
| Php | `.php` `.phtml` `.php7` `.phps` `.php-s` `.pht` `.phar` | `vendor` `node_modules` `.git` |
| Python | `.py` `.pyc` `.pyd` `.pyo` `.pyw` `.pyx` `.pxd` `.pxi` `.ipynb` `.req` `.txt` | `__pycache__` `.venv` `venv` `env` `.tox` `dist` `build` `.git` `.pytest_cache` |
| R | `.R` `.Rmd` `.Rproj` `.RData` `.rds` | `.Rproj.user` `.Rhistory` `.RData` `.Ruserdata` `.git` |
| Ruby | `.rb` `.rake` `.gemspec` `Gemfile` `Rakefile` `.erb` `.haml` `.slim` | `vendor` `.bundle` `coverage` `tmp` `log` `.git` |
| Rust | `.rs` `.toml` `.lock` | `target` `.cargo` `.git` |
| Scala | `.scala` `.sbt` `.sc` | `target` `project/target` `.bloop` `.metals` `.git` |
| Swift | `.swift` `.xib` `.storyboard` `.xcodeproj` `.pbxproj` `.plist` | `.build` `Pods` `.git` |
| TypeScript | `.ts` `.tsx` `.js` `.jsx` `.json` `.html` `.css` `.scss` `.less` | `node_modules` `dist` `build` `coverage` `.next` `.nuxt` `.git` |
| VbNet | `.vb` `.vbproj` `.config` `.settings` `.resx` `.sln` | `bin` `obj` `.vs` `packages` `node_modules` `.git` |

## DotNet Exclusion Patterns

The DotNet template carries a file-pattern exclusion list in addition to its directory exclusions. These patterns drop generated, designer, and lock files that add tokens without adding source the reader needs. A file matching any pattern is excluded even when its extension is in the include list.

The DotNet exclusion patterns are:

| Category | Patterns |
|----------|----------|
| Generated source | `*.g.cs` `*.g.i.cs` `*.generated.cs` `*.Designer.cs` `*.designer.cs` `TemporaryGeneratedFile_*.cs` `*.xsd.cs` `*_i.c` |
| Test scaffolding | `*.feature.cs` `*Steps.g.cs` `*.AssemblyHooks.cs` |
| Assembly and service metadata | `AssemblyInfo.cs` `ServiceReference.cs` `Reference.cs` |
| Caches and resources | `*.Cache.cs` `*.cache` `*.baml` `*.resx` `*.resources` |
| Settings and lock files | `launchSettings.json` `packages.lock.json` `bundleconfig.json` `package-lock.json` `yarn.lock` |
| Minified and mapped assets | `*.min.js` `*.min.css` `*.map` |

The Infrastructure template carries a smaller pattern list of the same kind, excluding Terraform state and plan files (`*.tfstate` and `*.tfplan` and their variants).

## What This Does Not Cover

This page lists the defaults each template provides; it does not cover the order in which collection applies templates, `.gitignore` rules, and command-line overrides, which the [Core Concepts](../getting-started/core-concepts.md) page describes as the Collection stage. It does not document how to register a new template; the [Adding A Template](../extending/template.md) page covers that.

## Next

See the [Options reference](options.md) to override a template's extensions for a single run, or the [Fusing .NET Code](../guides/fusing-dotnet.md) guide for the DotNet template in practice.
