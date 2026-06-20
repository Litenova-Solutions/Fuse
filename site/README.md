# Fuse documentation site

The marketing landing page and documentation for [Fuse](https://github.com/Litenova-Solutions/Fuse),
the .NET-native codebase context optimizer. Built with Next.js (App Router),
Fumadocs, and Tailwind CSS v4. This app is additive: it lives in `site/` and does
not touch the .NET solution.

## Develop

```bash
cd site
npm install
npm run dev     # http://localhost:3000
npm run build   # production build
```

## Layout

| Path | What it holds |
|------|---------------|
| `src/app/(home)/page.tsx` | The landing page. |
| `src/app/docs` | The documentation layout and the catch-all page renderer. |
| `src/components` | The brand mark, the Motion hero visual, and UI primitives. |
| `src/lib/shared.ts` | App name, GitHub repo, and route constants. |
| `content/docs` | All documentation, as MDX, organized into six sections. |

## Documentation structure

The sidebar follows a Diataxis split, ordered in `content/docs/meta.json`:

1. **Start** - what Fuse is, why, install, quickstart, and the MCP setup.
2. **Scenarios** - task-shaped how-tos, each a job with one command and real output.
3. **Concepts** - how Fuse works, reduction levels, scoping, the precision tier, glossary.
4. **Reference** - commands, options, MCP tools and resources, reducers, templates,
   tokenizers, config keys, output spec, detectors, redaction kinds.
5. **Internals and Extending** - pipeline, capability model, scoping and caching
   internals, options model, and the extension guides.
6. **Project** - benchmarks, performance, roadmap, changelog, contributing.

Measured numbers in these pages come from `tests/benchmarks/results` and the
benchmarks page; they are exact and sourced. Illustrative figures are labeled.

## Deploy

See [DEPLOYMENT.md](./DEPLOYMENT.md) for the Vercel project setup and the
`fuse.codes` DNS records.
