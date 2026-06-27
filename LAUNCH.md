# Launch post and demo script

This file is the launch material drawn from the [messaging source of truth](site/content/docs/project/messaging.mdx). Every number is quoted from `tests/benchmarks/results` and linked to the [benchmarks page](https://fuse.codes/docs/project/benchmarks); no head-to-head ranking is claimed beyond what the peer harness measures. The 30-to-60-second screen-capture demo is recorded from the script below (see "Status" at the end for what remains a manual step).

## Launch blog post (draft)

### AI coding assistants get lost in .NET codebases, and it costs you tokens

Watch an AI assistant start a task in a real .NET solution. Before it changes a line, it explores: it lists directories, greps for a name, and opens file after file trying to learn which ones matter. On a solution with hundreds of C# files, most of the context window goes to the hunt, not the change. And there is a class of question it cannot answer by grepping at all: which class does the container actually inject for this interface? Which handler runs this request? Which action does this route hit? Those answers live in the wiring, not in the file names, so the assistant guesses, and a wrong guess sends it opening more files.

That is the problem Fuse attacks.

Fuse is a Model Context Protocol server for .NET. It reads your code with Roslyn and builds a typed graph of how the code is wired, then serves your assistant the files a task needs, scoped and reduced, in one call. The assistant answers from the real graph instead of guessing, spends fewer tokens, and stops paying for the explore loop.

What that looks like in practice, on the eShopOnWeb sample application:

- Ask "what implements `IBasketService`, and what would a change to it touch?" Without Fuse, the assistant greps `IBasketService`, gets a page of hits, and opens files to work out which is the real implementation. With Fuse, `fuse_resolve service="IBasketService"` returns `BasketService` directly, tagged with the `di_resolves_to` edge and the registration that proves it.
- Ask it to review a branch. `fuse_review changedSince="main"` returns the changed files plus their semantic blast radius (the interface a changed type implements, its consumers) with provenance for each file, in about 958 tokens, keeping 100 percent of the changed files.

The numbers, measured over a commit-pinned corpus (Scrutor, Ardalis.Specification, NodaTime, and eShopOnWeb), counted with `o200k_base`, and reproduced with `fuse eval`:

- The extracted wiring graph matches the hand-built ground truth exactly on the wiring fixture: 22 of 22 edges, recall and precision 1.0.
- `fuse review` over 53 real merged pull requests keeps 100 percent of the changed files at 79.8 percent precision in a median 958 returned tokens; a grep baseline reaches 53 percent recall at 14 percent precision.
- Driving Claude (sonnet-4-6) over 12 pull requests, the Fuse MCP arm edged out bare filesystem tools on file recall (30 versus 26 percent) at comparable token cost, on a small, model-dependent sample.

And the honest part, because honesty is the point of a tool you trust with your codebase: the weakest mode is open-ended localization from a bare task title with no git base, where Fuse recalls about 15 percent of the changed files. Rather than return a low-precision guess on a title that names no code, Fuse refuses and hands back a navigation map asking for a symbol, a route, or a git base. Every number above, including that one, is on the [benchmarks page](https://fuse.codes/docs/project/benchmarks) with the reproduction command, and the peer comparison there is the only place a head-to-head is stated, because that is the only place the harness backs it.

Install it, connect it to your agent, and give your assistant a map of your code:

```bash
dotnet tool install -g Fuse
fuse mcp install --rules
```

It works with Claude Code, Cursor, and GitHub Copilot. The deepest support is on .NET; other languages are covered at the syntax tier today.

### Channels

- The .NET communities: r/dotnet, the .NET Discord, and a dev.to / blog cross-post.
- The MCP ecosystem: the MCP Registry (the manifest is in `mcp-registry/server.json`) and the awesome-mcp-servers lists.
- A Show HN with the honest-benchmarks angle (lead with the problem and the reproducible numbers, including the weak mode, not a ranking).

## Demo script (eShopOnWeb, reproducible)

A 30-to-60-second screen capture follows this script. It is a real session on a recognizable .NET application already in the corpus, showing the same two questions answered with and without Fuse. The point is correct-and-cheap with Fuse versus lost-without-it; it does not assert a head-to-head win over another tool.

Setup (once):

```bash
git clone https://github.com/dotnet-architecture/eShopOnWeb
cd eShopOnWeb
dotnet tool install -g Fuse
fuse index .
```

Scene 1, without Fuse (bare tools): ask the agent "what implements IBasketService and what does a change to it touch?" Show it grepping, getting many hits, and opening several files to guess the implementation and consumers.

Scene 2, with Fuse: ask the same question. Show:

```bash
fuse resolve --service IBasketService
# -> BasketService (src/ApplicationCore/Services/BasketService.cs), edge di_resolves_to

fuse review --changed-since main
# -> changed files + support files (the IBasketService interface, its consumers),
#    100% of changed files kept, ~958 tokens median, with provenance
```

End on the contrast: one scoped, provenance-backed answer instead of a file-opening hunt.

## Status

- Written and sourced: the launch post, the channel list, and the reproducible demo script (above).
- Remaining manual steps (cannot be produced from the build environment): record the screen capture from the demo script, publish the post to the chosen channels, and link the published asset from the landing page hero and the README. Until the recording exists, the landing page and README link the sourced benchmarks rather than a video, to avoid linking an asset that is not yet published.
