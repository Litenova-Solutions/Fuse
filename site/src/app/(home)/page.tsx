import type { Metadata } from 'next';
import Link from 'next/link';
import { Button } from '@/components/ui/button';
import { HeroInstallCommands } from '@/components/hero-install';

const siteUrl = 'https://fuse.codes';

export const metadata: Metadata = {
  title: {
    absolute: 'Fuse - local .NET index and compiler checks for coding agents',
  },
  description:
    'Fuse reuses a persistent MSBuild and Roslyn index, returns reduced task-scoped source, resolves .NET wiring, and checks proposed C# files against compiler state.',
  alternates: {
    canonical: siteUrl,
  },
  openGraph: {
    type: 'website',
    url: siteUrl,
    title: 'Fuse - local .NET index and compiler checks for coding agents',
    description:
      'Persistent .NET discovery, reduced task-scoped source, typed-graph wiring resolution, and proposed-file compiler checks.',
    siteName: 'Fuse',
    images: [
      {
        url: '/fuse-social-card.png',
        width: 1280,
        height: 640,
        alt: 'Fuse indexes a .NET solution and checks agent edits through the compiler',
      },
    ],
  },
  twitter: {
    card: 'summary_large_image',
    title: 'Fuse - local .NET index and compiler checks for coding agents',
    description:
      'Persistent .NET discovery, reduced task-scoped source, typed-graph wiring resolution, and proposed-file compiler checks.',
    images: ['/fuse-social-card.png'],
  },
};

const softwareApplicationJsonLd = {
  '@context': 'https://schema.org',
  '@type': 'SoftwareApplication',
  name: 'Fuse',
  applicationCategory: 'DeveloperApplication',
  operatingSystem: 'Windows, Linux, macOS',
  description:
    'A local .NET tool that reuses a persistent solution index, returns reduced task-scoped source, resolves application wiring, and checks proposed C# files against compiler state.',
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
    <div className="flex flex-1 flex-col">
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(softwareApplicationJsonLd) }}
      />

      <section className="border-b border-fd-border">
        <div className="mx-auto w-full max-w-4xl px-6 py-16 text-center md:py-24">
          <p className="text-sm font-medium text-[var(--brand)]">
            Local .NET context for coding agents
          </p>
          <h1 className="mx-auto mt-3 max-w-3xl text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
            Persistent semantic index and compiler verification for .NET agents
          </h1>
          <p className="mx-auto mt-6 max-w-2xl text-lg leading-8 text-fd-muted-foreground">
            Fuse indexes a solution through MSBuild and Roslyn, reuses that work across agent turns,
            resolves .NET wiring from a typed graph, returns reduced source for a selected scope, and
            checks proposed C# files against compiler state.
          </p>

          <div className="mt-8 flex flex-wrap justify-center gap-3">
            <Button asChild size="lg">
              <Link href="/docs/start/connect-your-ai">Connect Fuse</Link>
            </Button>
            <Button asChild size="lg" variant="secondary">
              <Link href="/docs/start/quickstart">Quickstart</Link>
            </Button>
          </div>

          <HeroInstallCommands />

          <div className="mt-14 grid gap-8 sm:grid-cols-2 lg:grid-cols-4">
            <HeroHighlight
              value="1.8 ms"
              title="Exact symbol lookup"
              detail="Median on the recorded NodaTime semantic index with 14,760 symbols; timings depend on the machine."
            />
            <HeroHighlight
              value="15.7 ms"
              title="Task localization"
              detail="Median warm lookup on the same NodaTime run; open-ended recall is reported separately on the benchmarks page."
            />
            <HeroHighlight
              value="38-44%"
              title="Skeleton token reduction"
              detail="Recorded across four repositories while retaining every measured public and protected type name."
            />
            <HeroHighlight
              value="0"
              title="False-green checks"
              detail="Zero false green and zero false red across 1,000 compiler-labeled mutation edits on the recorded OrderingApp set."
            />
          </div>

          <p className="mx-auto mt-10 max-w-2xl text-xs text-fd-muted-foreground">
            Any MCP client, including Cursor, Claude Code, and Copilot. Apache 2.0. Analysis runs locally and can work offline.
          </p>
        </div>
      </section>

      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-2xl">
            <p className="text-sm font-medium text-[var(--brand)]">Persistent discovery</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              One index reused across agent turns
            </h2>
            <p className="mt-4 text-lg text-fd-muted-foreground">
              File reads, grep, and regex can reconstruct the same symbols, references, and project
              structure several times during a task. Fuse stores that discovery in SQLite and updates
              changed files incrementally. Syntax mode provides symbols and source chunks; semantic
              mode adds DI registrations, handlers, routes, options, and call edges.
            </p>
          </div>

          <div className="mt-10 grid gap-5 md:grid-cols-3">
            <WarmIndexCard
              title="Index the solution"
              body="The shared daemon starts warming .fuse/fuse.db when the repository is served. Later calls reuse the store, and changed files re-index incrementally."
            />
            <WarmIndexCard
              title="Resolve .NET wiring"
              body="Resolve a service interface to its registered implementation, a route to its action, or a request to its handler through recorded framework relationships."
            />
            <WarmIndexCard
              title="Return reduced source"
              body="Select source from indexed anchors, reduce it under a token budget, and include provenance so the agent receives the code needed for the current scope."
            />
          </div>

          <div className="mt-10">
            <WiringResolveExample />
          </div>

          <p className="mt-8 text-sm text-fd-muted-foreground">
            Pipeline detail:{' '}
            <Link
              href="/docs/concepts/how-fuse-works"
              className="font-medium text-fd-foreground underline underline-offset-4"
            >
              index, localize, resolve, expand, plan, render
            </Link>
            .
          </p>
        </div>
      </section>

      <section className="border-b border-fd-border">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-2xl">
            <p className="text-sm font-medium text-[var(--brand)]">Indexed discovery and compiler checks</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              A local task path for reading and changing .NET code
            </h2>
            <p className="mt-4 text-lg text-fd-muted-foreground">
              Fuse runs beside your editor on the solution already on disk. The coding agent remains
              responsible for the change. Fuse supplies indexed answers, reduced source, and graded
              compiler results at the points where they are useful.
            </p>
          </div>

          <ol className="home-flow home-flow--six mt-10" aria-label="How Fuse works across a task">
            <FlowStep number="1" title="Survey the workspace">
              Map symbols, routes, and counts with <code className="font-mono text-fd-foreground">fuse_workspace</code>{' '}
              or rank candidate files for a task with <code className="font-mono text-fd-foreground">fuse_find</code>.
            </FlowStep>
            <FlowStep number="2" title="Resolve wiring">
              Trace a service, request, route, or config section to the code that handles it. No
              source bodies yet.
            </FlowStep>
            <FlowStep number="3" title="Read focused context">
              <code className="font-mono text-fd-foreground">fuse_context</code> emits reduced source with a manifest
              explaining why each file was included. Sessions skip repeats across turns.
            </FlowStep>
            <FlowStep number="4" title="Propose and check">
              Send proposed file content to <code className="font-mono text-fd-foreground">fuse_check</code> before
              writing. Revise from compiler diagnostics and repair packets.
            </FlowStep>
            <FlowStep number="5" title="Measure blast radius">
              Call <code className="font-mono text-fd-foreground">fuse_impact</code> before a public signature change.
              Run covering tests with <code className="font-mono text-fd-foreground">fuse_test</code>, not the whole
              suite.
            </FlowStep>
            <FlowStep number="6" title="Review the branch">
              <code className="font-mono text-fd-foreground">fuse_review</code> seeds on the git diff and packs related
              callers, handlers, and tests under a token budget.
            </FlowStep>
          </ol>
        </div>
      </section>

      <section className="mx-auto w-full max-w-6xl px-6 py-20">
        <div className="max-w-2xl">
          <p className="text-sm font-medium text-[var(--brand)]">Daily .NET work</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              MCP tools for .NET agent workflows
            </h2>
        </div>

        <div className="mt-10 grid gap-5 md:grid-cols-2">
          <TaskCard
            number="01"
            title="Check a proposed change"
            body="Typecheck a single-file edit before it is written. If the compiler rejects it, the agent gets the errors and can revise the proposal."
            example="Example: report a missing constructor argument before Program.cs changes."
          />
          <TaskCard
            number="02"
            title="Find the code that actually runs"
            body="Trace a registered service, request, route, or configuration section to its implementation, handler, action, or options class."
            example="Example: resolve POST /checkout to CheckoutCommandHandler.Handle."
          />
          <TaskCard
            number="03"
            title="See callers before changing a signature"
            body="List callers, implementations, and referencing types before a method or interface changes."
            example="Example: find every use of IOrderService.PlaceAsync before adding a parameter."
          />
          <TaskCard
            number="04"
            title="Run the relevant tests"
            body="Select and run the test types connected to the changed symbol instead of starting with the entire test suite."
            example="Example: run CheckoutHandlerTests after changing the checkout handler."
          />
          <TaskCard
            number="05"
            title="Stage a verified refactor"
            body="Rename a symbol, change a parameter list, or apply a code fix. The refactor comes back as a diff only when the compiler reports no new diagnostic."
            example="Example: add a CancellationToken parameter and update every call site."
          />
          <TaskCard
            number="06"
            title="Check a package upgrade"
            body="Give Fuse a NuGet package id and two versions. It returns the code in your solution that the upgrade would break."
            example="Example: see what breaks before moving Polly from 7.x to 8.x."
          />
        </div>

        <p className="mt-8 text-sm text-fd-muted-foreground">
          See the{' '}
          <Link
            href="/docs/reference/mcp-tools"
            className="font-medium text-fd-foreground underline underline-offset-4"
          >
            tool reference
          </Link>{' '}
          for command shapes, limits, and result fields.
        </p>
      </section>

      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-3xl">
            <p className="text-sm font-medium text-[var(--brand)]">Related tools</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              Code indexes and graphs are an existing category
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              <Link className="font-medium text-fd-foreground underline underline-offset-4" href="https://github.com/CodeGraphContext/CodeGraphContext">CodeGraphContext</Link>{' '}
              builds a local multi-language code graph,{' '}
              <Link className="font-medium text-fd-foreground underline underline-offset-4" href="https://github.com/oraios/serena">Serena</Link>{' '}
              exposes language-server-backed symbol operations, and{' '}
              <Link className="font-medium text-fd-foreground underline underline-offset-4" href="https://sourcegraph.com/code-search">Sourcegraph</Link>{' '}
              provides code search and navigation across repositories. Fuse concentrates on local
              .NET work through MSBuild and Roslyn: framework wiring, reduced scoped source,
              proposed-file compiler checks, change impact, and covering-test selection. It can run
              alongside a coding client&apos;s built-in index or another search tool.
            </p>
            <p className="mt-4 text-sm text-fd-muted-foreground">
              The{' '}
              <Link className="font-medium text-fd-foreground underline underline-offset-4" href="/docs/project/benchmarks#peer-comparison-fuse-versus-codegraph-coa-codesearch-and-serena">
                bounded peer comparison
              </Link>{' '}
              records its sample sizes and limits. It is not a general ranking of these tools.
            </p>
          </div>
        </div>
      </section>

      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-2xl">
            <p className="text-sm font-medium text-[var(--brand)]">Compared on the recorded corpus</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              Recorded corpus comparisons
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Rows from the benchmark suites on pinned repositories. They describe that sample,
              not every .NET project.
            </p>
          </div>

          <div className="home-compare mt-10" role="table">
            <div className="home-compare__row home-compare__row--head" role="row">
              <div role="columnheader">Question</div>
              <div role="columnheader">Grep or write-then-build</div>
              <div role="columnheader">Fuse (recorded)</div>
            </div>
            <CompareRow
              question="Which implementation runs for this interface?"
              baseline="Finds the interface name, not the DI registration"
              fuse="24 of 24 wiring edges on OrderingApp; 0 false positives"
            />
            <CompareRow
              question="What context does this branch need?"
              baseline="67% changed-file recall, 8% precision (grep baseline on 69 PRs)"
              fuse="93.4% precision, median 1,026 returned tokens; changed files kept by construction"
            />
            <CompareRow
              question="Will this proposed file compile?"
              baseline="Write the file, then run dotnet build"
              fuse="fuse_check before write; 0 of 1,000 compiler-labeled edits reported clean when broken"
            />
            <CompareRow
              question="Did the agent finish correctly?"
              baseline="82% pass@1 on scored rollouts; 9 false-done"
              fuse="89% pass@1; 8 false-done; build+test counts 3.1 vs 3.2 (essentially equal)"
            />
          </div>
        </div>
      </section>

      <section className="mx-auto w-full max-w-6xl px-6 py-20">
        <div className="mx-auto grid w-full max-w-6xl gap-10 lg:grid-cols-[0.8fr_1.2fr] lg:items-center">
          <div>
            <p className="text-sm font-medium text-[var(--brand)]">Recorded results</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              Honest checks on a defined sample
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The 1,000-edit compiler gate generated 500 breaking and 500 neutral single-file edits
              over OrderingApp and compared Fuse&apos;s verdict to the compiler label. Fuse reported
              every breaking edit and rejected none of the neutral edits.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              The agent-loop run scored edits against each task&apos;s own tests after the agent
              finished. Fuse passed on the first attempt in 89 percent of scored rollouts versus 82
              percent for native tools, with overlapping confidence intervals. Fuse declared success
              on a failing edit 8 times versus 9. Agent-visible build and test calls were 3.1 versus
              3.2, so the measured edge is fewer wrong finishes, not half the builds.
            </p>
            <p className="mt-4 text-sm text-fd-muted-foreground">
              Read the methods, weaker modes, and reproduction commands on the{' '}
              <Link
                href="/docs/project/benchmarks"
                className="font-medium text-fd-foreground underline underline-offset-4"
              >
                benchmarks page
              </Link>
              .
            </p>
          </div>

          <div className="grid gap-5 sm:grid-cols-2">
            <Evidence value="500 of 500" label="Breaking edits reported" />
            <Evidence value="500 of 500" label="Neutral edits accepted" />
            <Evidence value="89%" label="pass@1, Fuse arm (scored rollouts)" />
            <Evidence value="1,026" label="Median review tokens on 69 PRs" />
          </div>
        </div>
      </section>

      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl gap-10 px-6 py-20 lg:grid-cols-2">
          <div>
            <p className="text-sm font-medium text-[var(--brand)]">Runtime and verification behavior</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              Local analysis with graded compiler results
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              Fuse is a .NET 10 global tool. Any MCP-compatible agent connects through the
              protocol, including Cursor, Claude Code, Copilot, and others. Source and the derived
              index stay on your machine. Analysis can work offline; the optional update check and
              repository package feeds are the network-dependent exceptions. Read and check operations
              do not change working files unless you call the explicit apply path.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              Skeleton reduction removed 38 to 44 percent of tokens on four recorded repositories
              while keeping every public and protected type name and 96.3 to 99.4 percent of method
              names. That keeps survey and context calls smaller than returning whole files.
            </p>
          </div>
          <div className="rounded-xl border border-fd-border bg-fd-card p-7">
            <h3 className="text-lg font-semibold">Verification grades</h3>
            <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
              Every answer names how it was produced. Oracle grade checks against compiler state
              captured from the real build. Build grade runs a scoped{' '}
              <code className="font-mono text-fd-foreground">dotnet build</code> and returns those
              diagnostics. If neither path can run, Fuse abstains and names what is missing instead
              of guessing.
            </p>
            <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
              The opt-in resident workspace answers repeated{' '}
              <code className="font-mono text-fd-foreground">fuse_check</code> calls in about 31 ms
              at the median on the recorded NodaTime run. That path does not replace normal builds
              or tests before merge.
            </p>
            <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
              Open-ended file ranking from a title alone recalls 37.7 percent of changed files on the
              recorded corpus. Anchor on Git, a symbol, a route, or a service when you can.
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}

function HeroHighlight({
  value,
  title,
  detail,
}: {
  value: string;
  title: string;
  detail: string;
}) {
  return (
    <div className="home-hero-stat">
      <div className="text-3xl font-bold tracking-tight text-[var(--brand)]">{value}</div>
      <p className="mt-2 text-base font-semibold">{title}</p>
      <p className="mt-2 text-sm leading-6 text-fd-muted-foreground">{detail}</p>
    </div>
  );
}

function WiringResolveExample() {
  return (
    <div className="home-example" aria-label="Example of resolving a service to its implementation">
      <div className="home-example__header">
        <span>Resolve wiring</span>
        <strong>Which type handles IOrderService?</strong>
      </div>
      <div className="home-example__exchange">
        <div className="home-example__turn">
          <p className="home-example__speaker">Text search</p>
          <code>IOrderService appears in 14 files</code>
        </div>

        <div className="home-example__turn">
          <p className="home-example__speaker">fuse_find kind=service</p>
          <div className="home-example__result home-example__result--ok">
            <code>Registered implementation: OrderService</code>
            <code>Constructor injection in CheckoutHandler</code>
            <code>3 direct callers in production code</code>
          </div>
        </div>

        <p className="home-example__contrast">
          Grep finds the interface name. Fuse walks the DI registration to the type that runs.
        </p>
      </div>
    </div>
  );
}

function FlowStep({
  number,
  title,
  children,
}: {
  number: string;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <li className="home-flow__step">
      <span className="home-flow__number" aria-hidden="true">
        {number}
      </span>
      <h3 className="mt-5 text-lg font-semibold">{title}</h3>
      <p className="mt-2 text-sm leading-6 text-fd-muted-foreground">{children}</p>
    </li>
  );
}

function WarmIndexCard({ title, body }: { title: string; body: string }) {
  return (
    <article className="rounded-xl border border-fd-border bg-fd-background p-7">
      <h3 className="text-lg font-semibold">{title}</h3>
      <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">{body}</p>
    </article>
  );
}

function CompareRow({
  question,
  baseline,
  fuse,
}: {
  question: string;
  baseline: string;
  fuse: string;
}) {
  return (
    <div className="home-compare__row" role="row">
      <div className="home-compare__question" role="cell">
        {question}
      </div>
      <div className="home-compare__baseline" role="cell">
        {baseline}
      </div>
      <div className="home-compare__fuse" role="cell">
        {fuse}
      </div>
    </div>
  );
}

function TaskCard({
  number,
  title,
  body,
  example,
}: {
  number: string;
  title: string;
  body: string;
  example: string;
}) {
  return (
    <article className="rounded-xl border border-fd-border bg-fd-card p-7">
      <span className="font-mono text-sm font-semibold text-[var(--brand)]">{number}</span>
      <h3 className="mt-4 text-xl font-semibold">{title}</h3>
      <p className="mt-3 leading-7 text-fd-muted-foreground">{body}</p>
      <p className="mt-5 border-t border-fd-border pt-4 text-sm text-fd-muted-foreground">
        {example}
      </p>
    </article>
  );
}

function Evidence({ value, label }: { value: string; label: string }) {
  return (
    <div className="rounded-xl border border-fd-border bg-fd-background p-7">
      <div className="text-3xl font-bold tracking-tight text-[var(--brand)]">{value}</div>
      <div className="mt-3 text-sm text-fd-muted-foreground">{label}</div>
    </div>
  );
}
