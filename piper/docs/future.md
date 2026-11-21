# Future requirements

Work planned for after 1.0. Under the compatibility promise every item must be an addition: no 1.x release may stop a
program compiling or change what it means.

## Builds

### Remove code only tests use

A normal build leaves out `test` blocks, but compiles everything they call. Functions, records, enums, traits and
methods used only by tests still end up in the binary.

**Requirement:** a normal build (`piper build`, `piper run`, Piper.Build) leaves out every declaration reachable only
from tests. The binary should hold the bare minimum the program needs.

- **Roots:** what the program can reach without tests: `Main`, every public declaration when building a library (C# and
  other assemblies may call it), and whatever .NET reaches by reflection or attributes (see below).
- **Reachability:** follow calls, constructions, types named in signatures and fields, trait implementations
  (a trait method is reachable if the trait is and the type is), operators, extend blocks, and functions used as values.
- **Scope:** private and test-only declarations in an executable can go. In a library, only declarations private to a
  file and unreachable from its public ones can, since anything public may be called from outside.
- **Unsafe cases:** code .NET finds by reflection (serializers reading properties, `[ModuleInitializer]`, attribute-driven
  frameworks) must stay. Removing whole declarations, not record fields or properties, avoids most of this.
- **Checking:** `piper build --exe` on a program with test-only helpers produces an assembly without them. The output of
  every sample and golden program is unchanged, and the binary is smaller or the same.
- **Later:** extend the same analysis to remove anything unreachable, test-only or not, behind an option, since a
  library's public API must stay intact.

### References in a Piper.Build project

`piper.json`'s `"packages"`, `"frameworks"` and `"references"` work for a program folder. In a project using Piper.Build,
the `.piper` files are still only checked against .NET's own assemblies, not the project's `PackageReference`s. The
build should pass the project's resolved references (`@(ReferencePath)`) to the compiler, so Piper code there can use
them too.

## Language

- **`const` declarations,** usable in patterns and attribute arguments (`const` is reserved).
- ~~**`using` on an Option**~~ (found writing the actor tracing test), done: `using let span = source.StartActivity("x");` (an `Activity?`) disposes it if there is one, as C#'s `using` skips a null.
- **Spans:** `Span<T>` and `ReadOnlySpan<T>` (and ref structs generally), so APIs such as `AsSpan` and span overloads can be called. They can't be captured by lambdas or live across `await`, as in C#.
- **Namespace-qualified variants in patterns** (`Library.Loans.LoanError.UnknownBook(code)`).
- **Explicit attribute targets** (`[return: …]`), and `[Obsolete]` warnings for using an obsolete enum variant.
- **More contracts:** `requires` on trait methods (implementations must not demand more than the trait does), invariants on
  enum variants, and postconditions (`ensures result >= 0`).
- **Obsolete warnings for setting obsolete .NET properties and subscribing to obsolete events** (reading and calling are
  already reported).
- ~~**.NET's `T?` through a Piper type parameter**~~ (found by apps/tracker-desktop), done: in `fn Read<T>(…) => T? =
  JsonSerializer.Deserialize<T>(…)` the result is a `T?`, with null as None, for results, properties, `out` parameters
  and tasks, and a `T?` passes to .NET the other way.
- ~~**`AssertEqual(Ok(age), ParseAge(text))`**~~ (found by samples/testing.piper), done: an argument with no type of
  its own (`None`, `Ok(…)`, `[]`) takes it from a generic call's other arguments, wherever it comes.
- ~~**Async event handlers**~~ (found by apps/tracker-desktop), done: `button.Click += async (_, e) => { await Save(); }`
  is C#'s `async void`, allowed as an event's handler only (for Piper's events and .NET's); elsewhere an async lambda
  returning nothing is still an error. What it throws surfaces on the event's synchronization context, as in C#.
  Still to consider: reporting a handler's failure through a hook, as `[Command]`'s `OnCommandFailed` does.
- **Found by apps/auction:**
  - ~~**Helper methods in a `statemachine`** (and an `actor`)~~, done: a machine can declare `fn`s, its class's methods.
    And in an `actor`, as local functions where its loop starts, beside its state.
  - ~~**A list literal's `None`s typed by a later `Some`**~~, done: elements with no type of their own take the others'.
  - ~~**`[NotNullIfNotNull]` results**~~, done: given a plain value, the result is one.
- **Newtypes, later:** a newtype with type parameters, for phantom-typed ids (`newtype Id<T> = Guid;`, so an
  `Id<Issue>` isn't an `Id<Project>`); and newtypes as JSON dictionary keys (`Dictionary<IssueId, …>` written as an
  object, which needs the converter's property-name methods).
- **Interpolation that escapes for where it's going.** Markup escapes its holes by type, so cross-site scripting can't
  be written by accident; the same for the other places text goes would do that for injection generally.
  ~~`sql$"…"`~~, ~~`url$"…"`~~ and ~~`shell$"…"`~~ are done: `sql$`'s holes are parameters, `url$`'s are encoded for
  their part of the URL, `shell$`'s are each one argument of a program run without a shell, a plain `$"…"` is one of
  these where it's wanted, and a library adds a kind with `[InterpolationKind]` (see
  [SQL strings](language.md#sql-strings), [URL strings](language.md#url-strings) and
  [shell strings](language.md#shell-strings)). Still to come: kinds declared in Piper (they're .NET types today),
  enums in holes, a newtype inside an Option or list in a `sql$` hole (`url$` and `shell$` take them, as formattable
  values; `sql$` needs the value itself, so the binder would have to unwrap it), and an unclosed quote in a `shell$`
  literal reported by the compiler rather than when the command is made. And `Url` overloads for the web plugin's `htmx.Redirect`, `PushUrl` and `ReplaceUrl`, which
  take strings: adding them beside the string ones would make each `$"…"` given to them a warning (PIP0406), so they'd
  need to replace them, or the warning to know when a hole can't do harm.
- **Typestate from `statemachine`.** A machine's state as part of its type: `Order<Unpaid>`, `Order<Paid>`, where
  `order.Ship()` on an unpaid one is a compile error rather than a refused transition at run time, and each
  transition returns the machine in its next state. Protocols (pay, then pack, then ship; open before send) checked
  where they're written. For a machine kept in a variable whose state isn't known statically (an actor's), it stays
  as today, with `match` on its state giving the typed one.
- **Error sets inferred** (as Zig's): `fn Place(…) => Result<Order, _>` gets as its error the union of what its `?`s
  can raise (`StockError | PaymentError`), so a `match` on it must handle exactly those, and a new failure deep down
  shows at every caller that doesn't. In C# it's a generated enum over the errors, or the errors' common trait.
- ~~**Transient errors, which retries understand**~~, done (see [Transient errors](language.md#transient-errors)):
  `[Transient]` on a variant, an enum or a record; `[Retry]` on a function returning a `Result` tries again on a
  transient `Err` and gives the rest back at once, `[CircuitBreaker]` counts only transient `Err`s as failures, a
  workflow's activity is retried on one, and a `[Retry]` whose errors can never be transient is a warning. Still to
  come: asks to remote actors (once those take policies, item 4 of [Cloud and distributed](#cloud-and-distributed));
  a function returning a type alias for a `Result`, which the decorators, reading syntax, don't see as one; and, with
  inferred error sets (above), a transient failure deep down staying one in every caller's error.
- **`requires` proven where it can be.** Constant checking already reports a `requires` that constant arguments
  break. Following the code's flow, the compiler could also know that after `if amount > 0m`, `Money(amount)` can't
  fail (and leave the check out), and warn where a `requires` must fail on some path. A light refinement type, with
  nothing new to write: newtypes and their `requires` are the refinements.
- **Nested updates in a spread:** `{ ...order, address.city: "Leeds" }`, rather than a spread inside a spread for
  each level of records within records; `{ ...board, cells[i].worker: "a" }` for a list's item, perhaps, later.
- **String-literal enums:** `enum Swap { "innerHTML", "outerHTML", "none" }`, which are their strings in JSON, markup
  attributes and parsing, but checked as any enum (and matched exhaustively). Web APIs are full of such strings
  (htmx's `hx-swap`, CSS values, statuses); the markup completion's values could come from them.
- **What can cross between threads** (Swift's `Sendable`), the check [weighed in place of `pure`](#ideas-from-other-languages):
  immutable records, newtypes, enums and immutable collections can be shared; a class with `mut` fields can't, unless
  it's an actor or keeps its state behind a `lock`. Inferred, with nothing to write. Then an actor's messages, a
  `TaskScope`'s tasks, `Task.Run` lambdas and the parallel plugin's are all checked one way, for what they capture and
  send, in place of today's separate warnings (the actor plugins' on mutable message fields, the data-race checks).
- ~~**Duration literals**~~, done (see [Durations](language.md#durations)): `30s`, `500ms`, `1.5s`, `5min`, `2h`,
  `1day`, as `TimeSpan`s, exact to the tick, in expressions and patterns (exhaustiveness checked, as for integers), and
  taken by `[Timeout]`, `[Retry]` and `[CircuitBreaker]`. Days are `day`/`days`, as `1d` is already C#'s double. Still
  to come: durations in the plugins' clauses that read `shutdown 30 seconds` today (a server's, an actor's), and in
  `const` declarations once there are any.
- **Regex literals, checked, with typed captures:** `re"(?<year>\d{4})-(?<month>\d\d)"` is a compiled `Regex`, and an
  invalid pattern is a compile error. As a pattern, its named groups bind variables:
  `if line is re"(?<year>\d{4})-(?<month>\d\d)"(year, month) { … }`, and in a `match` arm. Alongside `sql$`, `url$`
  and `shell$`: text whose meaning the compiler knows.
- **A disposable never disposed is a warning,** as a task nothing waits for is (PIP0409): an `IDisposable` made and
  dropped without `using`, `defer`, being returned or stored, or passed to something that takes ownership. .NET's own
  analyzers miss most of these in generated code; the compiler sees them where they're written.
- **Patch types for `PATCH` endpoints:** `Patch<Issue>`, derived from a record, has each field optional (a field that's
  already optional, a `T??`: left alone, or set to None), and `patch.ApplyTo(issue)` gives the changed record, checking
  its `requires` again, or why not. JSON reads only the fields sent, and the OpenAPI schema says so.
  `patch "/issues/{id:int}" async (id: IssueId, changes: Patch<Issue>, db: TrackerDb) => …` with no hand-written
  record of optional fields per resource.

### Ideas from other languages

Weighed against what the apps needed (see each); in rough order of value for the cost.

- ~~**Property-based tests**~~ (QuickCheck, Hypothesis, FsCheck), done: `test "name" for (a: int in 1..500, …) { … }`
  runs the body over generated values and shrinks a failure to the smallest that fails, with a seed to repeat it
  (see [Property tests](language.md#property-tests)). apps/grid's "any order gives the same figures" and "every
  scenario is counted once" now check any order and any count, and apps/auction checks that bids never fall. Writing
  generators in Piper is done too (`PiperGen`'s `Select`, `Where`, `SelectMany`, `OneOf`, `ListOf` and `Shuffled`;
  the grid's order test uses `Shuffled`), as are ~~a per-test number of cases~~ (`runs 1000`) and ~~`PiperGen` without
  `using Piper.Runtime;`~~. Still to come: keeping the failures found, to try first next time (as Hypothesis's
  database does): each failure's shrunk choices kept in a file in the project, and replayed before new cases.
- **Structured concurrency** (Swift's `async let` and task groups, Kotlin's coroutine scopes): tasks started in a scope
  are awaited or cancelled when it ends, and their exceptions aren't lost. Designed below:
  [structured concurrency](#structured-concurrency-a-design).
- **Doc tests** (Rust, Elixir, Python): examples in `///` comments run by `piper test`, so documentation can't drift
  from the code. Piper has both doc comments and built-in tests, so it's mostly plumbing.
- **Units of measure** (F#), on hold: `decimal<GBP>`, `int<shares>`, checked at compile time and erased in the C#, so
  `value + holding.shares` is an error. Valuable for money and quantities (apps/grid multiplies shares, prices and
  volatilities), but unit arithmetic in the type checker is a large piece of work.
- **Weighed and not taken:**
  - **A checked `pure` keyword:** its strongest case, safety for actors and the parallel plugin, is about what crosses
    a boundary, which a `Sendable`-style check (as Swift's) addresses directly: the actor plugins already warn about
    mutable message fields, and lambdas given to the parallel plugin could be checked for captured `mut` variables.
    Purity itself would have caught nothing in the apps so far (the grid's determinism is held by a test), D's checked
    `pure` sees little use, and checking .NET calls needs a hand-kept list of pure members.
  - **Macros or compile-time evaluation** (Zig, Rust): plugins already do this job.
  - **Higher-kinded types, effect systems:** a large cost to learn, for little in the programs Piper is for.
  - **Active patterns** (F#): `match` with property and list patterns, and helper functions, covered what the apps
    needed.

### Structured concurrency: a design

**Status:** parts 1 and 2 are done: `TaskScope`, and the three checks. Part 3 is for later.

**The problem.** A task Piper code starts isn't tied to anything. It can outlive the code that started it, and if it
fails and nothing awaits it, its exception is lost. The apps work around this by hand, the same way each time:

- **apps/grid:** `main.piper` and `grid.tests.piper` each make a `CancellationTokenSource`, collect worker tasks in a
  list, then `stop.Cancel(); await Task.WhenAll(workers);`. If anything between starting the workers and that line
  throws (a failed assertion, in a test), the workers are never cancelled, and they keep running against a cluster
  the test has finished with.
- **apps/stock:** `load.piper` starts `concurrency` clients with `Task.WhenAll`. One client failing doesn't stop the
  others: the load runs to the end before the failure is seen.
- **The aspects plugin:** `[Command]` started an async method and dropped its task, so a failing command did nothing
  and said nothing, until `OnCommandFailed` was added.
- **Nothing warns:** calling an async function and not awaiting its task (`Save(order);` in an `async fn`) compiles
  silently. C# warns for that (CS4014); Piper doesn't.

**The proposal: a library first, then compiler checks.** As Piper's own advice says for a feature that's "run this
code, somehow": a runtime type used with a trailing closure, with no new syntax. The compiler then adds what only it can
check.

#### 1. `TaskScope`, in Piper.Runtime

```piper
await TaskScope.Run { scope =>
    for i in 1..3 {
        scope.Start { stop => RunWorkers(nodes[i], coordinator, $"worker-{i}", 1, stop) }
    }
    await GridServer(jobs, url).RunAsync();
    scope.Cancel();                    // the workers run until the server stops
}                                      // here every task the scope started has finished
```

- **`TaskScope.Run(body)`** runs `body` with a new scope, and finishes only when the body and every task it started
  have finished. `Run<T>` gives the body's value. An optional `CancellationToken` links the scope to an outer one.
- **`scope.Start(work)`** starts `work` (taking the scope's `CancellationToken`) as a child task, and returns its
  `Task<T>` (or `Task`) to await for its result if wanted. Awaiting it is optional: the scope waits for it anyway.
- **At the end of the body:** the scope waits for its children, as Kotlin's `coroutineScope`, Swift's task groups
  and Trio's nurseries do. Nothing is cancelled silently. Work that should stop calls `scope.Cancel()`, as grid's
  workers do above.
- **If the body throws:** the children are cancelled and awaited, then the body's exception is rethrown. A failed
  test can't leave workers running.
- **If a child fails** (with anything but cancellation): the scope is cancelled, so the other children and anything in
  the body using `scope.Token` stop. Once all have finished, `Run` throws the child's exception, the first if several
  failed, as `await Task.WhenAll` does.
- **Simulated:** `Start` runs its work through `PiperRouting.Run`, as `Task.Run` already does, so scopes work
  unchanged in `simulate` tests, and a seed repeats which child runs when.

What changes in the apps:

```piper
// apps/grid/grid.tests.piper. Before: a CancellationTokenSource, a list of tasks, and "cancel, then WhenAll" at the end.
let view = await TaskScope.Run { scope =>
    for i in 2..4 {
        scope.Start { stop => RunWorkers(cluster[i], coordinator.Address, $"w{i}", 1, stop) }
    }
    let view = await WaitForJob(jobs, 1);
    scope.Cancel();
    view
};

// apps/stock/load/load.piper. One client failing now stops the rest, and its exception is the one reported.
await TaskScope.Run { scope =>
    for _ in 0..<concurrency {
        scope.Start { stop =>
            while !stop.IsCancellationRequested && pending.TryDequeue(out let n) {
                outcomes.Add(await ReserveOne(http, nodes[n % nodes.Count], sku));
            }
        }
    }
};
```

Cancellation tokens stay explicit, as in C#: grid's `Work` loop still takes `stop` and checks it. The scope gives the
token rather than each caller making and threading a `CancellationTokenSource`.

#### 2. Compiler checks

- **An unobserved task is a warning:** a call whose `Task` is neither awaited, stored, returned nor passed on
  (`Save(order);`), anywhere, not only in `async fn`s as C#'s CS4014. The fix it suggests is `await`, or
  `scope.Start` if it should run alongside. `let _ = Save(order);` says the task is dropped on purpose.
- **Data races:** the lambdas a scope `Start`s run at once, so they're checked as the `Task.Run` lambdas given
  together to `Task.WhenAll` are now (a `List` they all `Add` to, a variable they all assign).
- **A scope can't escape:** `scope` stored in a field, returned, or captured by a lambda the scope doesn't run is a
  warning, as starting tasks on it after `Run` has finished would fail.

#### 3. Left for later

- **`async let`** (Swift): `async let a = Fetch(1); async let b = Fetch(2); Print(await a + await b);`, a child task
  per variable, in an implicit scope for the block. It's shorter for a fixed number of tasks, but it's new syntax C#
  doesn't have, and `TaskScope` covers the same cases. To reconsider once the apps have used scopes.
- **Implicit cancellation** (Kotlin's and Swift's): `Task.Delay` and other routed calls seeing the scope's
  cancellation without being given a token. `PiperRouting` could do it, but code that cancels without saying so is
  surprising to anyone who knows C#.
- ~~**Async event handlers**~~ ([above](#language)), done, as C#'s `async void`. A shared hook reporting their
  failures, as `[Command]`'s are, is still to consider.
- **Whichever happens first** (Go's `select`, made structured): waiting on several things, taking the first, and
  cancelling the others, as a `TaskScope` cancels its tasks:

  ```piper
  let outcome = await first {
      reply from jobs.Take(worker) => Got(reply),
      after 30s => TimedOut,
      stop.Cancelled => Stopping,
  };
  ```

  Each arm is awaited in the same scope; the first to finish gives the value, the rest are cancelled and waited for,
  and an arm that fails fails the whole. It replaces `Task.WhenAny` and the cancellation written around it, which
  apps/grid's leases and workers do by hand. Exhaustive in the sense that matters: every arm's type is the result's.

#### Decided

1. **The end of the body waits** for the children; nothing is cancelled unless `scope.Cancel()` asks.
2. **The name is `TaskScope`.**
3. **The unobserved-task warning applies everywhere**, not only in `async fn`s; `let _ =` drops a task on purpose.

**In order:** ~~`TaskScope`~~, done (see [Structured concurrency](language.md#structured-concurrency-taskscope)),
with ~~apps/grid and apps/stock moved to it~~ (grid's workers in its main, its tests and `RunWorkers`; stock's load
clients). An outer token comes first, `TaskScope.Run(stop) { scope => … }`, so the body stays a trailing closure.
So is ~~the unobserved-task warning~~ (PIP0409, see [Async and await](language.md#async-and-await)): no
code in the repository's samples, apps or their tests dropped a task, so it needed no `let _ =` anywhere.
`[ObservedTask]` on a .NET method exempts the task it gives (`scope.Start`). So are ~~the data-race grouping~~ (PIP0387
for a scope's tasks: each with the others, or with itself when started in a loop) and ~~the escape check~~ (PIP0410;
a local alias in the body, `scope.Token`, and passing the scope to a function aren't escapes).

## Production web and services

The `endpoints` and `services` plugins are thin layers over ASP.NET Core and Microsoft.Extensions.DependencyInjection.
To be production-ready they need Piper's own types to work at the boundary, and to use what the compiler knows (types,
contracts, the dependency graph) to catch at compile time what ASP.NET Core and DI only find when the program runs.

### Web, in order

Piper's types on the wire are done: Options, enums and invariants in JSON, optional handler parameters, and Options and
Results answered as 200, 404 or ProblemDetails (see [JSON](language.md#json) and [plugins](plugins.md)). What's left:

1. ~~**Testing.**~~ Done: `TestClient.Start(() => BuildApp())` (Piper.Web) serves an app from memory for `test`
   blocks, keeping cookies and sending forms' anti-forgery tokens (see [plugins](plugins.md#testing-a-web-app)). It
   found that a request ASP.NET Core couldn't bind broke the response filter (a 500, not a 400), now fixed.
2. **Endpoint metadata.** Authorization, anonymous access, rate limiting, CORS and output caching are done (`authorize`,
   `anonymous`, `ratelimit`, `cors` and `cache` on groups and endpoints, defined by a `policies` declaration), and so
   are ~~OpenAPI response types~~, from each handler's type (200 with its value, 404 for an Option, ProblemDetails with
   each status an error's `[Status]`es give), and ~~OpenAPI tags~~ (`tags "Books"`, or the declaration's name).
   ~~OpenAPI schemas for Piper's types~~ are done too, opted in to with the `openapi` plugin (Options as their value or
   null, newtypes as their value, enums as their variants), for Microsoft.AspNetCore.OpenApi; Swashbuckle would need
   a schema filter of its own. Still to come: per-user rate limits, partitioned by the signed-in user or the client's
   address rather than shared by everyone. ~~An optional query parameter described as its type~~ (found by
   samples/todo-api), done: `done: bool?` was described as a `string`, as it's bound by its Option's `TryParse`.
3. **Hosting and operations.** ~~A `server` declaration~~ is done (see [plugins](plugins.md#a-server)): services,
   policies and endpoints with their middleware in order, ProblemDetails, OpenAPI, health and readiness checks,
   `stopping` hooks and the shutdown timeout. Still to come:
   - ~~**Checking the server at compile time**~~, done: a handler, check or hook needing a service nothing in the
     server registers, a class DI makes needing one, a singleton taking a scoped service, and `authorize` with no
     authentication scheme. Still to come: cycles between services, and the same for `services` blocks used without
     a server (a console program's `ServiceCollection`), which would need its own idea of what's resolved.
   - ~~**Configuration in the server**~~, done: `config Smtp;` reads a `config` record as the app starts and registers
     it (and as `IOptions<Smtp>`). Still to come: reloading it when appsettings.json changes (`IOptionsMonitor`).
   - ~~**HTTPS**~~, done: `https;` redirects HTTP to HTTPS (with HSTS outside Development), and `https certificate
     path password p;` or `… key keyPath;` serves a .pfx or PEM certificate, from a `config` if wanted. ~~Trusting a
     TLS-ending proxy's forwarded headers~~ is done too: `forwarded;` (or `forwarded from "10.0.0.0/8";`), so `https`
     works behind an ingress. Still to come: `X-Forwarded-Host` and a path prefix (`X-Forwarded-Prefix`), for an app
     served under a path of another's host.
   - ~~**The other apps on it**~~, done: every web app is now a `server`. apps/stock and apps/grid moved their routes
     into `endpoints`, and their hand-written `/healthz` became liveness and readiness checks (their Kubernetes
     readiness probes use `/ready`); apps/auction's server takes its auctioneer, which Main and the tests make.
   - ~~**Publishing for a container**~~, done: `piper build --publish` writes the compiled app, its packages (native
     libraries for Linux) and a Dockerfile on .NET's runtime or aspnet image. Still to come: a self-contained build
     (the runtime packs for each platform, from NuGet), and building the image itself without Docker, as .NET's own
     container publishing does (base images pulled from a registry, layers written).

Server-side HTML belongs with [UI](#ui) (htmx over plain endpoints), not Razor.

### Services

- **Checking the dependency graph at compile time.** The plugin sees each implementation's constructor, so it can
  report what DI only finds at run time (and `ValidateOnBuild` only partly): a dependency nothing registers, a singleton
  depending on a scoped service (a captive dependency), and a cycle.
- **Several implementations of a service,** resolved as `IEnumerable<T>`, which the duplicate check now forbids; an
  explicit form (`many Handler => AuditHandler;`) keeps that check for mistakes.
- **Keyed services** (`singleton Cache["fast"] => MemoryCache;`, injected with `[FromKeyedServices("fast")]`) and
  **open generics** (`scoped Repository<> => SqlRepository<>;`).
- **Configuration and options:** typed configuration is done (the `config` plugin), and a `server` registers one with
  `config Smtp;` (as `IOptions<Smtp>` too), and so does a `host`, for programs without a web server. Still to come:
  reloading it when appsettings.json changes.
- **Databases:** the `data` plugin covers EF Core models and queries, and ~~registering a database with DI~~ is done: a
  server's or host's `database db: TrackerDb from "Database";` (or `(settings.provider, settings.connection)`), the
  provider chosen by configuration (sqlite, sqlserver, postgres), scoped, and `db` a factory for the clauses after
  (apps/tracker uses it), and so are ~~migrations~~ (a database's shape recorded and checked as it builds, and
  `piper db migrate` writing reviewed SQL scripts from it; see [plugins](plugins.md#migrations-a-databases-shape-and-the-sql-that-changes-it)).
  Still to come: relationships between tables (navigation properties), and comparing an optional column with an
  optional variable in a query.
- ~~**Background work**~~ in a `server`, done: `hosted Mailer;` (`AddHostedService`), and `worker` handlers whose
  parameters are services, run for the app's life or `every` period (apps/auction's clock is one), and in a `host` too.
- **Composition and testing:** a `services` block including another (a library's), and replacing a registration in a
  test (`services Test: AppServices { singleton Clock = FixedClock("noon"); }`).
- ~~**Hosts beyond the web**~~, done: the `hosting` plugin's `host`, over `Host.CreateApplicationBuilder`, gives console
  programs and workers configuration, logging and a lifetime, with a server's clauses (apps/grid's worker nodes are
  hosts). ~~Checking the services its handlers take~~, as a server's are, is done too (the checks are shared).

## Cloud and distributed

**Provider agnostic, always.** Every cloud-type service (secrets, queues, storage, event stores, functions, telemetry
backends) is defined by a trait or interface describing what it does, and the app assigns the implementation:
registered as a service, passed to `Spawn`, chosen by configuration at startup. A provider (Azure, AWS, Google Cloud, a
self-hosted broker) is an adapter or a package the app brings, never part of the language or a plugin's shape, so a
program moves between providers by changing its configuration, not its code. **And every one has a local
implementation** (in memory, a local file or database, a local container), so a program runs and is tested on a
developer's machine with no cloud at all. What's built so far follows this:

| Service | The abstraction | Local | Cloud, chosen at startup |
|---|---|---|---|
| Secrets and configuration | .NET's configuration sources (`sources`) | appsettings.json, the environment, files | Azure Key Vault, AWS Secrets Manager, GCP Secret Manager, HashiCorp Vault |
| Persisted actors' events | `IEventStore`, chosen by `Events:Provider` | `memory`; `sqlite` | `sqlserver` (Azure SQL, Amazon RDS), `postgres` (Amazon RDS and Aurora, Cloud SQL, Azure's) |
| Cluster membership | `IClusterMembership`, chosen by `Cluster:Provider` | `memory`; `static` (seed nodes); `sqlite` | `postgres`, `sqlserver` (leases in a database the nodes share) |
| Telemetry | OpenTelemetry's protocol (OTLP) | nothing, or Aspire's dashboard | any OTLP backend (Azure Monitor, AWS X-Ray, Google Cloud Trace, Jaeger) |
| Remote actors' transport | `IActorTransport`, chosen by `Actors:Provider` | `memory` | `http` (HTTP/2 on Kestrel, built in); `proto` (Proto.Actor's gRPC, with the proto-actor plugin) |

The choice is made by Piper.Runtime's provider registry (see [plugins](plugins.md#providers-chosen-by-configuration)):
a service's providers by name, the local one built in, others registered by the plugins a program brings, chosen by
the configuration's `Provider` at startup, with a server's or host's `provider` clause. Code takes the service as a
value, so nothing depends on dependency injection. Planned services start the same way: the messaging plugin's
transport trait, with an in-memory transport first and brokers as registered providers.

What [apps/stock](../apps/stock/README.md) and [apps/grid](../apps/grid/README.md) wrote by hand, running as pods in
Kubernetes, that the language or its plugins could provide. They also raise the priority of what's planned above:
health checks (both apps hand-write `/healthz`), graceful shutdown (a deleted grid pod abandons its chunk rather than
finishing it), container publishing, and hosted workers; and of actor clustering (see the remote actors plugin in
[plugins.md](plugins.md)). In the order they'd best be done:

1. **Checking message compatibility at compile time.** The remote actors plugin needs every node to run the same
   program, but a rolling update (how Kubernetes deploys, as the stock app's redeploys did) always has old and new pods
   talking for a while. A message that gains a required field, renames one or changes a field's type breaks them
   mid-rollout. Piper knows every message type, so `piper build` could keep their shapes in a checked-in file and report
   breaking changes against it: a field removed or retyped, a new field without a default, a handler removed. The same
   check suits records sent as JSON by an API. Cheap, and a check only the compiler is placed to make.
   ~~**For an `api`**~~, done: its shape is recorded in `Name.shape.json` and each change checked against it (see
   [plugins](plugins.md#api-an-apis-shape-and-a-typed-client)), through the compiler's `WireShapes`, which
   `PluginAnalysis.CheckWireShape` gives any plugin. Still to come:
   - ~~the remote actors plugin's messages and replies~~, done: each actor's in `Name.shape.json` (apps/stock and
     apps/grid record theirs);
   - `requires` tightened on a record callers send (old clients' values may now be refused);
   - generic records, whose fields are recorded in terms of their type parameters;
   - a server answering clients of more than one shape (versioned routes).
2. **Tracing through actors** (OpenTelemetry). The stock and grid apps needed hand-built dashboards and `servedBy`
   fields to see which node did what. The plugin generates each remote message's envelope, so it can carry the trace
   context: one request becomes one trace through HTTP, node A and the product actor on node B, with handler times and
   mailbox depth as metrics. Locally, .NET Aspire's dashboard shows them with no setup.
   ~~Done~~, mostly: a message carries its sender's trace in its headers, and each handler runs in a span
   continuing it, timed as `piper.actor.handler.duration` (Piper.Actors.Remote, on .NET's `Activity` and `Meter`). The
   new `telemetry` plugin's `telemetry;` (in a `server` or `host`) exports traces, metrics and logs over OTLP;
   apps/stock and apps/grid use it, and a request to stock is one trace through HTTP, the warehouse and the product.
   Still to come:
   - mailbox depth, and spans for sends too (Producer and Client), so a tell that's
     lost still shows;
   - a grid chunk as a trace of its own, from the coordinator lending it to the worker's result, with links between them;
   - leaving health probes (`/healthz`, `/ready`) out of the traces, which Kubernetes calls every few seconds;
   - ~~`log.Info($"…")` as structured logs with named fields~~, done (see structured logging, below).
3. **Actors that keep their state** (event sourcing). Both apps lose their state when a pod restarts: stock's counts,
   grid's jobs. Actors' handlers already take commands, so an actor could declare its events, save each as it's handled,
   and replay them when it starts:

   ```piper
   actor Product(sku: string) persisted {
       event Received(quantity: int) { onHand += quantity; }
       on Receive(quantity: int) => Level { emit Received(quantity); … }
   }
   ```

   The compiler can check that every event changes state, and that replaying needs nothing but the events. It would be
   stored through the data plugin. ~~The language part~~ is done, in both actor plugins
   (`persisted by sku`, `event`, `emit`; see [plugins](plugins.md#keeping-state-persisted-actors)), with an in-memory
   store and checks that state changes only through events, and so is ~~a durable store~~ (Piper.Data's
   `DatabaseEventStore`, through EF Core), and ~~stock using it~~ (each product persisted, its node's events in a SQLite
   file on the pod's own volume), ~~grid's jobs~~ (with `transient` state, for what isn't worth keeping) and
   ~~snapshots for long streams~~ (`snapshot every 100;`), and so is ~~clustering~~, on Piper's own actor runtime
   (Piper.Actors.Remote, which replaced Proto.Actor so the runtime owns its scheduling and network, as deterministic
   simulation in item 6 needs): `Cluster.JoinAsync`, a membership provider (`memory`, `static` seeds), pings between
   the nodes over their transport, owners by rendezvous hashing, actors started on their owner as messages arrive
   (`Product.InCluster(node, sku)`, `Product.HostInCluster(…)`) and moved as members change, with the store refusing a
   second writer while nodes disagree (see [plugins](plugins.md#clusters-actors-shared-out-among-nodes-moving-as-they-come-and-go)).
   apps/stock uses it, its products' events in PostgreSQL the nodes share, with no `hash % count` or fixed node list.
   Still to come:
   - ~~membership through leases in the shared database~~, done (the data plugin's `DatabaseClusterMembership`:
     `Cluster:Provider` `postgres`, `sqlite` or `sqlserver`), which apps/stock uses, so its pods are a Deployment with
     no list of nodes; still to come, Kubernetes' endpoints, for nodes with no shared database;
   - tells to an actor that's moving are lost (at most once); queuing them while it starts on its new owner;
   - placement beyond hashing: keeping an actor near what it uses, or on a node with room;
   - **singletons:** one instance of an actor in the whole cluster (a scheduler, a coordinator such as apps/grid's),
     started on its owner and moved as members change, as keyed actors already are, with the store's single writer
     keeping two nodes from running it while they disagree. Most needs for leader election are this, so no consensus
     algorithm (Raft, Paxos) is planned. A distributed lock (`await cluster.Lock("nightly-report", lease: 30s)`, held
     with `using`) could come from the same leases membership already keeps in the shared database;
   - **projections, the read side** (CQRS). An actor's events answer questions about one actor; "every product low
     on stock" needs them across all of them. A `projection` would consume a persisted actor type's events from the
     `IEventStore` and keep a view, a table through the data plugin, remembering how far it has read, so it resumes
     after a restart and can be rebuilt from the start when its code changes:

     ```piper
     projection LowStock from Product into StockDb {
         on Received(quantity) { … }        // `this` is the actor's key; update the view's rows
         on Sold(quantity) { … }
     }
     ```

     The compiler checks it handles each of the actor's events (or says which it ignores), and the events' recorded
     shapes cover it during a rollout. A view lags its events, so reading it is eventually consistent; one that must
     be current asks the actor instead. In simulated tests, projections run on the simulated clock like everything else.
4. **Failure policies.** The same patterns were written by hand several times: stock retries once after a dead letter,
   grid lends chunks with a lease and ignores late results, the tracker retries its webhook (with `[Retry]`). The
   aspects plugin could add `[Timeout]` and `[CircuitBreaker]` beside `[Retry]`, and asks to remote actors take the
   same policies. And supervision, as Erlang's: an actor that throws is restarted by a policy its parent declares
   (restart it, restart its siblings too, or give up after so many failures in a minute), rather than each handler
   catching what it can; deterministic simulation (item 6) tests the policies.
   ~~`[Timeout]` and `[CircuitBreaker]`~~ are done (the aspects plugin, on Piper.Runtime's PiperPolicies), and so are
   ~~failure policies for actors~~: `on failure resume|rollback|restart [n per minute]|stop;` (see
   [plugins](plugins.md#actor-the-actor-model)), with `rollback` and `restart` checked to keep only immutable state.
   Still to come:
   - supervision trees (a `supervisor` owning child actors, restarting one or all of them), once something needs a
     hierarchy; Piper's actors have no parent yet;
   - `restart` recovering the actor's last state rather than starting empty, once actors keep their state (item 3);
   - asks to remote actors taking `[Retry]`, `[Timeout]` and `[CircuitBreaker]` as functions do;
   - **`[Bulkhead(max, queue)]`** beside them: at most `max` calls running at once, up to `queue` more waiting, and
     the rest refused at once, so one slow dependency can't take every thread or connection a service has. On
     PiperPolicies as the others are, shared by every caller of the function (or named, `[Bulkhead("payments", 10)]`,
     to share one between functions), and for remote asks too. A refusal is a transient error (see
     [transient errors](#language)), so a `[Retry]` outside it may try again later.
5. **An outbox, and queues.** The tracker tells its webhook after saving a change; if the process dies in between, the
   notification is lost. An outbox writes the message in the change's own transaction and sends it afterwards, so it
   can't be lost, which suits the data plugin. ~~The outbox~~ is done (the messaging plugin's, with the data plugin's
   `outbox;`), and apps/tracker's webhook uses it. A queue plugin would give jobs that must survive a restart somewhere to
   live, as the grid's README suggests for long ones: see [the messaging plugin](#a-messaging-plugin) below.
6. **Testing distributed programs.** Both apps wrote their own `StartCluster` and `StopCluster`, and the grid's
   "vanished worker" test was put together by hand. A test helper could give a cluster in one process, with faults to
   inject: stop a node, drop or delay messages to one, split the network. Property-based tests (above) suit it, with
   the faults among the generated inputs. ~~The helper~~ is done: Piper.Actors.Remote's `PiperCluster` (see
   [plugins](plugins.md#testing-a-cluster-in-one-process)), which stock's and grid's tests use. Still to come: faults as
   generated inputs, and the rest of deterministic simulation (below).

   **Deterministic simulation** goes further, as FoundationDB and TigerBeetle test themselves, but built in: the whole
   cluster (actors, remote actors, messaging, timers) runs in one process on one thread, with a simulated clock and
   network, and every choice the run makes (which message is delivered next, which is dropped, delayed or duplicated,
   when a node restarts, when the network splits) comes from the property test's recorded choices. So:

   ```piper
   test "no stock is sold twice" for (orders: List<Order>) simulate (nodes: 3, faults: on) {
       …   // drive the cluster; check what's true however it ran
   }
   ```

   - A race is found by trying thousands of schedules in seconds, with simulated time racing ahead.
   - It shrinks like any failure, since the schedule is just more choices: to the fewest events that cause it ("node 2
     restarts between the reservation and its confirmation"), shown as a timeline.
   - `--seed` replays it exactly, under the debugger too, as nothing depends on real threads or time.
   - It's possible only because the compiler sees every place a program reads the clock, gets random numbers, sleeps,
     starts a task or does I/O, so in a simulated test each is routed to the simulator (and one it can't route, such
     as a .NET call doing its own I/O, is reported). Other languages need the program written against a framework from
     the start; Piper's actors, timers and messages would be simulated as written.

   ~~The runtime~~ is done: Piper.Runtime's `PiperSimulation` (one thread, a simulated clock, every choice from a seed;
   `Explore` over seeds, `Run` to repeat one, a timeline of what happened) and `PiperRuntime.Current`, the ambient
   runtime Piper's actors, clusters and failure policies take their time and background work from; and
   Piper.Actors.Remote's `SimulatedNetwork` (latency, loss, duplication, reordering, splits) and `SimulatedCluster`
   (crash, stop, restart, settle). See [plugins](plugins.md#simulating-a-cluster-every-run-repeatable-many-runs-in-seconds).
   It found, at once, that a node that hasn't yet noticed another crash sends it asks that time out rather than
   moving on. Still to come:
   - ~~the `simulate` clause on `test`~~, done, with `--seed` repeating a failure (see
     [simulated tests](language.md#simulated-tests)), and so is ~~the compiler routing a program's own clock reads,
     random numbers, delays and `Task.Run`~~ (Piper.Runtime's `PiperRouting`, in every build), and
     ~~reporting what can't be~~ (PIP0512: `Thread.Sleep`, blocking waits, real threads and timers, where a simulated
     test reaches them); still to come, a .NET library's own I/O and threads, which the compiler can't see into;
   - ~~shrinking a failing run~~, done: its choices are a list in which 0 is the calmest, shrunk as a property test's
     are (and together with its values, in a simulated property test), and so are ~~faults as the run's own choices~~
     (`SimulatedCluster.StartChaos` and `CalmAsync`), which apps/stock's simulated test uses;
   - shrinking to show the failure as the few events that matter, rather than the shrunk run's whole timeline;
   - the messaging plugin's transport and the data plugin's stores in simulation (an in-memory store already is).
7. **Deploying without hand-written files.** Each app had a Dockerfile copying the repository to build the compiler,
   and Kubernetes YAML with the same `POD_IP` plumbing. ~~`piper build --publish`~~ writes the program and its
   Dockerfile, and ~~a declaration of the app's roles~~ is done: the `deploy` plugin (see
   [plugins](plugins.md#deploy-kubernetes-manifests)), whose `deploy` writes the Kubernetes manifest on each build;
   apps/stock, apps/grid and apps/orders have theirs made so. Still to come: Docker Compose and Aspire's manifest,
   building the image without Docker (as the .NET SDK's container publishing does), and reading the server's health
   paths and `config` sections from the program rather than saying them again.
8. **Durable workflows, checked by the compiler** (a `workflow` plugin, as Temporal and Azure Durable Functions). A
   process that runs for minutes or weeks (an order paid, packed, shipped, refunded if it isn't), surviving restarts by
   replaying its history of steps. Those frameworks need the workflow's code to be deterministic, so a replay takes the
   same path, and leave that to discipline: a `DateTime.Now`, a `Random` or a direct HTTP call in one is a bug found in
   production. Piper can check it:

   ```piper
   workflow Fulfil(order: Order) {
       let payment = await activity Charge(order);            // run once, its result recorded
       await timer(TimeSpan.FromDays(2));                     // durable: survives restarts
       if !(await activity Shipped(order.id)) { await activity Refund(payment); }
   }
   ```

   - In a workflow's body, reading the clock, random numbers, I/O and anything else that differs between runs is a
     compile error; it goes in an `activity`, whose result is recorded and replayed.
   - A `statemachine` can be a workflow's state, and compensating steps (undo a charge if shipping fails) make sagas,
     with the messaging plugin's (item 7 there).
   - The compatibility check (item 1) covers workflows too: changing one while instances are mid-way is a breaking
     change unless the new code replays the old history.
   - Deterministic simulation (item 6) runs them, with crashes between their steps.

   ~~The runtime~~ is done: the `workflows` plugin's Piper.Workflows (`WorkflowEngine`, see
   [plugins](plugins.md#workflows-durable-workflows)), each instance's history a stream of the persisted actors'
   `IEventStore`, replayed on resuming: activities recorded once (retried, then failed into the workflow, which can
   compensate), durable timers, recorded values (`flow.Now()`), idempotent starts, one runner per instance, resuming
   unfinished instances, and a replay that takes another path failing the instance, saying where. Tested in
   simulations, restarted at random moments. Still to come, in order:
   - ~~`workflow`, `activity` and `timer`~~, done, with ~~the clock, random numbers, new ids, plain delays, threads,
     blocking and I/O in a workflow's code a compile error~~ (PIP0513, through what it calls; any function marked
     `[PiperDeterministic]`); still to come, mutable state shared outside the workflow (a static field) and the order of
     iterating a `HashSet` or `Dictionary`;
   - ~~running them from a server's or host's clause~~, done (`workflows Fulfil(shop) from store;`), and so is
     ~~waiting on signals~~ (`flow.Signal<T>(name, within)`, `Fulfil.Signal(engine, id, name, value)`), which makes
     sagas, a consumer passing a message on as a signal;
   - ~~a real app on them~~, done: [apps/orders](../apps/orders/README.md), an order's fulfilment waiting for its payment,
     and so is ~~the compatibility check for a workflow's code~~ (its input, result and steps in order recorded in
     `Name.shape.json`; a step inserted or moved before others is an error), and so are ~~versions~~ (`workflow Fulfil
     version 2`, the old kept as `workflow FulfilV1 as Fulfil version 1`; each instance replays on the version it
     started on). Still to come: seeing which versions still have instances running, to know when an old one can go.

More, around the services themselves:

- **What crosses a process boundary, checked.** A message to a remote actor, a queue or a workflow must be data that
  survives the trip: immutable, serializable, and holding no handles (a stream, a connection, a lambda). The actor
  plugins already warn about a message's mutable fields; a check like Swift's `Sendable` would make each of these an
  error where the message type is declared, rather than a failure when it's first sent.

- ~~**Typed clients generated from `endpoints`**~~, done as the `api` plugin (see [plugins](plugins.md#api-an-apis-shape-and-a-typed-client)):
  `api TrackerApi { fn Issue(id: IssueId) => IssueView? = get "/issues/{id:int}"; … }` is the API's shape and its
  typed client, and `endpoints Api: TrackerApi` is checked against it, so a change to either that the other doesn't
  follow is a compile error. Shared through piper.json's new `"projects"` (source compiled into each program),
  [apps/tracker-core](../apps/tracker-core) holds it with the tracker's domain; apps/tracker-desktop's hand-written
  `TrackerClient` and its copies of the records are gone. Still to come:
  - calls answering nothing (`unit`), or `Result<T?, E>`;
  - generating the `api` from existing `endpoints` (or the reverse), for an API that has no clients in Piper yet;
  - per-call headers and cancellation (a `CancellationToken` parameter passed to the request);
  - `"projects"` in a Piper.Build project (MSBuild), which compiles only its own folder's files today;
  - between services: a client for another team's API from its OpenAPI document, checked the same way.
- **Ids made anywhere** (`newtype OrderId = Guid`, as UUIDv7). The tracker's ids are numbered by the database, so an
  issue has none until it's saved; with messaging and an outbox, an event needs one first. Time-ordered GUIDs
  (`Guid.CreateVersion7()`, in .NET) index well and are made on any node. With newtypes this is small: a standard way
  to declare such an id and make a new one, which the data plugin stores.
- ~~**Secrets that can't leak**~~, done (see [Secrets](language.md#secrets)): `Secret<T>` prints and serializes as
  `***`, the compiler refuses one in a string's hole (PIP0407), `.Reveal()` is the one way to its value, and a
  `config` field can be `secret`; apps/tracker's webhook URL is one. Where secrets come from is any configuration
  source, chosen by the app with a server's or host's `sources` clause (Azure Key Vault, AWS Secrets Manager,
  HashiCorp Vault, Kubernetes secrets as files: each its provider's `IConfigurationBuilder` extension). Still to come:
  refusing one in string concatenation (`"key " + secret` gives `***` today, safe but likely a mistake).
- ~~**Structured logging from interpolation**~~, done (see [Logging](language.md#logging)):
  `log.LogInformation($"Reserved {quantity} of {sku}")` is a template with named fields, checking the level first,
  and the telemetry plugin exports the logs with their traces. apps/tracker's webhook failures are logged so. Still
  to come: a high-performance form (`LoggerMessage.Define`, cached per call site, as C#'s generator makes), and event
  ids.
- **Serverless hosts, and Native AOT.** A `consumer` or an endpoint could also run as a serverless function (a queue
  or HTTP trigger), the code unchanged and only the host different: one host abstraction, with an adapter for each
  platform the app chooses (Azure Functions, AWS Lambda, Google Cloud Functions, Knative). That wants Native AOT and trimming, for fast starts
  when scaling from zero, and Piper's runtime uses reflection in places (enum JSON, the data plugin's converters,
  newtypes' delegates). Worth checking Piper programs against AOT early, before more reflection goes in, with
  source-generated JSON where it's needed.
- ~~**Idempotent endpoints**~~, done (see [plugins](plugins.md#idempotent-endpoints)): `post "/issues" idempotent …`
  keeps each request's `Idempotency-Key` and its response, in memory or a database (the data plugin's
  `idempotency;`), and answers a retry with the same response; apps/tracker's posts are. Still to come: a warning
  for `idempotent` endpoints mapped by hand without `PiperIdempotency.Use(app)` (a `server` always adds it).
- **Feature flags,** as every cloud service here: an `IFeatureFlags` trait (`flags.IsOn("new-checkout", user.id)`, and
  variants beyond on and off), a local provider reading configuration (`"Features": { "new-checkout": 10 }` for 10% of
  keys, hashed so a user's answer stays the same), and others (a flag service, OpenFeature's providers) registered
  and chosen by `Features:Provider`. A library, not language; what Piper adds is where flags meet its checks:
  - **In a workflow,** a flag's answer can change between a run and its replay, so reading one is a compile error
    there (PIP0513) unless through `flow`, which records it as `flow.Now()` is;
  - **In simulated tests,** each flag read is one of the run's choices, so both sides of a flag are explored and a
    failure shrinks to the flags that cause it;
  - flags read in the program are listed by `piper doc --ops` with the configuration, so stale ones can be found.

### A messaging plugin

Service buses and message queues, behind a transport trait the app chooses an implementation of (in-memory for tests;
adapters for RabbitMQ, Azure Service Bus, Amazon SQS and others), as declarations: what messages there are,
where they go and who handles them, so the compiler checks what C# libraries find only when the system runs. Programs
name what they mean (a command, an event, an ordering key); which broker carries it is configuration.

**Constraints.** No heavy dependencies, and only open source or Piper's own code. So no MassTransit or NServiceBus
(commercial licences), no Wolverine or Rebus (whose conventions and runtime code generation would show through), and no
Dapr (a sidecar per service). Instead, a thin runtime of Piper's own (`Piper.Messaging`) with an adapter per transport,
each over the broker's official, open-source client: Azure.Messaging.ServiceBus (MIT), RabbitMQ.Client (Apache 2.0 or MPL 2.0).
A program brings only its transport's package, as the data plugin's programs bring their database's provider.

**Meaning, not the lowest common denominator.** The brokers differ (Service Bus has sessions, scheduling, duplicate
detection and dead-lettering; RabbitMQ routes through exchanges; Kafka is a log, not a queue), so code says only what
they share, and each transport maps it:

| In code | Service Bus | RabbitMQ |
|---|---|---|
| **command**: to one receiver | a queue | a queue |
| **event**: to every subscriber | a topic, a subscription per consumer | a fanout exchange, a queue per consumer |
| **key**: in order, per key | a session id | a consistent-hash exchange |
| **delay** | a scheduled message | a delayed exchange, or a TTL and dead-letter queue |
| **reject** | the dead-letter queue | a dead-letter exchange |

Transport-specific settings (connection, prefetch, lock duration, duplicate detection) are configuration
(`"Messaging": { "Transport": "ServiceBus", … }`), never code. Kafka is left for later, as streams, a concept of its
own, rather than forced into queues.

**What it looks like:**

```piper
messages Orders {
    command PlaceOrder(orderId: OrderId, lines: ImmutableList<Line>) queue "orders";
    event OrderPlaced(orderId: OrderId, total: decimal) topic "order-events" key orderId;
}

consumer Billing(db: BillingDb) {
    on OrderPlaced(e) => Handled {
        if await AlreadyBilled(db, e.orderId) { return Handled.Done; }    // handlers are idempotent
        match await Charge(e) {
            Ok(_) => Handled.Done,
            Err(Declined(reason)) => Handled.Reject(reason),             // dead-lettered: retrying won't help
            Err(Unavailable) => Handled.RetryLater(TimeSpan.FromSeconds(30)),
        }
    }
}

await bus.Send(PlaceOrder(id, lines));
await bus.Publish(OrderPlaced(id, total));
```

**What Piper adds:**

1. **A handler's result is the message's fate.** `Handled` is `Done`, `RetryLater(after)` or `Reject(reason)`, matched
   exhaustively, rather than "throw to retry": complete, abandon or schedule again, and dead-letter. An unexpected
   exception still retries, by the configured policy, and dead-letters after the last attempt.
2. **Contracts and routing checked at compile time:** messages are data (records, enums, newtypes, immutable
   collections, as the actor plugin checks); a command has exactly one handler in the program; a handler for a message
   the program doesn't declare, or an ordered event without a `key`, is reported.
3. **Versions checked** ([item 1](#cloud-and-distributed) above): queues keep old messages for days, and other teams
   read your events, so breaking changes to a message's recorded shape are errors.
4. **An outbox, with the data plugin:** a `Publish` while saving to a database goes into the same transaction and is
   sent afterwards, so a change and its message are never separated.
5. **Topology from declarations:** the compiler knows every queue, topic and subscription, so they're created at start
   if missing, or written out as Bicep or Terraform, rather than drifting from the code.
6. **Testing:** an in-memory transport, with assertions on what was sent and published, and later the fault injection
   of item 6.
7. **Later, sagas:** a long-running process following several messages is a `statemachine` (the state-machines
   plugin) whose events are messages, correlated by a key and saved between them.

**Messages or actors:** actors are fast request and reply within one cluster; messages are durable and asynchronous,
between services. Both stay, and the docs say when to use which.

**In order:** ~~the transport trait and the runtime with the in-memory transport, `messages` and `consumer` with
`Handled`, the compile-time checks, and versions (recorded shapes)~~, done (see
[plugins](plugins.md#messaging-commands-and-events-between-services)), and so is ~~the outbox~~ (the data plugin's
`outbox;`, `bus.In(db.Outbox)` and `bus.Relay(…)`, relays sharing a table by claims; see
[plugins](plugins.md#the-outbox-a-change-and-its-messages-saved-together)), and so is ~~RabbitMQ~~ (the rabbitmq plugin,
chosen by `Messaging:Provider`; see [plugins](plugins.md#rabbitmq)); then the other brokers' adapters, each a provider
(Azure Service Bus, Amazon SQS, Google Pub/Sub), and topology (queues and topics made from the declarations at
startup, or written out as Bicep or Terraform); ~~consumers and relays started by a server's or host's clauses~~,
done (`bus from "Messaging";`, `consume`, `relay`); then sagas.

**To decide:** whether Piper services must interoperate with existing C# services on the same bus. Their envelope
(headers, type names, correlation) is a protocol; if they use MassTransit's or NServiceBus's, the transports would need
an option to read and write it.

## One versioning system: a design

**Status:** the database's shape, its checks and `piper db migrate` are done (parts 1 to 3, with the lock and the
`migrate` clause; see [plugins](plugins.md#migrations-a-databases-shape-and-the-sql-that-changes-it)), and so is
`piper check --since` (part 4), and part 5: `sql$` checked against the schema, and versioned events, and so is
migrating Piper's own tables in a program's shape (the outbox's and idempotency keys') when a later Piper changes them
([plugins](plugins.md#migrations-a-databases-shape-and-the-sql-that-changes-it)). Still to come: the same for the
tables stores make as they open (the event store's and cluster's), which would need a record of each one's version in
the database, as they aren't in a shape.

**The problem.** A release is safe to deploy only if everything it shares with what's already running still fits: old
pods during a rolling update, clients already installed, messages already queued, workflows half way through, and the
database's rows. Piper already checks four of those as it builds, through recorded shapes (`Name.shape.json`, see
[item 1](#cloud-and-distributed)): an `api`'s calls, a remote actor's messages, a `messages` block's commands and
events, and a workflow's steps. The fifth, the database, isn't checked at all:

- **Nothing records a database's schema.** Adding a field to a table's record changes the table, but nothing notices
  until the program runs against a database made before it. `EnsureCreated` makes missing databases, never missing
  tables or columns, so apps/tracker's checked-in `tracker.db` never got the `PiperIdempotency` table, and every
  `idempotent` post against it would fail.
- **There are no migrations.** EF Core's own (`dotnet ef migrations add`) generate C# classes with the Design package,
  which doesn't suit a Piper program, and their snapshot is C# too.
- **Nothing says whether a schema change is safe alongside the old release.** Dropping a column old pods still read,
  or adding a required one they don't write, breaks them mid-rollout, as a breaking API change does.

**The proposal: the database as a fifth shape, and migrations generated from it.** The same mechanism as the others:
recorded at build, checked against what's recorded, breaking changes errors unless accepted. Then a command that turns
the recorded change into SQL, reviewed and checked in.

#### 1. A database's shape

The data plugin's analysis already sees each table's record or class with its fields' types (it checks `key` and
`index` against them). From those it records `Shop.schema.json` beside the declaration, through the same
`PluginAnalysis` machinery as `CheckWireShape`:

```json
{
  "name": "TrackerDb",
  "tables": {
    "Issues": {
      "entity": "Issue",
      "key": ["id"],
      "columns": {
        "id": { "type": "IssueId", "stored": "int", "optional": false },
        "assignee": { "type": "string?", "stored": "string", "optional": true },
        "status": { "type": "WorkflowState", "stored": "string", "optional": false }
      },
      "indexes": [["projectId"], ["projectId", "status"]]
    }
  },
  "piper": ["outbox", "idempotency"]
}
```

- **Logical, not SQL:** columns as Piper types and what they're stored as (a newtype as its value, an Option as
  nullable, a fieldless enum as its variant's name, as Piper.Data's converters store them). The same for every
  provider, so one shape serves SQLite on a laptop and PostgreSQL in a cloud.
- **Piper's own tables** (`outbox;`, `idempotency;`) are listed by name and version, as Piper.Data owns their shape.

#### 2. Checked as it builds, as the other shapes are

| Change | Alongside the old release |
|---|---|
| a table, an optional column, an index | compatible: recorded |
| a required column **with** a default | compatible |
| a required column without one | **breaking**: old pods insert rows without it |
| a column or table removed, renamed, or its stored type changed | **breaking**: old pods read and write it |
| a column made required | **breaking**, unless every row has a value (the migration must fill them) |
| a column made optional | compatible |

A breaking change is an error at the field, saying why and what to do, the usual way to change a schema without
downtime: **expand, then contract**. Add the new column optional and have the release write both; once every pod runs
it, a later release drops the old one. `--accept-breaking` records it anyway, as for the other shapes, when the
downtime is deliberate (a database only one process uses, as a desktop app's).

#### 3. `piper db migrate`

```
piper db migrate apps/tracker            # write the next script, if the shape changed since the last
piper db migrate apps/tracker --apply    # apply the scripts not yet applied, to the configured database
piper db status apps/tracker             # which are applied, which are waiting
```

- **Scripts, not code:** the change between the last migration's shape and the current one becomes a numbered SQL
  script, `TrackerDb.migrations/0003-issues-priority.sql`, beside the declaration, with the shape it migrates to. Plain
  SQL to read in review and check in, as a schema change deserves.
- **Generated through EF Core's provider:** the change becomes EF Core's migration operations (`CreateTable`,
  `AddColumn`, `CreateIndex`, …), and the provider's `IMigrationsSqlGenerator` writes the SQL, in the Relational
  package Piper.Data already uses. No Design package, no generated C#, and each provider's SQL is its own (SQLite's
  table rebuilds for what it can't alter, PostgreSQL's `ALTER COLUMN`).
- **Applied once each:** a `PiperMigrations` table records what's applied (number, name, the script's hash). Applying
  takes a lock where the database has one (PostgreSQL's and SQL Server's application locks; SQLite's write lock), so two
  pods starting together don't both migrate. A script whose hash changed after it was applied is an error.
- **An existing database** (made by `EnsureCreated`, as every app's is today): the first `piper db migrate` records the
  current shape as migration 0001, the baseline, and applying it to a database whose tables are already there marks it
  applied without running it.
- **In a server or host** (optional): `database db: TrackerDb from "Database" migrate;` applies waiting scripts as the
  app starts, for development and single-instance apps, so `piper run` just works. A cluster migrates as a deploy step
  instead (a Kubernetes Job, which the `deploy` plugin could write).
- **Tests** keep their own databases made from the current shape (`EnsureCreated`), and a check that applying every
  script in order to an empty database gives the same schema as `EnsureCreated`, so scripts and model can't drift.

#### 4. One answer for a release

~~Done~~ (see [plugins](plugins.md#checking-a-release-piper-check)): `piper check <folder> [--since <ref>]` lists each
shape with what was added, removed and changed, the plugin's reasons for any break (with where), shapes the program no
longer has, and a database's migrations added since and whether one reaches its shape; it fails on a break. It doesn't
yet say whether a migration is applied (that needs a connection: `piper db status`), nor default to the last release's
tag. The design as written:

With the database a shape like the others, one command answers the question a deploy asks:

```
piper check apps/tracker --since piper-v1.4.0
  api TrackerApi       2 calls added                          compatible
  messages Notifications  no change
  database TrackerDb   column Issues.priority added (optional) compatible, migration 0004 waiting
  workflow Fulfil      step inserted before 'ship'            BREAKING: instances running v3 can't replay
```

It compares the recorded shapes against those at a git tag (or the last release), so CI can fail a release that would
break what's running, and the output doubles as the release's notes on what changed for callers.

#### 5. Built on it

- ~~**`sql$` checked against the schema.**~~ Done (see [plugins](plugins.md#data-ef-core-databases)) for queries given
  straight to `Query`, `Execute` and `ExecuteAsync`; still to come: a query built in a function and run elsewhere.
  With each database's shape recorded, every `sql$"…"` query can be checked as it compiles, without a database to
  connect to: its tables and columns exist, each hole's type suits its column, and
  the columns it selects fit the record it's read into (`shop.Query<OrderRow>(sql$"select …")`). A column renamed in
  the model then shows at every query using the old name. As Rust's sqlx checks queries, but from the recorded shape
  rather than a live database. The SQL is parsed for its table and column references only (a light parser of
  Piper's own, for the dialects the providers speak), and a query it can't follow is left unchecked, not refused.
- ~~**Persisted actors' events, versioned.**~~ Done (see [plugins](plugins.md#keeping-state-persisted-actors)), in
  both actor plugins, with each version recorded in `Name.events.json`. An upgrade declares the old version's field
  types (`upgrade Placed version 1 (id: OrderId, total: Money) => …`), as they're how it's read, and snapshots needed
  nothing new: one of other types is already discarded and rebuilt from the events. The design as written:
  A persisted actor's events are kept for ever, so changing an event record
  silently breaks replaying the streams written before. Their shapes are recorded as the others' are, and an old
  version is upgraded explicitly:

  ```piper
  event Placed version 2 (id: OrderId, total: Money, currency: string);
  upgrade Placed version 1 (id, total) => Placed(id, total, "GBP");
  ```

  Each stored event keeps its version; replaying one of an older version runs its upgrades in turn. The compiler
  checks that every version ever recorded still has a path to the current one, and that a changed event without a
  new version is an error, so the hardest part of event sourcing is checked rather than left to discipline. Snapshots
  (`snapshot every 100;`) are versioned the same way, or discarded and rebuilt from the events.

#### Decided

1. **Where migrations run:** `piper db migrate --apply`, as a deploy step, with the server clause's `migrate` (and a
   database's `Migrate()`) for development and single-instance apps.
2. **Scripts for several providers:** one script per provider the program brings a package for
   (`TrackerDb.migrations/sqlite/…`, `…/postgres/…`). Each migration also keeps the shape it reaches
   (`0002-issues-priority.json`), so a provider added later gets scripts for every migration before.
3. **Breaking schema changes:** errors, with `--accept-breaking`, as for the other shapes.

**In order:** ~~the database's shape and its checks; `piper db migrate` with SQLite and the baseline (apps/tracker's
`tracker.db` is the first user); PostgreSQL and SQL Server, and the lock; `migrate` in the server clause~~, done, and tested against PostgreSQL and SQL Server in containers (the tests skip
without Docker); then ~~`piper check --since`~~, done.

## UI

Piper programs can be consoles and servers; a UI should come from .NET's UI frameworks, built in code rather than XAML
(nested object and collection initializers make a control tree read naturally).

- **Desktop through interop (first):** code-first samples, to find what's missing. Avalonia (cross-platform, from
  NuGet) is done: [apps/tracker-desktop](../apps/tracker-desktop) ([what it found](../apps/tracker/NOTES.md#phase-5-a-desktop-client-avalonia)).
  WPF (`"frameworks": ["Microsoft.WindowsDesktop.App"]`) is next. Two gaps remain:
  - `[STAThread]` on `Main` doesn't reach the generated entry point (`PiperEntryPoint.Main`), which WPF and WinForms
    need (Avalonia runs without it). `piper run` calls `Main` on the CLI's thread, so it would need to start an STA
    thread too.
  - No piper.json setting for a windowed program (MSBuild's `WinExe`), so a console window opens alongside it. The
    launcher piper writes from the SDK's apphost would need its PE subsystem set to GUI, as the SDK does.
  XAML is out of scope: it relies on generated `InitializeComponent` partials the binder can't see.
- **An MVU plugin:** Model-View-Update (as Elm and F#'s Fabulous) suits Piper: a record for the model, updated by
  spreading; an enum of messages, handled by an exhaustive `match`; and a view function returning a control tree. The
  plugin would supply the runtime (diffing the view, dispatching messages) over Avalonia. For the classic MVVM style
  instead, the `aspects` plugin's `[Notify]` gives a class `INotifyPropertyChanged`, as C# gets it from a source
  generator.
- **Web UI:** see [Web UI](#web-ui) below.
- **Terminal UI:** Spectre.Console or Terminal.Gui as packages should already work; a sample would show it.

### Web UI

**The problem, in Piper's own apps.** The stock and grid dashboards are hundreds of lines of HTML and JavaScript in
raw strings, which nothing checks: the JavaScript was syntax-checked with `node` by hand, values escaped by a
hand-written `esc()`, updates polled for, and URLs (`/stock/${sku}/receive`) tied to the routes by nothing. The server
is typed and the UI isn't, so they drift. One way of writing views should serve both kinds of app: content sites
(server-rendered, cached, found by search engines) and interactive ones (live, several people seeing the same thing
change), and a page should be able to start as one and gain parts of the other.

1. **Typed HTML.** Done: markup literals ([Markup](language.md#markup)), with escaping by type, components, `for` and
   `if` in holes, and pages from web handlers; typed links (`SiteRoutes.Lot(id)`) and forms read into records
   (`Form<T>`, with anti-forgery tokens), in the [web plugin](plugins.md). Still to come:
   - ~~**Editor support**~~, done: the grammar reads markup as markup (tags, attributes, text, holes as code), a
     component is its function for the editor (colour, hover, go to definition, rename in both tags), and completion
     offers tags, attributes and an input's fields in markup. Closing tags as `>` is typed, and hover on HTML's
     elements and attributes (a line and a link to MDN or htmx), are done too.
   - ~~**Messages for `requires`**~~, done: `requires … else "message"`, shown beside the field it's about.
   - ~~**All of a form's errors at once**~~, done: each `requires` is checked wherever the fields it mentions were read.
   - ~~**Typed forms in markup**~~, done: `<form for={NewLot}>` checks its inputs' names against the record's fields.
     Field names given to `errors.For(…)` and `form.Text(…)` are checked too (`FormErrors<T>`). Inputs made by
     components or holes aren't seen, which is also why a field with no input isn't reported.
   - ~~**Query values in route functions**~~, done: a handler's simple or optional parameters outside the route.
   Markup literals in expressions (as JSX, or Scala's XML), compiled to an `Html` value:

   ```piper
   fn LotRow(lot: LotView) => Html =
       <tr>
           <td><a href={Routes.Lot(lot.id)}>{lot.title}</a></td>
           <td>{lot.current:C}</td>
           <td>{match lot.state { Open(ends) => <Countdown until={ends} />, Sold(price) => $"sold for {price:C}", _ => "" }}</td>
       </tr>;
   ```

   - **Escaping by type:** a `string` in markup is always escaped; only an `Html` value goes in as markup, and raw HTML
     takes an explicit `Html.Raw(…)`. Cross-site scripting can't be written by accident.
   - **Links checked against routes:** the compiler knows every endpoint, so it generates `Routes.Lot(id: LotId)`; a
     link to no route, or with the wrong type ([newtypes](language.md#newtypes) included), is a compile error.
   - **Forms bound to records:** `<form for={NewLot}>` is checked against the record's fields; a submitted form is
     parsed into it, and its `requires` failures come back as messages beside the fields. Every `<form>` gets an
     anti-forgery token, which C# leaves to be remembered.
   - **`match` and `if` in markup,** so every state of an enum is rendered; **components are functions** returning
     `Html`, with nothing new to learn.
   - It's a language feature, as the lexer must read markup in expressions. The alternative needing no lexer change, a
     builder over trailing closures (`tr { td { … } }`, as Kotlin's HTML DSL), reads worse to anyone who knows HTML.
2. ~~**htmx support**~~, done (see the [web plugin](plugins.md)): the app serves htmx, `<Htmx.Script />` loads it with
   the anti-forgery token as a header, a group's `layout` gives browsers the page and htmx the fragment, and
   `HtmxRequest` reads and sets htmx's headers. `hx-get={SiteRoutes.Search()}` is checked like any other link.
3. ~~**Watching**~~, done: `piper run --watch` recompiles on save, restarts, and reloads the open pages. Still to
   come with live pages: keeping a live page's model across the restart.
4. ~~**Live pages**~~, done as htmx and pushed fragments (see the [web plugin](plugins.md)): pages watch topics over
   server-sent events, and `Live.Publish(topic, event, fragment)` updates every page watching. It was chosen over the
   MVU design below as much smaller, reusing markup, layouts, routes and forms, and keeping no state per page, so it
   scales as any site does; state stays where it is (actors, the database). The MVU design stays here, for if typed
   events from the browser (`on:click={Msg.Bid(5)}`) and a model per page are wanted later:

   **MVU on the server** (not built): the [MVU plugin](#ui)'s model, run on the server, as Phoenix LiveView does:

   ```piper
   live LotPage at "/lots/{id:int}" {
       record Model(lot: LotView, bidding: decimal?, message: string?);
       enum Msg { Bid(amount: decimal), Outbid(by: decimal), Tick }

       async fn Init(id: LotId, lots: Lots) => Model = …;
       async fn Update(model: Model, msg: Msg) => Model = match msg { … };
       fn View(model: Model) => Html = <div>…</div>;
   }
   ```

   - The model stays on the server. A small script of Piper's own (a few KB) sends the browser's events over a
     WebSocket or server-sent events; the server runs `Update`, compares the new view with the old, and sends what
     changed. No JavaScript, and no API layer for the app's own UI: typed from the database to the button.
   - The server can push: an actor or a message updates every page watching it.
   - A live page renders as ordinary HTML first, then connects, so it's found by search engines and shows at once.
   - The same model, messages and update can drive Avalonia on the desktop. `Update` is testable without a browser.
   - Each page holds a connection and a little memory, and lives in a sticky session or, better, an actor, so it can
     run on any node. That suits apps people sign in to, dashboards and live events; content pages stay plain.

**Not, for now:** Piper in the browser (WebAssembly through Blazor, or compiled to JavaScript): large downloads, or a
large compiler. If ever needed, Blazor components can be written as plain C# (`BuildRenderTree`), without Razor, so
markup literals could compile to them. A front end in TypeScript stays a fine choice: [typed clients](#cloud-and-distributed)
generated from `endpoints` can write TypeScript types too.

**The proving app: an auction site**, built: [apps/auction](../apps/auction/README.md), with what it found. It differs
from the plan below in places: lots open when listed (no Scheduled state), run in one process (not over pods), and
its pages aren't cached (see below). The plan, as written, still lists what's left for it (pods, persistence,
messaging); its `Money` and property test are done. As the tracker proved the API side, people list items, others
browse and search, then bid live as the clock runs down. Each part of it exercises one of the above:

- **Listing pages and search:** server-rendered, with search as you type through htmx. **Not cached**, as decided
  when it came to it: a page's prices are live, and a page only hears of changes after it connects, so a cached copy
  would miss the bids made since it was cached, for good. Its full pages also carry each visitor's anti-forgery token
  (which ASP.NET Core's output cache won't store, as it comes with a cookie). htmx's scripts are cached, for a day.
  Caching suits a content site's pages, not live ones.
- **Listing an item:** a form bound to `NewLot`, whose `requires` check the starting price, the reserve and the end.
- **A lot's life:** a `statemachine`, Scheduled → Open → Closing (extended by a last-minute bid) → Sold or Unsold,
  which each view `match`es on.
- **Bidding:** each lot an actor, taking bids one at a time, so two people can't both win at a price: the stock app's
  guarantee, in a UI people watch. Lots spread over pods with the remote actors plugin.
- **The lot page, live:** the current price, a countdown, "you've been outbid" and the bids, pushed to everyone
  watching.
- **Types and tests:** newtypes (`LotId`, `BidderId`, and, done, a `Money` requiring more than 0), and
  [property tests](language.md#property-tests) that the highest bid wins and bids never fall (done: any bids, in any
  order, against the lot's actor).
- **Later:** telling the winner and the seller through the [messaging plugin](#a-messaging-plugin) and an outbox, and
  the bids kept by persisted actors, so a restart loses nothing.

**In order:** typed HTML with routes and forms (with which the auction's pages and forms work, server-rendered); htmx
support; `piper watch`; live pages, proved by the auction's bidding. Like the tracker, the app records each gap it
finds, and what was done.

## Tools

- ~~**`piper fmt`**~~, done: `piper fmt app/` lays out .piper files as Piper is written (indentation from brackets and
  continued lines, blank lines tidied, nothing moved within a line), and so does Format Document in the editor;
  `--check` fails CI on files it would change. Still to come: spacing within a line (around operators and commas),
  wrapping long lines, and putting a `{` on the line it belongs to.
- **Language server:**
  - `piper.json` errors while typing, not just on save;
  - signature help inside lambdas.
- **`piper doc`: documentation from what the compiler knows.** Today `///` comments are shown in the editor and written
  into the generated C# as XML documentation (`piper build --exe` and `--library` write the `.xml`), which .NET tools
  such as DocFX can read, but as the C# Piper becomes: Options, newtypes, enums and contracts appear as their C#
  encodings. `piper doc app/ [-o docs/api]` would document the program as Piper, and what only Piper knows about it:
  - **The API, as written:** namespaces, records (fields and defaults), enums (variants and their fields), traits and
    what implements them, classes, functions and `extend` blocks, with their `///` comments. Types as Piper spells
    them (`string?`, `Result<Order, OrderError>`, `IssueId` with what it's stored as), linked to their pages.
  - **Contracts:** each record's, newtype's and function's `requires` (and their `else` messages), so a type's rules
    are part of its documentation: `Money` must be more than 0, a title can't be blank.
  - **A service's surface:** an `endpoints` block's routes, with their inputs, answers, the error statuses its
    `[Status]`es give and its policies (`authorize`, `idempotent`, rate limits); a `messages` block's commands and
    events, where they go and who handles them; remote actors' messages; workflows' inputs and steps. From the same
    information as the recorded shapes, so it's what's really on the wire.
  - **Configuration:** every `config`'s keys, with their types, defaults, `requires`, which are secret, and their
    names in appsettings.json and the environment (`Tracker:Connection`, `Tracker__Connection`), and what a
    server's `provider`, `database` and `bus` clauses read (`Events:Provider`, `Database:Connection`). What an operator
    needs to run the program, generated rather than kept by hand.
  - **Output:** Markdown first (a page per namespace, and an index; GitHub and most viewers render it), with no new
    dependencies; HTML later, through a small renderer of Piper's own rather than a static site generator.
  - **With doc tests** (above, [ideas from other languages](#ideas-from-other-languages)): the examples in `///`
    comments run by `piper test`, and shown in the docs with their output, so documentation can't drift from the code.
  - **To decide:** what's included (public declarations only, as for a library, or everything, for a service's own
    team), and whether the service and configuration pages are their own command (`piper doc --ops`), as they're for
    a different reader.
- **MCP (Model Context Protocol), both ways.**
  - **`piper mcp`: the compiler as an MCP server,** for AI agents writing and changing Piper programs. An agent reading
    files and running the build sees text and error messages; the compiler knows the program. Over MCP (JSON-RPC over
    stdio, as the language server's LSP is, so written with the same plumbing rather than a new dependency) it would
    offer tools:
    - **understand:** a program's diagnostics (with their quick fixes), the type and documentation at a position,
      definitions and references, what calls a function and what it calls, and what it reaches (the clock, the
      database, the network, as the simulation and workflow checks already work out);
    - **check:** the recorded shapes and whether a change breaks them (API, messages, workflows and, with
      [versioning](#one-versioning-system-a-design), the database), `piper fmt`'s layout, and the generated C#;
    - **run:** tests, by name, with a property test's or simulation's seed to repeat a failure, and its shrunk
      timeline;
    - **know:** Piper's own reference (language.md and plugins.md, by topic), and a program's configuration keys and
      service surface (as [`piper doc`](#tools) would describe them),

    and resources for the recorded shapes and schemas. Running tests runs the program's code, so, as the language
    server does, only in a workspace the client trusts. Answers are structured (types as Piper spells them, spans,
    codes), not prose to parse. A language built so that an agent can ask it rather than guess is unusual, and suits
    how programs are increasingly written.
  - **An `mcp` clause in a server: a Piper service's endpoints as MCP tools,** for AI agents to call. The web plugin
    already knows each endpoint's inputs, answers and errors (the same as OpenAPI's and the recorded shapes'), so
    `mcp "/mcp" { tools Api; }` would serve the endpoints named as tools, with JSON schemas from their records (Options
    as optional, newtypes as their values, enums as their variants, `requires` as constraints where JSON Schema can say
    them), the endpoints' policies applying to each call (`authorize`, rate limits, `idempotent`). Descriptions come from
    the endpoints' `///` comments. As OpenAPI made an API describable to code, this makes it callable by agents with no
    hand-written tool definitions to drift from it.
  - **To decide:** whether `piper mcp` edits (applying a quick fix, renaming) or only reads and runs, leaving changes
    to the agent's own file tools; and whether the server-side `mcp` is in the web plugin or a plugin of its own.
