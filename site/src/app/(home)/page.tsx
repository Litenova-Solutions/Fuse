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
            <h1 className="max-w-2xl text-4xl font-bold tracking-tight md:text-5xl lg:text-6xl">
              Catch the wrong edit before it reaches your code
            </h1>
            <p className="mt-6 max-w-xl text-lg leading-8 text-fd-muted-foreground">
              A coding agent can guess which service or handler runs, change the wrong file, and
              discover compiler errors after the edit. Fuse helps Cursor, Claude Code, and Copilot
              check the proposed change against your local .NET solution first.
            </p>

            <div className="mt-8 flex flex-wrap gap-3">
              <Button asChild size="lg">
                <Link href="/docs/start/connect-your-ai">Connect Fuse</Link>
              </Button>
              <Button asChild size="lg" variant="secondary">
                <a href={githubUrl} target="_blank" rel="noreferrer">
                  View source
                </a>
              </Button>
            </div>

            <div className="mt-9 grid max-w-xl gap-4" aria-label="Install and connect Fuse">
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
              or review the{' '}
              <Link
                href="/docs/project/benchmarks"
                className="font-medium text-fd-foreground underline underline-offset-4"
              >
                benchmark methods
              </Link>
              .
            </p>
          </div>

          <BeforeAfterExample />
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
            Four checks that prevent common agent mistakes
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
        </div>

        <p className="mt-8 text-sm text-fd-muted-foreground">
          The landing page keeps the flow simple. See the{' '}
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
            <p className="text-sm font-medium text-[var(--brand)]">Recorded compiler test</p>
            <h2 className="mt-2 text-3xl font-semibold tracking-tight">
              In a 1,000-change compiler test, every broken change was caught
            </h2>
            <p className="mt-4 text-fd-muted-foreground">
              The test generated 500 breaking edits and 500 non-breaking edits, then compared
              Fuse&apos;s answer with the .NET compiler. Fuse reported all 500 broken edits and did
              not reject any of the 500 non-breaking edits.
            </p>
            <p className="mt-4 text-sm text-fd-muted-foreground">
              This is a bounded test on the recorded OrderingApp sample. It does not claim that
              Fuse can catch every possible error in every repository.{' '}
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
            Fuse uses saved compiler state when that state is available. Otherwise, it runs a
            scoped <code className="font-mono text-fd-foreground">dotnet build</code> and returns
            those diagnostics. If neither path can run, Fuse reports that it could not check the
            edit.
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

function BeforeAfterExample() {
  return (
    <div className="home-example" aria-label="Example of a coding agent working without and with Fuse">
      <div className="home-example__header">
        <span>Example task</span>
        <strong>Add cancellation to checkout</strong>
      </div>
      <div className="home-example__grid">
        <section className="home-example__panel" aria-labelledby="without-fuse">
          <p id="without-fuse" className="home-example__eyebrow">
            Without Fuse
          </p>
          <div className="home-example__line">
            <span>Guessed file</span>
            <code>CheckoutService.cs</code>
          </div>
          <div className="home-example__line">
            <span>Edit</span>
            <code>Add CancellationToken</code>
          </div>
          <div className="home-example__result home-example__result--error">
            <strong>Build fails after the edit</strong>
            <code>CS7036: required argument missing</code>
          </div>
        </section>

        <section className="home-example__panel" aria-labelledby="with-fuse">
          <p id="with-fuse" className="home-example__eyebrow">
            With Fuse
          </p>
          <div className="home-example__line">
            <span>Route</span>
            <code>POST /checkout</code>
          </div>
          <div className="home-example__line">
            <span>Runs</span>
            <code>CheckoutCommandHandler.Handle</code>
          </div>
          <div className="home-example__result home-example__result--ok">
            <strong>Proposed edit checked first</strong>
            <code>Callers and compiler errors returned</code>
          </div>
        </section>
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
