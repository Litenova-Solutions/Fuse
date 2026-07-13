import Link from 'next/link';
import type { Metadata } from 'next';
import { ArrowRight, Check } from 'lucide-react';
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
    images: [
      {
        url: '/fuse-social-card.png',
        width: 1280,
        height: 640,
        alt: 'Fuse gives coding agents local compiler evidence and typed .NET wiring',
      },
    ],
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
    <pre className="overflow-x-auto rounded-lg border border-fd-border bg-fd-background p-4 font-mono text-[13px] leading-relaxed text-fd-foreground">
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

      <section className="relative overflow-hidden border-b border-fd-border">
        <div className="mx-auto grid w-full max-w-6xl gap-12 px-6 py-16 md:py-20 lg:grid-cols-[0.9fr_1.1fr] lg:items-center">
          <div className="flex flex-col items-start text-left">
            <span className="inline-flex rounded-full border border-fd-border bg-fd-card px-3 py-1 text-xs font-medium text-fd-muted-foreground">
              Open source .NET 10 tool for MCP clients
            </span>
            <h1 className="mt-5 text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
              Compiler evidence before your agent edits
            </h1>
            <p className="mt-6 max-w-xl text-lg text-fd-muted-foreground">
              Fuse gives Cursor, Claude Code, Copilot, and other MCP clients graded compiler
              checks and a typed .NET wiring graph. It runs locally and reports which compiler
              path produced each answer.
            </p>
            <div className="mt-8 w-full max-w-xl space-y-3" aria-label="Install and connect Fuse">
              <div>
                <p className="mb-2 text-xs font-medium uppercase tracking-wide text-fd-muted-foreground">
                  Install
                </p>
                <CodeBlock>dotnet tool install -g Fuse</CodeBlock>
              </div>
              <div>
                <p className="mb-2 text-xs font-medium uppercase tracking-wide text-fd-muted-foreground">
                  Connect and add agent rules
                </p>
                <CodeBlock>fuse mcp install --rules</CodeBlock>
              </div>
            </div>
            <div className="mt-6 flex flex-wrap items-center gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/connect-your-ai">
                  Connect Fuse <ArrowRight className="size-4" aria-hidden="true" />
                </Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <a href={githubUrl} target="_blank" rel="noreferrer">
                  View source
                </a>
              </Button>
            </div>
            <ul className="mt-6 flex flex-wrap gap-x-5 gap-y-2 text-sm text-fd-muted-foreground">
              <TrustItem>Apache-2.0 source</TrustItem>
              <TrustItem>Local workspace data</TrustItem>
              <TrustItem>Checks do not write files</TrustItem>
            </ul>
          </div>
          <figure className="overflow-hidden rounded-xl border border-fd-border bg-fd-card shadow-sm">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src="/fuse-check-demo.png"
              alt="Terminal output from fuse_check showing a compiler diagnostic, a suggested repair, and the evidence grade for a proposed C# edit"
              className="h-auto w-full"
            />
            <figcaption className="border-t border-fd-border px-4 py-3 text-sm text-fd-muted-foreground">
              Real <code className="font-mono">fuse_check</code> output. The proposed file stays
              in memory until the agent chooses to apply it.
            </figcaption>
          </figure>
        </div>
      </section>

      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl grid-cols-1 gap-6 px-6 py-12 md:grid-cols-3">
          <Metric
            value="0 false green"
            label="In 1,000 compiler-verified mutation cases on the in-repo OrderingApp fixture; false red was also 0."
          />
          <Metric
            value="24 of 24 agreed"
            label="Oracle-grade and build-grade diagnostics matched by ID on 24 OrderingApp fixture mutants."
          />
          <Metric
            value="1,026 tokens"
            label="Median review context across the 69-PR corpus-v2 run; changed files were seeded by construction."
          />
        </div>
        <p className="pb-8 text-center text-xs text-fd-muted-foreground">
          Fixture and corpus scope is stated with each result. Full methods, confidence intervals,
          and recorded limitations are published in{' '}
          <Link href="/docs/project/benchmarks" className="underline hover:text-fd-foreground">
            the benchmarks
          </Link>
          .
        </p>
      </section>

      <section className="mx-auto grid w-full max-w-6xl gap-12 px-6 py-20 lg:grid-cols-2 lg:items-center">
        <div>
          <p className="text-sm font-medium text-[var(--brand)]">A bounded local service</p>
          <h2 className="mt-2 text-2xl font-semibold tracking-tight md:text-3xl">
            Your agent keeps its model and file tools
          </h2>
          <p className="mt-4 text-fd-muted-foreground">
            Fuse adds compiler, graph, test-selection, and review tools over Model Context
            Protocol (MCP). Source and index data stay on the machine. Read and check operations do
            not modify the working tree; only the explicit apply operation can write a proposed
            single-file edit.
          </p>
          <dl className="mt-8 grid gap-4 sm:grid-cols-2">
            <BoundaryFact term="Runtime">Local .NET 10 global tool</BoundaryFact>
            <BoundaryFact term="Models">No hosted model or embedding download</BoundaryFact>
            <BoundaryFact term="Clients">Cursor, Claude Code, Copilot, and MCP clients</BoundaryFact>
            <BoundaryFact term="Platforms">Windows, Linux, and macOS</BoundaryFact>
          </dl>
        </div>
        <figure>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="/fuse-product-boundary.svg"
            alt="Product boundary diagram showing an MCP coding agent calling the local Fuse process, which reads the workspace and compiler state without sending source to a hosted Fuse service"
            className="mx-auto w-full"
          />
          <figcaption className="mt-3 text-sm text-fd-muted-foreground">
            Fuse is an additive local tool. It does not replace the editor, model, source control,
            or build system.
          </figcaption>
        </figure>
      </section>

      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-2xl">
            <p className="text-sm font-medium text-[var(--brand)]">High-value workflows</p>
            <h2 className="mt-2 text-2xl font-semibold tracking-tight md:text-3xl">
              Ask precise questions before changing code
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Each tool maps to one engineering action and returns provenance, diagnostics, or an
              explicit abstention when the available evidence cannot support an answer.
            </p>
          </div>
          <div className="mt-10 grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            <Feature
              tool="fuse_check"
              title="Check an edit"
              body="Typecheck a proposed single-file change and return graded compiler diagnostics before the file is written."
            />
            <Feature
              tool="fuse_impact"
              title="Inspect impact"
              body="List callers, implementers, and referencing types before a signature change."
            />
            <Feature
              tool="fuse_find"
              title="Resolve .NET wiring"
              body="Trace services, requests, routes, and configuration to their typed implementations and consumers."
            />
            <Feature
              tool="fuse_test"
              title="Run covering tests"
              body="Select and run test types connected to a symbol through the persisted test graph."
            />
            <Feature
              tool="fuse_refactor"
              title="Stage a refactor"
              body="Run compiler-backed rename, parameter, interface, move-type, and code-fix operations as a verified diff."
            />
            <Feature
              tool="fuse_impact"
              title="Check a package upgrade"
              body="Compare NuGet versions and return the package upgrade break set supported by the index."
            />
            <Feature
              tool="fuse_review"
              title="Review a git change"
              body="Start from the diff and add graph-selected support context with provenance under a token budget."
            />
          </div>
          <div className="mt-8 text-center">
            <Button asChild variant="secondary">
              <Link href="/docs/reference/mcp-tools">
                See the tool reference <ArrowRight className="size-4" aria-hidden="true" />
              </Link>
            </Button>
          </div>
        </div>
      </section>

      <section className="mx-auto grid w-full max-w-6xl gap-12 px-6 py-20 lg:grid-cols-[0.8fr_1.2fr] lg:items-center">
        <div>
          <p className="text-sm font-medium text-[var(--brand)]">Evidence grades stay visible</p>
          <h2 className="mt-2 text-2xl font-semibold tracking-tight md:text-3xl">
            Check, repair, then run the tests that cover the symbol
          </h2>
          <p className="mt-4 text-fd-muted-foreground">
            With a resident build-captured compilation, <code className="font-mono">fuse_check</code>{' '}
            can answer at oracle grade without invoking a build. Otherwise it runs a scoped build
            and labels the result build grade. It abstains if neither compiler path can run.
          </p>
          <p className="mt-4 text-sm text-fd-muted-foreground">
            The recorded reduced-scope loop run did not show fewer agent-visible build and test
            calls: Fuse averaged 3.1 versus 3.2 for the native arm across the scored run.
          </p>
        </div>
        <figure>
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="/fuse-loop-diagram.svg"
            alt="Verification loop showing an agent proposing an edit, fuse_check returning graded compiler diagnostics, the agent applying a repair, and fuse_test running covering tests after a clean check"
            className="mx-auto w-full"
          />
          <figcaption className="mt-3 text-sm text-fd-muted-foreground">
            The no-build check shown here is the oracle-grade path. Build grade remains the
            fallback when build-captured compiler state is unavailable.
          </figcaption>
        </figure>
      </section>

      <section className="border-t border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-4xl px-6 py-20 text-center">
          <p className="text-sm font-medium text-[var(--brand)]">Ready for a real repository</p>
          <h2 className="mt-2 text-3xl font-semibold tracking-tight">
            Add compiler evidence to your next agent session
          </h2>
          <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
            Install the .NET 10 global tool, connect the MCP server, and keep your existing coding
            agent and workflow.
          </p>
          <div className="mt-8 flex flex-wrap justify-center gap-3">
            <Button asChild size="lg">
              <Link href="/docs/start/connect-your-ai">
                Connect Fuse <ArrowRight className="size-4" aria-hidden="true" />
              </Link>
            </Button>
            <Button asChild size="lg" variant="secondary">
              <Link href="/docs/project/benchmarks">Read the benchmark methods</Link>
            </Button>
          </div>
        </div>
      </section>
    </main>
  );
}

function TrustItem({ children }: { children: React.ReactNode }) {
  return (
    <li className="inline-flex items-center gap-1.5">
      <Check className="size-3.5 text-[var(--brand)]" aria-hidden="true" />
      {children}
    </li>
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
  tool,
  title,
  body,
}: {
  tool: string;
  title: string;
  body: string;
}) {
  return (
    <div className="rounded-xl border border-fd-border bg-fd-background p-6">
      <code className="font-mono text-xs font-medium text-[var(--brand)]">{tool}</code>
      <h3 className="mt-3 font-semibold">{title}</h3>
      <p className="mt-2 text-sm text-fd-muted-foreground">{body}</p>
    </div>
  );
}

function BoundaryFact({ term, children }: { term: string; children: React.ReactNode }) {
  return (
    <div className="border-l-2 border-[var(--brand)] pl-4">
      <dt className="text-sm font-medium">{term}</dt>
      <dd className="mt-1 text-sm text-fd-muted-foreground">{children}</dd>
    </div>
  );
}
