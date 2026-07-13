import Link from 'next/link';
import type { Metadata } from 'next';
import {
  ArrowRight,
  Crosshair,
  GitPullRequest,
  ShieldCheck,
  Zap,
} from 'lucide-react';
import { Button } from '@/components/ui/button';
import { githubUrl } from '@/lib/shared';

const siteUrl = 'https://fuse.codes';

export const metadata: Metadata = {
  title: {
    absolute: 'Fuse - local compiler and typed .NET wiring for coding agents',
  },
  description:
    'Fuse gives existing coding agents local, graded compiler checks and typed .NET wiring through MCP, with no hosted model or embedding download.',
  alternates: {
    canonical: siteUrl,
  },
  openGraph: {
    type: 'website',
    url: siteUrl,
    title: 'Fuse - local compiler and typed .NET wiring for coding agents',
    description:
      'Local, graded compiler checks and typed .NET wiring for existing coding agents.',
    siteName: 'Fuse',
    images: [{ url: '/fuse-social-card.png', width: 1280, height: 640, alt: 'Fuse benchmarks' }],
  },
  twitter: {
    card: 'summary_large_image',
    title: 'Fuse - local compiler and typed .NET wiring for coding agents',
    description:
      'Local, graded compiler checks and typed .NET wiring for existing coding agents.',
    images: ['/fuse-social-card.png'],
  },
};

function CodeBlock({ children }: { children: React.ReactNode }) {
  return (
    <pre className="overflow-x-auto rounded-lg border border-fd-border bg-fd-card p-4 font-mono text-[13px] leading-relaxed text-fd-foreground">
      {children}
    </pre>
  );
}

const softwareApplicationJsonLd = {
  '@context': 'https://schema.org',
  '@type': 'SoftwareApplication',
  name: 'Fuse',
  applicationCategory: 'DeveloperApplication',
  operatingSystem: 'Windows, Linux, macOS',
  description:
    'Local compiler and typed .NET wiring service for coding agents, exposed through MCP.',
  url: siteUrl,
  downloadUrl: 'https://www.nuget.org/packages/Fuse',
  license: 'https://www.apache.org/licenses/LICENSE-2.0',
  offers: {
    '@type': 'Offer',
    price: '0',
    priceCurrency: 'USD',
  },
};

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col">
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(softwareApplicationJsonLd) }}
      />

      {/* Hero */}
      <section className="relative overflow-hidden border-b border-fd-border">
        <div className="pointer-events-none absolute inset-0 bg-grid" />
        <div className="relative mx-auto grid w-full max-w-6xl gap-12 px-6 py-20 md:py-28 lg:grid-cols-2 lg:items-center">
          <div className="flex flex-col items-start text-left">
            <span className="inline-flex items-center gap-2 rounded-full border border-fd-border bg-fd-card px-3 py-1 text-xs font-medium text-fd-muted-foreground">
              Local compiler service for existing coding agents
            </span>
            <h1 className="mt-5 text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
              Give your coding agent a local .NET compiler and wiring graph
            </h1>
            <p className="mt-6 max-w-xl text-lg text-fd-muted-foreground">
              Fuse connects Claude Code, Cursor, Copilot, and other MCP clients to graded
              compiler checks and typed .NET wiring. It runs on your machine, requires no hosted
              model or embedding download, and leaves the working tree unchanged during checks.
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
          <div className="space-y-4">
            <div>
              <div className="mb-2 text-xs font-medium uppercase tracking-wide text-fd-muted-foreground">
                Without Fuse
              </div>
              <CodeBlock>{`agent proposes an edit to OrderService.cs
$ dotnet build          (a full round-trip)
error CS1061: 'Order' has no member
  'TotalAmount'
agent reads the error, edits, builds again
... repeat until green`}</CodeBlock>
            </div>
            <div>
              <div className="mb-2 text-xs font-medium uppercase tracking-wide text-[var(--brand)]">
                With Fuse and build capture
              </div>
              <CodeBlock>{`fuse_check file="OrderService.cs"
           content="<proposed edit>"
  -> CS1061 at line 41: 'Order' has no
     member 'TotalAmount'
     repair: 'Total' exists; 'TotalAmount'
     does not
     grade: oracle   (before the edit lands)`}</CodeBlock>
            </div>
          </div>
        </div>
      </section>

      {/* Proof strip */}
      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl grid-cols-1 gap-6 px-6 py-12 md:grid-cols-3">
          <Metric
            value="0 wrong tested verdicts"
            label="Across 1,000 compiler-labeled OrderingApp mutations, fuse_check returned no false-green or false-red verdicts."
          />
          <Metric
            value="89% vs 82%"
            label="True pass@1 in a reduced-scope loop run. The 95% confidence intervals overlapped, and build plus test calls were 3.1 vs 3.2."
          />
          <Metric
            value="~1,026 tokens"
            label="Median git-seeded branch context over 69 PRs, including every changed file by construction plus graph-selected support files."
          />
        </div>
        <p className="pb-8 text-center text-xs text-fd-muted-foreground">
          Measured over real open-source .NET repositories, with weaknesses reported alongside
          strengths.{' '}
          <Link href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
            See the benchmarks
          </Link>
          .
        </p>
      </section>

      {/* How it works */}
      <section className="mx-auto w-full max-w-5xl px-6 py-20">
        <div className="text-center">
          <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
            How it works
          </h2>
          <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
            <code className="font-mono">fuse_check</code> typechecks a proposed single-file edit
            without writing it. Each answer reports its evidence grade: resident build-captured
            compilation when opt-in resident mode is available, a scoped build fallback, or an
            abstention. <code className="font-mono">fuse_test</code> can then run covering tests.
          </p>
        </div>
        <div className="mt-10">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="/fuse-loop-diagram.svg"
            alt="The Fuse verify loop: an agent proposes an edit; fuse_check returns graded compiler diagnostics and a repair packet before the file is written; the agent applies the repair and re-checks; when clean, fuse_test runs the selected covering tests."
            className="mx-auto w-full max-w-3xl"
          />
        </div>
      </section>

      {/* What it answers */}
      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <h2 className="text-center text-2xl font-semibold tracking-tight md:text-3xl">
            What Fuse answers
          </h2>
          <div className="mt-10 grid gap-6 md:grid-cols-2">
            <Feature
              icon={<ShieldCheck className="size-5" />}
              title="Verify an edit"
              body="fuse_check typechecks an in-memory single-file proposal and returns diagnostics plus a repair packet. It reports the evidence grade and abstains when neither compiler path can answer."
            />
            <Feature
              icon={<Zap className="size-5" />}
              title="Blast radius"
              body="fuse_impact lists callers, implementers, and referencing types supported by the typed graph, so the agent can inspect a signature change before editing."
            />
            <Feature
              icon={<Crosshair className="size-5" />}
              title=".NET wiring"
              body="Fuse resolves what is connected to what: a service to its registered implementation, a request to its handler, a route to its action, options to their consumers. Answers come from Roslyn, not grep."
            />
            <Feature
              icon={<GitPullRequest className="size-5" />}
              title="Scoped PR context"
              body="fuse_review starts from the git diff and adds graph-selected support files under a token budget, with provenance for each inclusion. It is compact branch context, not proof that every relevant file was found."
            />
          </div>
          <div className="mt-8 text-center">
            <Button asChild variant="secondary">
              <Link href="/docs/start/why-fuse">
                Why add Fuse to an agent{' '}
                <ArrowRight className="size-4" />
              </Link>
            </Button>
          </div>
        </div>
      </section>

      {/* Connect + CTA */}
      <section className="mx-auto w-full max-w-6xl px-6 py-24">
        <div className="grid w-full gap-10 lg:grid-cols-2 lg:items-center">
          <div>
            <h2 className="text-2xl font-semibold tracking-tight md:text-3xl">
              Connect it to your agent
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Install the .NET tool, then register <code className="font-mono">fuse mcp serve</code>.
              Your current agent gains check, impact, wiring, review, and context verbs without
              replacing its model, editor, or file tools.
            </p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/connect-your-ai">
                  Connect your agent <ArrowRight className="size-4" />
                </Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <Link href="/docs/project/benchmarks">See the benchmarks</Link>
              </Button>
            </div>
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
            <CodeBlock>{`dotnet tool install -g Fuse
fuse mcp install --rules`}</CodeBlock>
          </div>
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
