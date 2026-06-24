import Link from 'next/link';
import Image from 'next/image';
import type { Metadata } from 'next';
import {
  ArrowRight,
  Crosshair,
  GitPullRequest,
  Layers,
  Plug,
  ShieldCheck,
  Coins,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { HeroVisual } from '@/components/hero-visual';
import { githubUrl } from '@/lib/shared';

export const metadata: Metadata = {
  title: 'Fuse - collapse your AI agent\'s explore phase on .NET into one call',
  description:
    'Fuse is a Model Context Protocol server and CLI that finds the .NET code a task needs and hands it over scoped and reduced in one call, so your agent spends its time on the change instead of exploring.',
};

function CodeBlock({ children }: { children: React.ReactNode }) {
  return (
    <pre className="overflow-x-auto rounded-lg border border-fd-border bg-fd-card p-4 font-mono text-[13px] leading-relaxed text-fd-foreground">
      {children}
    </pre>
  );
}

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col">
      {/* Hero */}
      <section className="relative overflow-hidden border-b border-fd-border">
        <div className="pointer-events-none absolute inset-0 bg-grid" />
        <div className="relative mx-auto grid w-full max-w-6xl gap-12 px-6 py-20 md:py-28 lg:grid-cols-2 lg:items-center">
          <div className="flex flex-col items-start text-left">
            <span className="inline-flex items-center gap-2 rounded-full border border-fd-border bg-fd-card px-3 py-1 text-xs font-medium text-fd-muted-foreground">
              MCP server for AI coding agents on .NET
            </span>
            <h1 className="mt-5 text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
              Your agent&apos;s explore phase,{' '}
              <span className="text-gradient">collapsed into one call</span>.
            </h1>
            <p className="mt-6 max-w-xl text-lg text-fd-muted-foreground">
              An AI coding agent starts every task by exploring: listing, searching,
              opening file after file to find the few that matter. Fuse finds the .NET
              code a task needs and hands it over scoped and reduced in one call, so the
              agent spends its time on the change instead of the hunt, with 99 to 100% of
              the public API intact. A CLI is included.
            </p>
            <div className="mt-8 flex flex-wrap items-center gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/what-is-fuse">
                  See how it works <ArrowRight className="size-4" />
                </Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <Link href="/docs/start/connect-your-ai">Connect your agent</Link>
              </Button>
            </div>
            <p className="mt-4 text-sm text-fd-muted-foreground">
              <a href={githubUrl} target="_blank" rel="noreferrer" className="underline hover:text-fd-foreground">
                View on GitHub
              </a>{' '}
              or install with{' '}
              <code className="font-mono">dotnet tool install -g Fuse</code>
            </p>
          </div>
          <div className="flex justify-center lg:justify-end">
            <HeroVisual />
          </div>
        </div>
      </section>

      {/* Measured metrics strip */}
      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl grid-cols-2 gap-6 px-6 py-10 md:grid-cols-4">
          <Metric value="1 call" label="to gather a change's context, vs at least ~6 blind file reads" />
          <Metric value="~13x" label="fewer tokens than a generic packer at that one call" />
          <Metric value="88%" label="change-scoping recall on real merged PRs" />
          <Metric value="99-100%" label="of public types and methods kept" />
        </div>
        <p className="pb-6 text-center text-xs text-fd-muted-foreground">
          Measured over a commit-pinned OSS corpus, counted with{' '}
          <code className="font-mono">o200k_base</code>. The blind-read count is a
          structural lower bound; read the token win with its 51% query recall.{' '}
          <Link href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
            Reproduce the benchmarks
          </Link>
          .
        </p>
      </section>

      {/* Problem */}
      <section className="mx-auto w-full max-w-4xl px-6 py-20 text-center">
        <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
          Agents waste their context window finding the code
        </h2>
        <p className="mx-auto mt-5 max-w-2xl text-fd-muted-foreground">
          Before an AI coding tool changes a line, it explores: lists directories,
          greps, opens file after file to learn which ones matter. On a solution
          with hundreds of C# files that burns most of the context window and many
          slow round-trips on discovery, not on the task. Generic packers fix this
          by dumping the whole repo as text, which is rarely smaller and loses the
          structure. Fuse takes a different path.
        </p>
        <Button asChild variant="ghost" className="mt-6">
          <Link href="/docs/start/why-fuse">
            Why Fuse, and how it compares <ArrowRight className="size-4" />
          </Link>
        </Button>
      </section>

      {/* Value props */}
      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="grid gap-6 md:grid-cols-3">
            <Feature
              icon={<Plug className="size-5" />}
              title="One call, not an explore loop"
              body="fuse mcp serve is a Model Context Protocol server with eight tools for Claude Code, Cursor, and Copilot. Your agent fetches scoped, reduced context in one call instead of opening files one by one, so its time goes to the change."
            />
            <Feature
              icon={<Crosshair className="size-5" />}
              title="Finds the right files"
              body="Your agent scopes a fusion to a type and its dependencies, the files a git diff touched, or the files a query ranks highest. Fuse expands through a dependency graph instead of dumping everything."
            />
            <Feature
              icon={<Layers className="size-5" />}
              title="Refines across turns"
              body="A shared session id means later calls in the same task skip files the agent already holds, so a multi-step change does not re-pay tokens for context it already has."
            />
            <Feature
              icon={<Coins className="size-5" />}
              title="Fewer tokens, full API"
              body="Structural C# reduction cuts 7-40% of tokens, and an independent Roslyn oracle confirms the default and --all keep 99-100% of public types and methods. Reduction is not deletion."
            />
            <Feature
              icon={<GitPullRequest className="size-5" />}
              title="Built for review"
              body="Your agent scopes to a branch with change recall of 88% on real merged PRs and prepends a review map of diff hunks and the callers of each changed file."
            />
            <Feature
              icon={<ShieldCheck className="size-5" />}
              title="Roslyn structural analysis"
              body="C# skeletons, dependency edges, route maps, and semantic markers use Roslyn syntax parsing by default."
            />
          </div>
        </div>
      </section>

      {/* Benchmark figure */}
      <section className="mx-auto w-full max-w-5xl px-6 py-20">
        <div className="text-center">
          <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
            Measured, and reported in full
          </h2>
          <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
            Round-trips, tokens, scoping recall, and public-API fidelity across the
            benchmark corpus. Every number comes from a harness anyone can rerun
            against the same pinned commits, including the arms where Fuse ties or loses.
          </p>
        </div>
        <div className="mt-10 overflow-hidden rounded-xl border border-fd-border bg-fd-card p-4">
          <Image
            src="/fuse-benchmarks.png"
            alt="Fuse benchmark results across MediatR, FluentValidation, AutoMapper, and Newtonsoft.Json: token reduction at full public-API fidelity, change-scoping recall versus a grep baseline, skeleton method fidelity with the opt-in Roslyn tier, one scoped call replacing at least six grep-and-open round-trips, and that call delivering a task's context in about 13 times fewer tokens than a generic packer."
            width={1600}
            height={900}
            className="h-auto w-full rounded-lg"
            priority={false}
          />
        </div>
      </section>

      {/* Connect your agent (primary) */}
      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl gap-10 px-6 py-20 lg:grid-cols-2 lg:items-center">
          <div>
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Connect it to your agent in one line
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Run <code className="font-mono">fuse mcp serve</code> and your agent gets
              eight tools: survey a codebase, drill into a type, scope to a query
              or a branch, or ask one question and let Fuse pick the strategy. It
              works with Claude Code, Cursor, and GitHub Copilot.
            </p>
            <Button asChild className="mt-6">
              <Link href="/docs/start/connect-your-ai">
                Connect your agent <ArrowRight className="size-4" />
              </Link>
            </Button>
          </div>
          <div className="space-y-4">
            <CodeBlock>{`// .mcp.json
{
  "mcpServers": {
    "fuse": {
      "type": "stdio",
      "command": "fuse",
      "args": ["mcp", "serve"]
    }
  }
}`}</CodeBlock>
            <CodeBlock>{`# or register it with Claude Code in one line
claude mcp add fuse --scope project -- fuse mcp serve`}</CodeBlock>
          </div>
        </div>
      </section>

      {/* CLI (secondary) */}
      <section className="mx-auto w-full max-w-6xl px-6 py-20">
        <div className="grid gap-10 lg:grid-cols-2 lg:items-center">
          <div className="space-y-4 lg:order-2">
            <CodeBlock>{`# the same engine, as a plain CLI
dotnet tool install -g Fuse

# fuse a project at full API fidelity
fuse dotnet --directory ./src --all`}</CodeBlock>
            <CodeBlock>{`Fused 511 files
Estimated tokens: 366,121 (-21.5%)
cache: 0 hit / 511 miss
Output: AutoMapper_2026-06-20_366k.txt`}</CodeBlock>
          </div>
          <div className="lg:order-1">
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Prefer the command line?
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The same engine runs as a global tool. Point it at a .NET source
              tree and get one scoped, reduced payload, with a manifest that lists
              every included file and its token cost.
            </p>
            <Button asChild variant="ghost" className="mt-6">
              <Link href="/docs/start/quickstart">
                CLI quickstart <ArrowRight className="size-4" />
              </Link>
            </Button>
          </div>
        </div>
      </section>

      {/* Comparison teaser */}
      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-5xl px-6 py-20">
          <div className="text-center">
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Not a packer. Not an embedding index.
            </h2>
            <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
              Generic packers concatenate files as text. RAG indexers return
              fuzzy chunks. Fuse understands C# structure and preserves it.
            </p>
          </div>
          <div className="mt-10 overflow-x-auto">
            <table className="w-full min-w-[640px] border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-fd-border text-fd-muted-foreground">
                  <th className="py-3 pr-4 font-medium">Capability</th>
                  <th className="py-3 pr-4 font-medium">Generic packers</th>
                  <th className="py-3 pr-4 font-medium">RAG / embeddings</th>
                  <th className="py-3 pr-4 font-medium text-fd-foreground">Fuse</th>
                </tr>
              </thead>
              <tbody className="text-fd-muted-foreground">
                <ComparisonRow
                  cap="Context for one task, one call"
                  packer="One dump, ~512K tokens"
                  rag="Ranked chunks, partial"
                  fuse="One call, ~40K tokens (51% recall)"
                />
                <ComparisonRow
                  cap="Understands C# structure"
                  packer="No, plain text"
                  rag="No, opaque chunks"
                  fuse="Yes, types and signatures"
                />
                <ComparisonRow
                  cap="Keeps whole API surface"
                  packer="Only if you include it all"
                  rag="No, partial recall"
                  fuse="99-100% verified"
                />
                <ComparisonRow
                  cap="Deterministic output"
                  packer="Yes"
                  rag="No, similarity-ranked"
                  fuse="Yes"
                />
              </tbody>
            </table>
          </div>
          <div className="mt-8 text-center">
            <Button asChild variant="secondary">
              <Link href="/docs/start/why-fuse">
                Read the full comparison <ArrowRight className="size-4" />
              </Link>
            </Button>
          </div>
        </div>
      </section>

      {/* Final CTA */}
      <section className="mx-auto w-full max-w-4xl px-6 py-24 text-center">
        <h2 className="text-3xl font-bold tracking-tight md:text-4xl">
          Stop paying for the explore phase.
        </h2>
        <p className="mx-auto mt-5 max-w-xl text-fd-muted-foreground">
          Install Fuse, connect it to your agent, and hand it scoped context that
          fits the window.
        </p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <Button asChild size="lg">
            <Link href="/docs/start/what-is-fuse">
              See how it works <ArrowRight className="size-4" />
            </Link>
          </Button>
          <Button asChild size="lg" variant="secondary">
            <Link href="/docs/start/connect-your-ai">Connect your agent</Link>
          </Button>
        </div>
      </section>
    </main>
  );
}

function Metric({ value, label }: { value: string; label: string }) {
  return (
    <div className="text-center md:text-left">
      <div className="text-2xl font-bold tracking-tight text-[var(--brand)] md:text-3xl">
        {value}
      </div>
      <div className="mt-1 text-sm text-fd-muted-foreground">{label}</div>
    </div>
  );
}

function Feature({
  icon,
  title,
  body,
}: {
  icon: React.ReactNode;
  title: string;
  body: string;
}) {
  return (
    <div className="rounded-xl border border-fd-border bg-fd-background p-6">
      <div className="flex size-10 items-center justify-center rounded-lg bg-[var(--brand)]/10 text-[var(--brand)]">
        {icon}
      </div>
      <h3 className="mt-4 font-semibold">{title}</h3>
      <p className="mt-2 text-sm text-fd-muted-foreground">{body}</p>
    </div>
  );
}

function ComparisonRow({
  cap,
  packer,
  rag,
  fuse,
}: {
  cap: string;
  packer: string;
  rag: string;
  fuse: string;
}) {
  return (
    <tr className="border-b border-fd-border/60">
      <td className="py-3 pr-4 font-medium text-fd-foreground">{cap}</td>
      <td className="py-3 pr-4">{packer}</td>
      <td className="py-3 pr-4">{rag}</td>
      <td className="py-3 pr-4 font-medium text-fd-foreground">{fuse}</td>
    </tr>
  );
}
