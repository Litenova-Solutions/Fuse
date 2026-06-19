# Project Templates

Each template defines default file extensions, excluded directories, and excluded glob patterns for a project type. Templates are selected via CLI subcommands (`fuse dotnet`, `fuse wiki`), the MCP `fuse_generic` tool's `template` parameter, or programmatically via `FusionRequestBuilder.WithTemplate()`.

Extension overrides work relative to template defaults:

- `--include-extensions` adds extensions
- `--exclude-extensions` removes extensions
- `--only-extensions` replaces all template defaults entirely

---

## AzureDevOpsWiki

For Azure DevOps wiki repositories.

**Extensions:** `.md`

**Excluded directories:** `.git`, `.attachments`

**Excluded patterns:** none

---

## Clojure

**Extensions:** `.clj`, `.cljs`, `.cljc`, `.edn`

**Excluded directories:** `target`, `.cpcache`, `.git`

**Excluded patterns:** none

---

## CppCSharp

For mixed C++ and C# projects.

**Extensions:** `.cpp`, `.hpp`, `.h`, `.c`, `.cc`, `.cs`, `.csproj`, `.sln`

**Excluded directories:** `bin`, `obj`, `Debug`, `Release`, `x64`, `x86`, `.vs`, `.git`

**Excluded patterns:** none

---

## Dart

For Dart and Flutter projects.

**Extensions:** `.dart`, `.yaml`, `.lock`

**Excluded directories:** `build`, `.dart_tool`, `.pub-cache`, `.git`

**Excluded patterns:** none

---

## DotNet

For .NET projects: C#, F#, VB.NET, ASP.NET, Blazor.

**Extensions:** `.cs`, `.razor`, `.cshtml`, `.xaml`, `.csproj`, `.props`, `.targets`, `.config`, `.json`, `.xml`, `.yml`, `.yaml`, `.md`, `.scss`, `.css`, `.html`, `.htm`, `.editorconfig`

**Excluded directories:** `bin`, `obj`, `.vs`, `.git`, `.idea`, `node_modules`, `TestResults`, `packages`, `artifacts`

**Excluded patterns:**

- Generated C#: `*.feature.cs`, `*Steps.g.cs`, `*.AssemblyHooks.cs`, `*.g.cs`, `*.g.i.cs`, `*.Designer.cs`, `*.designer.cs`, `*_i.c`, `*.generated.cs`, `TemporaryGeneratedFile_*.cs`, `*.Cache.cs`, `*.cache`, `*.baml`, `ServiceReference.cs`, `Reference.cs`, `AssemblyInfo.cs`, `*.xsd.cs`
- Resources: `*.resx`, `*.resources`
- Config noise: `launchSettings.json`, `packages.lock.json`, `bundleconfig.json`
- Minified assets: `*.min.js`, `*.min.css`, `*.map`
- Lock files: `package-lock.json`, `yarn.lock`

**Opt-in extensions** (via `--include-extensions`):

`.sql`, `.bat`, `.sh`, `.ps1`, `.cmd`, `.scriban`, `.feature`, `.nuspec`, `.txt`

---

## Elixir

For Elixir and Phoenix projects.

**Extensions:** `.ex`, `.exs`, `.eex`, `.leex`, `mix.exs`

**Excluded directories:** `_build`, `deps`, `.git`

**Excluded patterns:** none

---

## Erlang

**Extensions:** `.erl`, `.hrl`, `.app.src`, `rebar.config`

**Excluded directories:** `_build`, `.rebar3`, `.git`

**Excluded patterns:** none

---

## Fsharp

For F# projects.

**Extensions:** `.fs`, `.fsi`, `.fsx`, `.fsproj`, `.config`, `.sln`

**Excluded directories:** `bin`, `obj`, `.vs`, `packages`, `node_modules`, `.git`

**Excluded patterns:** none

---

## Generic

Minimal assumptions for text-based files.

**Extensions:** `.txt`, `.md`, `.json`, `.xml`, `.yaml`, `.yml`

**Excluded directories:** `.git`, `.svn`, `.hg`, `node_modules`, `.vscode`, `.idea`

**Excluded patterns:** none

---

## Go

**Extensions:** `.go`, `.mod`, `.sum`

**Excluded directories:** `vendor`, `bin`, `.git`

**Excluded patterns:** none

---

## Haskell

**Extensions:** `.hs`, `.lhs`, `.cabal`, `.hs-boot`

**Excluded directories:** `dist`, `dist-newstyle`, `.stack-work`, `.git`

**Excluded patterns:** none

---

## Infrastructure

For infrastructure-as-code: Terraform, Kubernetes, Ansible, Helm.

**Extensions:** `.tf`, `.tfvars`, `.yaml`, `.yml`, `.json`, `.md`, `.sh`, `.ps1`, `.hcl`, `.tpl`, `.env`, `.properties`, `.conf`, `.config`

**Excluded directories:** `.terraform`, `node_modules`, `.git`, `.vs`, `.idea`, `bin`, `obj`, `dist`, `build`, `.pytest_cache`, `__pycache__`, `tmp`, `temp`, `logs`

**Excluded patterns:**

- Terraform state: `*.tfstate`, `*.tfstate.backup`, `*.tfplan`, `*.tfvars.json`
- Overrides: `override.tf`, `override.tf.json`, `*_override.tf`, `*_override.tf.json`
- Config: `.terraformrc`, `terraform.rc`, `.terraform.lock.hcl`
- Logs: `crash.log`, `crash.*.log`

---

## Java

For Java projects (Maven, Gradle).

**Extensions:** `.java`, `.gradle`, `.xml`, `.properties`, `.jar`, `.jsp`, `.jspx`, `.class`

**Excluded directories:** `build`, `target`, `.gradle`, `.mvn`, `node_modules`, `.git`

**Excluded patterns:** none

---

## JavaScript

**Extensions:** `.js`, `.jsx`, `.json`, `.ts`, `.tsx`, `.html`, `.css`, `.scss`, `.less`, `.mjs`

**Excluded directories:** `node_modules`, `dist`, `build`, `coverage`, `.next`, `.nuxt`, `.git`

**Excluded patterns:** none

---

## Kotlin

For Kotlin and Android projects.

**Extensions:** `.kt`, `.kts`, `.java`, `.xml`, `.gradle`

**Excluded directories:** `build`, `.gradle`, `.idea`, `.git`

**Excluded patterns:** none

---

## Lua

**Extensions:** `.lua`, `.rockspec`

**Excluded directories:** `.git`

**Excluded patterns:** none

---

## Perl

**Extensions:** `.pl`, `.pm`, `.t`

**Excluded directories:** `blib`, `_build`, `.git`

**Excluded patterns:** none

---

## Php

**Extensions:** `.php`, `.phtml`, `.php7`, `.phps`, `.php-s`, `.pht`, `.phar`

**Excluded directories:** `vendor`, `node_modules`, `.git`

**Excluded patterns:** none

---

## Python

**Extensions:** `.py`, `.pyc`, `.pyd`, `.pyo`, `.pyw`, `.pyx`, `.pxd`, `.pxi`, `.ipynb`, `.req`, `.txt`

**Excluded directories:** `__pycache__`, `.venv`, `venv`, `env`, `.tox`, `dist`, `build`, `.git`, `.pytest_cache`

**Excluded patterns:** none

---

## R

For R projects (statistics, data science).

**Extensions:** `.R`, `.Rmd`, `.Rproj`, `.RData`, `.rds`

**Excluded directories:** `.Rproj.user`, `.Rhistory`, `.RData`, `.Ruserdata`, `.git`

**Excluded patterns:** none

---

## Ruby

For Ruby and Rails projects.

**Extensions:** `.rb`, `.rake`, `.gemspec`, `Gemfile`, `Rakefile`, `.erb`, `.haml`, `.slim`

**Excluded directories:** `vendor`, `.bundle`, `coverage`, `tmp`, `log`, `.git`

**Excluded patterns:** none

---

## Rust

For Rust projects (Cargo).

**Extensions:** `.rs`, `.toml`, `.lock`

**Excluded directories:** `target`, `.cargo`, `.git`

**Excluded patterns:** none

---

## Scala

**Extensions:** `.scala`, `.sbt`, `.sc`

**Excluded directories:** `target`, `project/target`, `.bloop`, `.metals`, `.git`

**Excluded patterns:** none

---

## Swift

For Swift, iOS, and macOS projects.

**Extensions:** `.swift`, `.xib`, `.storyboard`, `.xcodeproj`, `.pbxproj`, `.plist`

**Excluded directories:** `.build`, `Pods`, `.git`

**Excluded patterns:** none

---

## TypeScript

**Extensions:** `.ts`, `.tsx`, `.js`, `.jsx`, `.json`, `.html`, `.css`, `.scss`, `.less`

**Excluded directories:** `node_modules`, `dist`, `build`, `coverage`, `.next`, `.nuxt`, `.git`

**Excluded patterns:** none

---

## VbNet

For Visual Basic .NET projects.

**Extensions:** `.vb`, `.vbproj`, `.config`, `.settings`, `.resx`, `.sln`

**Excluded directories:** `bin`, `obj`, `.vs`, `packages`, `node_modules`, `.git`

**Excluded patterns:** none

---

## Choosing a Template

| Use Case | Template or Command |
|----------|---------------------|
| .NET / C# / ASP.NET | `DotNet` or `fuse dotnet` |
| Azure DevOps wiki | `AzureDevOpsWiki` or `fuse wiki` |
| Python backend | `Python` |
| React / Node frontend | `JavaScript` or `TypeScript` |
| Terraform / K8s | `Infrastructure` |
| Mixed or unknown | `Generic` or no template with `--only-extensions` |

For the complete enum list, see `ProjectTemplate` in `Fuse.Collection/Models/ProjectTemplate.cs`.
