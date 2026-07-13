# WiringBench adjudication protocol (B4)

This protocol defines what counts as a **correct** predicted semantic edge when adjudicating the
stratified corpus sample (`results/semantics-corpus-sample.json`, produced by
`fuse eval semantics --corpus-sample N --manifest tests/benchmarks/corpus-v2.json`). It exists so the
per-edge-type precision recorded in `results/semantics-corpus.json` (v2) is reproducible: two
adjudicators applying this protocol to the same sample reach the same labels.

Each sampled edge is `{ from, to, type, repo }`, where `from` and `to` are node ids (a symbol id, a
service type, a request type, a route template, or a config section). Adjudication resolves each id to
its source location in the pinned checkout and applies the per-type rule below. A label is one of:

- `correct` - the edge is a true relationship of that type in the source.
- `incorrect` - the edge is a false positive (no such relationship, or the wrong target).
- `unadjudicable` - the id cannot be resolved to source (a generated or external symbol not in the
  checkout), or the type is ambiguous by this protocol; excluded from the precision denominator and
  counted separately.

Precision per type = `correct / (correct + incorrect)`, with a Wilson 95% interval; `unadjudicable`
edges are reported but excluded from the ratio (and their share is reported, since a high
unadjudicable rate is itself a finding).

## Per-type rules

- **di_resolves_to** (service type -> concrete implementation): `correct` when the container is
  configured so that resolving `from` yields `to` at the pinned commit - an `AddX<From, To>()`, an
  `AddX<From>(sp => new To(...))` factory, a `TryAdd`, a Scrutor `.As<From>()` scan that includes `To`,
  or a keyed registration whose key and service match. `incorrect` when `to` is a different
  implementation than the one registered for `from`, or when no registration wires them. An open
  generic registration is `correct` only when `to` is a closed construction the registration produces.
- **di_injects** (type -> constructor-injected dependency): `correct` when `from`'s selected
  constructor (the DI-selected greediest resolvable one) has a parameter whose type is `to` (or a
  closed generic / `IEnumerable<to>` / `Lazy<to>` / `Func<to>`). `incorrect` when `to` is not a
  constructor parameter type of `from`.
- **mediatr_handles** (request -> handler): `correct` when `to` implements
  `IRequestHandler<from, _>` / `INotificationHandler<from>` / `IStreamRequestHandler<from, _>` for the
  request type `from`. `incorrect` when `to` handles a different request or is not a handler.
- **route_handles** (route template or HTTP method+path -> action): `correct` when `to` is the action
  method or minimal-API delegate the framework binds `from` to (attribute routing, conventional
  routing, `MapGet`/`MapPost`, gRPC service method, SignalR hub method). `incorrect` on a wrong action
  or a route that no action serves.
- **options_binds** (config section -> options type): `correct` when `to` is bound to the configuration
  section `from` via `Configure<To>(section)`, `.Bind`, `.Get<To>()`, or `AddOptions<To>().Bind(...)`.
  `incorrect` when the section binds a different type or is unbound.
- **implements** (type -> interface): `correct` when `from`'s declared base list (or a partial
  declaration) includes interface `to`, transitively through its own declared interfaces. `incorrect`
  otherwise.
- **inherits** (type -> base class): `correct` when `to` is `from`'s declared base class. `incorrect`
  otherwise.
- **references** (type -> referenced type): `correct` when `from`'s source names `to` in a member
  signature, field, base list, or attribute (the persisted reference relation). `incorrect` when the
  named type is not `to` (for example a same-name type in another namespace).
- **calls** (member -> invoked member): `correct` when `from`'s body invokes `to` (the exact overload).
  `incorrect` on a wrong overload or an uninvoked member.
- **tests** (test -> covered symbol): `correct` when the test `from` exercises `to` through a direct or
  transitive call within the test's project references. `incorrect` when no call path reaches `to`.
- **project_references** and structural edges are not sampled for precision (they are build facts, not
  inferred wiring); if present in a sample they are `unadjudicable` for this protocol.

## Stratification and sample size

Sample is stratified per `type` with a fixed count per type (seeded shuffle, `seed: 1469`, via
`EdgeSampler`), targeting at least 200 edges total across the wiring types present in the corpus-v2
semantic graphs. Types absent from the corpus (no predicted edges) are recorded as absent, not padded.

## Auto-adjudication note

Where an edge's `from`/`to` resolve to source and the rule above is mechanically decidable (implements,
inherits, di_injects, mediatr_handles by interface shape), an adjudicator may pre-label programmatically
and record the method; the semantically ambiguous types (di_resolves_to with scans/factories,
route_handles with conventional routing) require human judgment. The recorded table names which types
were auto-adjudicated versus human-adjudicated so the precision numbers are honest about their method.
