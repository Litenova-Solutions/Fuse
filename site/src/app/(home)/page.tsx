import Link from 'next/link';
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
import { githubUrl } from '@/lib/shared';

export const metadata: Metadata = {
  title: 'Fuse - a faster, cheaper, more accurate AI assistant on .NET',
  description:
    'Fuse is a Model Context Protocol server that makes your AI coding assistant faster, cheaper, and more accurate on .NET code by understanding how the code is actually wired.',
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
              A faster, cheaper, more accurate{' '}
              <span className="text-gradient">AI assistant on your code</span>.
            </h1>
            <p className="mt-6 max-w-xl text-lg text-fd-muted-foreground">
              Fuse hands your AI coding assistant the .NET code a task needs, scoped and
              reduced, in one call. It understands how your code is actually wired, so the
              assistant answers from the real graph instead of guessing, spends fewer
              tokens, and stops burning its context window on the hunt.
            </p>
            <div className="mt-8 flex flex-wrap items-center gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/connect-your-ai">
                  Connect your agent <ArrowRight className="size-4" />
                </Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <Link href="/docs/start/what-is-fuse">See how it works</Link>
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
          {/* See it: the same question, without and with Fuse */}
          <div className="space-y-4">
            <div>
              <div className="mb-2 text-xs font-medium uppercase tracking-wide text-fd-muted-foreground">
                Without Fuse
              </div>
              <CodeBlock>{`Q: what implements IBasketService, and what would
   a change to it touch?

agent: grep IBasketService ... 14 hits
       open file ... open file ... open file
       (many reads, guessing from names)`}</CodeBlock>
            </div>
            <div>
              <div className="mb-2 text-xs font-medium uppercase tracking-wide text-[var(--brand)]">
                With Fuse
              </div>
              <CodeBlock>{`fuse_resolve service="IBasketService"
  -> BasketService  (di_resolves_to)
fuse_review changedSince="main"
  -> changed + support files, ~958 tokens,
     100% of changed files kept, one call`}</CodeBlock>
            </div>
          </div>
        </div>
      </section>

      {/* Three proof tiles */}
      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl grid-cols-1 gap-6 px-6 py-12 md:grid-cols-3">
          <Metric
            value="Wired, not guessed"
            label="Accurate answers about how the code connects: the extracted wiring graph matches the ground truth exactly on the fixture (22 of 22 edges)."
          />
          <Metric
            value="~958 tokens"
            label="A pull request's scoped context in about a thousand tokens, keeping 100% of the changed files."
          />
          <Metric
            value="Milliseconds"
            label="Warm answers in tens of milliseconds once the index is built, held resident across calls."
          />
        </div>
        <p className="pb-8 text-center text-xs text-fd-muted-foreground">
          Measured over a commit-pinned .NET corpus, counted with{' '}
          <code className="font-mono">o200k_base</code>, and reported in full including the
          modes where Fuse is weak.{' '}
          <Link href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
            See the honest benchmarks
          </Link>
          .
        </p>
      </section>

      {/* Problem */}
      <section className="mx-auto w-full max-w-4xl px-6 py-20 text-center">
        <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
          AI assistants get lost in .NET codebases
        </h2>
        <p className="mx-auto mt-5 max-w-2xl text-fd-muted-foreground">
          Before it changes a line, an assistant explores: it lists directories, greps,
          and opens file after file to learn which ones matter. On a solution with
          hundreds of C# files that burns the context window on discovery, and a grep
          cannot tell which class the container actually injects or which handler a
          request runs. Fuse answers those structural questions directly and hands back
          only the files that matter.
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
              icon={<Crosshair className="size-5" />}
              title="Understands .NET wiring"
              body="Fuse reads your code with Roslyn and resolves what is connected to what: a service to its registered implementation, a request to its handler, a route to its action, options to their consumers. The assistant answers from the real graph."
            />
            <Feature
              icon={<Coins className="size-5" />}
              title="Fewer tokens per turn"
              body="A task's context arrives scoped and reduced instead of as a pile of files. A pull request's context fits in about a thousand tokens, and structural reduction keeps essentially all of the public API."
            />
            <Feature
              icon={<GitPullRequest className="size-5" />}
              title="Built for change review"
              body="Scope to a branch and Fuse returns the changed files plus their semantic blast radius (callers, DI consumers, handlers), with provenance for why each file is there, at 100% changed-file recall."
            />
            <Feature
              icon={<Plug className="size-5" />}
              title="One call, in your agent"
              body="fuse mcp serve is a Model Context Protocol server for Claude Code, Cursor, and Copilot. Your agent fetches scoped context in one call instead of opening files one by one."
            />
            <Feature
              icon={<ShieldCheck className="size-5" />}
              title="Honest by design"
              body="Every published number is sourced and reproducible, weaknesses are listed alongside strengths, and when a request lacks a usable anchor Fuse hands back a navigation map instead of guessing."
            />
            <Feature
              icon={<Layers className="size-5" />}
              title="Offline and local"
              body="A small embedding model is fetched once and cached, then runs entirely offline; no code or query ever leaves your machine, and a deterministic lexical path is the fallback."
            />
          </div>
        </div>
      </section>

      {/* Connect your agent (primary) */}
      <section className="mx-auto w-full max-w-6xl px-6 py-20">
        <div className="grid w-full gap-10 lg:grid-cols-2 lg:items-center">
          <div>
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Connect it to your agent in one line
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Run <code className="font-mono">fuse mcp serve</code> and your agent gets the
              verbs it needs: map the workspace, resolve wiring, localize a task, review a
              change, and read scoped context. It works with Claude Code, Cursor, and
              GitHub Copilot.
            </p>
            <Button asChild className="mt-6">
              <Link href="/docs/start/connect-your-ai">
                Connect your agent <ArrowRight className="size-4" />
              </Link>
            </Button>
          </div>
          <div className="space-y-4">
            <CodeBlock>{`// .mcp.json (Claude Code; same shape for Cursor and Copilot)
{
  "mcpServers": {
    "fuse": {
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

      {/* Comparison teaser */}
      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-5xl px-6 py-20">
          <div className="text-center">
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Not a packer. Not an embedding index.
            </h2>
            <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
              Generic packers concatenate files as text. Embedding search returns fuzzy
              chunks. Neither can answer a structural question. Fuse understands .NET
              structure and resolves it deterministically.
            </p>
          </div>
          <div className="mt-10 overflow-x-auto">
            <table className="w-full min-w-[640px] border-collapse text-left text-sm">
              <thead>
                <tr className="border-b border-fd-border text-fd-muted-foreground">
                  <th className="py-3 pr-4 font-medium">Capability</th>
                  <th className="py-3 pr-4 font-medium">Generic packers</th>
                  <th className="py-3 pr-4 font-medium">Embedding search</th>
                  <th className="py-3 pr-4 font-medium text-fd-foreground">Fuse</th>
                </tr>
              </thead>
              <tbody className="text-fd-muted-foreground">
                <ComparisonRow
                  cap="Answers how the code is wired"
                  packer="No, plain text"
                  rag="No, surface similarity"
                  fuse="Yes, a typed Roslyn graph"
                />
                <ComparisonRow
                  cap="Context for one task, one call"
                  packer="One large dump"
                  rag="Ranked chunks, partial"
                  fuse="Scoped, about a thousand tokens for a PR"
                />
                <ComparisonRow
                  cap="Keeps the public API surface"
                  packer="Only if you include it all"
                  rag="No, partial recall"
                  fuse="Essentially all of it"
                />
                <ComparisonRow
                  cap="Says when it cannot answer"
                  packer="No"
                  rag="No, always returns chunks"
                  fuse="Yes, refuses and hands back a map"
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
          Give your assistant a map of your code.
        </h2>
        <p className="mx-auto mt-5 max-w-xl text-fd-muted-foreground">
          Install Fuse, connect it to your agent, and it answers from how your .NET code is
          actually wired, in fewer tokens.
        </p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <Button asChild size="lg">
            <Link href="/docs/start/connect-your-ai">
              Connect your agent <ArrowRight className="size-4" />
            </Link>
          </Button>
          <Button asChild size="lg" variant="secondary">
            <Link href="/docs/project/benchmarks">See the benchmarks</Link>
          </Button>
        </div>
      </section>
    </main>
  );
}

function Metric({ value, label }: { value: string; label: string }) {
  return (
    <div className="rounded-xl border border-fd-border bg-fd-background p-6 text-center md:text-left">
      <div className="text-xl font-bold tracking-tight text-[var(--brand)] md:text-2xl">
        {value}
      </div>
      <div className="mt-2 text-sm text-fd-muted-foreground">{label}</div>
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
