import type { Metadata } from 'next';
import Link from 'next/link';
import { Button } from '@/components/ui/button';
import { githubUrl } from '@/lib/shared';

const siteUrl = 'https://fuse.codes';

export const metadata: Metadata = {
  title: {
    absolute: 'Fuse - safer .NET changes with Cursor, Claude Code, and Copilot',
  },
  description:
    'Fuse helps coding agents check proposed .NET changes, trace handlers and routes, inspect callers, and run relevant tests from your local workspace.',
  alternates: {
    canonical: siteUrl,
  },
  openGraph: {
    type: 'website',
    url: siteUrl,
    title: 'Fuse - safer .NET changes with Cursor, Claude Code, and Copilot',
    description:
      'Check proposed .NET changes, trace the code that runs, inspect callers, and run relevant tests.',
    siteName: 'Fuse',
    images: [
      {
        url: '/fuse-social-card.png',
        width: 1280,
        height: 640,
        alt: 'Fuse helps coding agents check .NET changes before applying them',
      },
    ],
  },
  twitter: {
    card: 'summary_large_image',
    title: 'Fuse - safer .NET changes with Cursor, Claude Code, and Copilot',
    description:
      'Check proposed .NET changes, trace the code that runs, inspect callers, and run relevant tests.',
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
    'A local .NET developer tool that helps coding agents check proposed changes, trace application wiring, inspect impact, and run relevant tests.',
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

      <section className="border-b border-fd-border">
        <div className="mx-auto grid w-full max-w-6xl gap-12 px-6 py-16 md:py-20 lg:grid-cols-[0.88fr_1.12fr] lg:items-center">
          <div>
            <p className="text-sm font-medium text-[var(--brand)]">MCP server for .NET</p>
            <h1 className="mt-3 max-w-2xl text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
              Your agent&apos;s C# edits, compiler-checked before they land
            </h1>
            <p className="mt-6 max-w-xl text-lg leading-8 text-fd-muted-foreground">
              Fuse lets your coding agent ask the local .NET compiler about a proposed edit before
              writing it. The same typed index follows dependency injection, routes, and handlers
              to the code that actually runs.
            </p>

            <div className="mt-8 flex flex-wrap gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/connect-your-ai">Connect Fuse</Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <Link href="/docs/project/benchmarks">See the benchmarks</Link>
              </Button>
            </div>

            <p className="mt-5 max-w-xl text-sm text-fd-muted-foreground">
              In the recorded 1,000-edit compiler test, zero broken edits were reported clean.
            </p>

            <div className="mt-8 grid max-w-xl gap-4" aria-label="Install and connect Fuse">
              <Command label="Install">dotnet tool install -g Fuse</Command>
              <Command label="Connect and add agent rules">fuse mcp install --rules</Command>
            </div>

            <p className="mt-5 text-sm text-fd-muted-foreground">
              Read the{' '}
              <Link
                href="/docs/start/what-is-fuse"
                className="font-medium text-fd-foreground underline underline-offset-4"
              >
                technical details
              </Link>{' '}
              or view the{' '}
              <a
                href={githubUrl}
                target="_blank"
                rel="noreferrer"
                className="font-medium text-fd-foreground underline underline-offset-4"
              >
                source on GitHub
              </a>
              .
            </p>

            <p className="mt-6 flex max-w-xl flex-wrap gap-x-5 gap-y-1 text-xs text-fd-muted-foreground">
              <span>Works with Cursor, Claude Code, and GitHub Copilot</span>
              <span>Apache 2.0</span>
              <span>Runs locally</span>
            </p>
          </div>

          <ToolExchangeExample />
        </div>
      </section>

      <section className="border-b border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-6xl px-6 py-20">
          <div className="max-w-2xl">
            <p className="text-sm font-medium text-[var(--brand)]">One local feedback loop</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              Ask, check, then edit
            </h2>
            <p className="mt-4 text-lg text-fd-muted-foreground">
              Fuse runs beside your editor and works with the solution already on your machine.
              Your existing coding agent stays in control of the task and the final edit.
            </p>
          </div>

          <ol className="home-flow mt-10" aria-label="How Fuse works">
            <FlowStep number="1" title="Your agent asks a precise question">
              Which handler serves this request? What calls this method? Will this proposed file
              compile?
            </FlowStep>
            <FlowStep number="2" title="Fuse checks the local solution">
              It reads the current code, project relationships, and compiler information on your
              machine.
            </FlowStep>
            <FlowStep number="3" title="Your agent gets a concrete answer">
              It receives the implementation, callers, compiler errors, or relevant tests before
              deciding what to change.
            </FlowStep>
          </ol>
        </div>
      </section>

      <section className="mx-auto w-full max-w-6xl px-6 py-20">
        <div className="max-w-2xl">
          <p className="text-sm font-medium text-[var(--brand)]">Daily .NET work</p>
          <h2 className="mt-2 text-3xl font-semibold tracking-tight">
            Six checks that prevent common agent mistakes
          </h2>
        </div>

        <div className="mt-10 grid gap-5 md:grid-cols-2">
          <TaskCard
            number="01"
            title="Check a proposed change"
            body="Typecheck a single-file edit before it is written. If the compiler rejects it, the agent gets the errors and can revise the proposal."
            example="Example: catch a missing constructor argument before Program.cs changes."
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
            tool reference and technical diagrams
          </Link>{' '}
          for command shapes, limits, and result fields.
        </p>
      </section>

      <section className="border-y border-fd-border bg-fd-card/40">
        <div className="mx-auto grid w-full max-w-6xl gap-10 px-6 py-20 lg:grid-cols-[0.8fr_1.2fr] lg:items-center">
          <div>
            <p className="text-sm font-medium text-[var(--brand)]">Recorded results</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              In a 1,000-change compiler test, every broken change was caught
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The test generated 500 breaking edits and 500 non-breaking edits, then compared
              Fuse&apos;s answer with the .NET compiler. Fuse reported all 500 broken edits and did
              not reject any of the 500 non-breaking edits.
            </p>
            <p className="mt-4 text-fd-muted-foreground">
              In a separate agent-loop comparison, edits from the Fuse arm passed the project&apos;s
              own tests on the first attempt in 89 percent of scored runs versus 82 percent for
              native tools, and Fuse declared success on an edit that failed the tests less often
              (8 versus 9). The confidence intervals overlap, and build counts were essentially
              equal.
            </p>
            <p className="mt-4 text-sm text-fd-muted-foreground">
              These are bounded tests on the recorded OrderingApp sample and task corpus. They do
              not claim that Fuse catches every possible error in every repository.{' '}
              <Link
                href="/docs/project/benchmarks"
                className="font-medium text-fd-foreground underline underline-offset-4"
              >
                Read the methodology and scope
              </Link>
              .
            </p>
          </div>

          <div className="grid gap-5 sm:grid-cols-2">
            <Evidence value="500 of 500" label="Breaking changes reported" />
            <Evidence value="500 of 500" label="Non-breaking changes accepted" />
          </div>
        </div>
      </section>

      <section className="mx-auto grid w-full max-w-6xl gap-10 px-6 py-20 lg:grid-cols-2">
        <div>
          <p className="text-sm font-medium text-[var(--brand)]">Technical details</p>
          <h2 className="mt-2 text-3xl font-semibold tracking-tight">
            Local checks, with clear limits
          </h2>
          <p className="mt-4 text-fd-muted-foreground">
            Fuse is a .NET 10 global tool. Cursor, Claude Code, and compatible clients connect to
            it through the Model Context Protocol (MCP), a standard way for an agent to call local
            development tools.
          </p>
          <p className="mt-4 text-fd-muted-foreground">
            Source and index data stay on your machine. Read and check operations do not change
            working files. Only the explicit apply command can write a proposed single-file edit.
          </p>
        </div>
        <div className="rounded-xl border border-fd-border bg-fd-card p-7">
          <h3 className="text-lg font-semibold">What a compiler check means</h3>
          <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
            Every answer names how it was produced. Fuse calls this the verification grade:
            oracle grade checks against compiler state captured from the real build, build grade
            runs a scoped <code className="font-mono text-fd-foreground">dotnet build</code> and
            returns those diagnostics, and if neither path can run, Fuse abstains and names what
            is missing instead of guessing.
          </p>
          <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
            Warm answers are fast: on the recorded NodaTime run, an exact symbol lookup took 2.2
            milliseconds at the median and the opt-in resident check answered in 31 milliseconds.
            Timings are environment-dependent.
          </p>
          <p className="mt-3 text-sm leading-6 text-fd-muted-foreground">
            Fuse does not claim to remove all builds or prove that an application behaves
            correctly. Use normal builds, tests, and review before merging.
          </p>
        </div>
      </section>

      <section className="border-t border-fd-border bg-fd-card/40">
        <div className="mx-auto w-full max-w-4xl px-6 py-20 text-center">
          <h2 className="text-3xl font-semibold tracking-tight">
            Check the next .NET change before it is applied
          </h2>
          <p className="mx-auto mt-4 max-w-2xl text-fd-muted-foreground">
            Install Fuse, connect it to your coding agent, and use it in the repository you already
            have open.
          </p>
          <div className="mt-8 flex flex-wrap justify-center gap-3">
            <Button asChild size="lg">
              <Link href="/docs/start/connect-your-ai">Connect Fuse</Link>
            </Button>
            <Button asChild size="lg" variant="secondary">
              <Link href="/docs/start/quickstart">Open the quickstart</Link>
            </Button>
          </div>
        </div>
      </section>
    </main>
  );
}

function Command({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <p className="mb-2 text-xs font-medium uppercase tracking-wide text-fd-muted-foreground">
        {label}
      </p>
      <pre className="overflow-x-auto rounded-lg border border-fd-border bg-fd-card px-4 py-3 font-mono text-sm text-fd-foreground">
        {children}
      </pre>
    </div>
  );
}

function ToolExchangeExample() {
  return (
    <div className="home-example" aria-label="Example of one edit checked by fuse_check before it is written">
      <div className="home-example__header">
        <span>One checked edit</span>
        <strong>Update the order total in OrderService.cs</strong>
      </div>
      <div className="home-example__exchange">
        <div className="home-example__turn">
          <p className="home-example__speaker">Agent proposes, before writing the file</p>
          <code>return order.TotalAmount * quantity;</code>
        </div>

        <div className="home-example__turn">
          <p className="home-example__speaker">fuse_check answers from the compiler</p>
          <div className="home-example__result home-example__result--error">
            <code>CS1061: &apos;Order&apos; has no member &apos;TotalAmount&apos;</code>
            <code>Repair: &apos;Total&apos; exists on Order</code>
          </div>
        </div>

        <div className="home-example__turn">
          <p className="home-example__speaker">Agent revises and checks again</p>
          <div className="home-example__result home-example__result--ok">
            <code>return order.Total * quantity;</code>
            <strong>No diagnostics. The file is written once, correctly.</strong>
          </div>
        </div>

        <p className="home-example__contrast">
          Without Fuse, the first version lands and the build fails after the edit.
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
