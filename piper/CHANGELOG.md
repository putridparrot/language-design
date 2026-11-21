# Changelog

## Unreleased

- **`piper check <folder> [--since <ref>]`: one answer for a release.** Compares every shape the program records
  (apis, remote actors' and `messages` blocks' messages, workflows' steps, databases) with those checked in at a git
  tag, branch or commit, or with those on disk, without writing any. Each is listed as new, unchanged, compatible or
  BREAKING, with the plugin's reasons for a break and where, then what was added, removed and changed
  (`Issues.priority`, `GET /api/count`). A shape the program no longer has is breaking; a database's migrations added
  since are listed, with a note when none reaches its shape yet. It fails when anything breaks, for CI. Plugins get
  this for free: shapes they check with `CheckWireShape` or `CheckRecordedShape` are compared in the new
  `WireShapeMode.Compare` (and `PluginAnalysis.RecordsShapes` says whether to write what they write beside a program).

- **`sql$` queries checked against the database, as they compile:** each `sql$"…"` given to the data plugin's
  `Query`, `Execute` or `ExecuteAsync` is checked against the recorded shape of the database it runs on, with no
  database to connect to. Its tables and columns must exist (the error points at the name in the string), each hole
  must suit the column it's compared with, inserted into or set to, `db.Query<T>`'s select must give what `T` reads
  (one column named `Value`, or a column per field), and a table's `Query` must give whole rows. An optional hole
  compared with `=` is a warning, since `= NULL` matches nothing. Aliases, joins, `group by`/`having`, functions and
  the names a select gives are understood. Subqueries, `union`, `with`, schema-qualified names, the databases' own
  tables, SQL in a hole and queries built elsewhere are left unchecked rather than refused.

- **Fixed: stale reads after a cluster's network splits.** While a node was cut off, another could run its persisted
  actors and save new events. Once the network healed, the node's own copy, still running from before, answered reads
  from what it had (apps/stock's simulation found this: reservations granted during a split didn't show). Now a node
  that another took to be down restarts its persisted actors once they see each other again, and they replay what was
  saved. Pings say how many times their sender has taken the receiver to be down (new optional fields, so nodes on an
  older Piper still understand them), which catches a network lost one way only. `Cluster.Host` takes `persisted`,
  which the remote actors plugin passes. apps/stock has a test of the case, which failed on every run before the fix.

- **Migrations on PostgreSQL and SQL Server, tested:** the tests start each in a container (skipping without Docker,
  or using `PIPER_POSTGRES` and `PIPER_SQLSERVER`) and check that applying every migration in turn makes the schema
  `EnsureCreated` does, that the baseline completes an existing database, and that eight programs migrating at once
  apply each migration once, under the lock. Applying now creates a database that isn't there yet.

- **Fixed: a package's assembly for this platform** is the one referenced. Packages such as Microsoft.Data.SqlClient
  (which SQL Server's EF Core provider uses) ship a placeholder in `lib/` that only throws
  PlatformNotSupportedException, and the real assembly under `runtimes/win/lib` or `runtimes/unix/lib`. Piper took
  the placeholder, so the `sqlserver` provider couldn't connect. It now takes the most specific platform's assembly
  (`win-x64`, then `win`; `linux-x64`, then `linux`, then `unix`), when running and when publishing for a container.

- **Database migrations:** building a program records each `database`'s shape in `Name.schema.json` beside it: its
  tables' columns as Piper types and as stored, keys, indexes and Piper's own tables. A change that would break the
  release still running is an error (PIP0503) unless built with `--accept-breaking`: a column removed or added required,
  a stored type changed, a key changed. **`piper db migrate <folder>`** writes the next migration when the shape
  changed: the shape it reaches (`Name.migrations/0002-orders-region.json`), and a reviewed SQL script for each
  provider the program has the EF Core package of (`sqlite/0002-orders-region.sql`), written by EF Core's model differ
  and the provider's SQL generator, with no Design package. `--apply --connection …` applies the waiting ones, and
  **`piper db status`** lists them. Programs carry their scripts: a database's class has `Migrate()` and
  `MigrationStatus()`, and a server's or host's `database … migrate;` applies them as it starts. Each migration is
  applied once, in a transaction with its row in `PiperMigrations` (a script changed since is an error), under a lock;
  a database `EnsureCreated` made takes the baseline's missing tables only. apps/tracker migrates, which gave its
  `tracker.db` the `PiperIdempotency` table it lacked. A plugin's parser can now read its file (`PluginParser.File`),
  and a plugin can record a shape of its own (`PluginAnalysis.CheckRecordedShape`).

- **Fixed:** the `sqlserver` database provider (for event stores, cluster membership and a server's `database`) looked
  for `UseSqlServer` on the wrong class, so it never loaded.

- **Duration literals:** `500ms`, `30s`, `1.5s`, `5min`, `2h`, `1day` (or `7days`) are `TimeSpan`s, exact to the tick
  (one finer, too long, or with an exponent is error PIP0011), with TimeSpan's own operators (`1h + 30min`,
  `2 * 45s`). They work in patterns as numbers do (`< 100ms`, `100ms..<1s`), checked for exhaustiveness, and in C#
  become `TimeSpan.FromSeconds(30)` and `{ Ticks: < 1000000L }`. C#'s suffixes are unchanged: `5m` is a decimal and
  `1d` a double, so minutes are `min` and days `day`. `[Timeout(2s)]`, `[Retry(3, 200ms)]` and
  `[CircuitBreaker(5, 30s)]` take them (or milliseconds, as before); a timeout's message says "2 s" from a second up.

- **Transient errors:** `[Transient]` on an enum variant, a whole enum or a record marks errors worth trying again.
  `[Retry]` on a function returning a `Result` (or `Task<Result<…>>`) tries again on a transient `Err` as on a throw,
  and gives any other `Err` back at once; `[CircuitBreaker]` counts only transient `Err`s as failures (any other is a
  success: what it called answered); a workflow's activity giving a `Result` is retried on one. A `[Retry]` whose
  `Result` can never hold a transient error is a warning. `PiperTransient.IsTransient` and `IsTransientFailure` (in
  Piper.Runtime) are there for policies of your own, and decorators can ask `DecoratorContext.ReturnsResult`.

- **`piper package add <id>`** adds a NuGet package to a folder's piper.json (made if there's none) at its newest
  stable version on your NuGet sources, found with `dotnet package search` (so private feeds work), or at
  `--version`'s, looking nothing up; `--prerelease` includes previews. A package already there has its version changed.
  **`piper package remove <id>`** takes one out. Only the package's entry is edited, so piper.json's comments and
  layout stay as they were.

- **The editor on Piper's own source:** with this repository open, the VS Code extension runs the piper built in it
  rather than the installed tool, from a copy (so builds aren't blocked by locked files), restarting on each new
  build. The false errors on what the installed tool didn't know yet (`idempotent`, `TaskScope`, a server's
  `database`) go. Package and install the extension again once to get it (editors/vscode/README.md).

- **A `config`'s record keeps its defaults:** code making one directly (a test's) can leave defaulted fields out or name
  them, `Settings(connection)` or `Settings(connection, webhook: …)`, so adding a field with a default no longer breaks
  every such call (it broke apps/tracker's tests when `provider` was added). Optional fields are None by default. Only
  literal defaults carry over, on the trailing fields, as a record's defaults must be constants after the fields
  without them.

- **A database in a server or host:** `database db: TrackerDb from "Database";` registers a `database` declaration's
  class as a scoped service (as `AddDbContext` does) on the provider the configuration chooses (`Database:Provider`:
  sqlite by default, sqlserver, postgres; `Database:Connection`), and names `db`, a function making one, for the
  clauses after (`relay DatabaseOutbox.Store(db);`). `database db: TrackerDb(settings.provider, settings.connection);`
  takes them from the program's own `config` record. An unknown provider or a missing connection stops the app as it
  starts. apps/tracker's server uses it in place of its hand-written `DbContextOptionsBuilder` registration and
  factories, and its settings gain `provider` (`Tracker:Provider`, sqlite by default).

- **Async event handlers:** `button.Click += async (_, e) => { await Save(); }` (C#'s `async void`), for Piper's events
  and .NET's, was an error, as an async lambda had to return a Task. An event's handler can now be async; anywhere else
  an async lambda returning nothing is still an error (PIP0297). Found by apps/tracker-desktop, which keeps running its
  `[Command]`s from its handler, as they report failures in its status line.

- **`TaskScope` checks:** the tasks a scope starts are checked for data races (PIP0387), each with the others or,
  started in a loop, with itself, as `Task.WhenAll`'s `Task.Run` lambdas and parallel loops are. And a scope kept past
  its `Run` (stored outside the body, held by a lambda kept for later, or returned) is a warning (PIP0410), as it can't
  start tasks once `Run` has finished. Passing it to a function, or reading `scope.Token`, is fine.

- **A task nothing waits for is a warning** (PIP0409): a call giving a `Task` or `ValueTask` whose value is thrown
  away (`Save(order);`, the last expression of a block whose value isn't used, or an `Action` lambda's body), as its
  exception would be lost. In any function, not only async ones as C#'s CS4014. `let _ = …;` drops one on purpose;
  `TaskScope.Start`'s task, marked `[ObservedTask]`, is its scope's to wait for. Nothing in the samples or apps
  needed changing.

- **Structured concurrency: `TaskScope`.** `await TaskScope.Run { scope => … scope.Start { stop => Work(stop) } … }`
  ties tasks to a block. It finishes only when the body and every task it started have finished. A body that throws
  cancels the tasks (so a failed test can't leave workers running), and a task that fails cancels the rest, its
  exception being the one thrown. `TaskScope.Run(stop) { … }` links it to an outer token. Simulated tests run it as
  they run `Task.Run`, and it needs no `using`. apps/grid (its workers, in main, tests and `RunWorkers`) and
  apps/stock (its load clients) use it in place of "cancel, then `Task.WhenAll`". See
  [docs/future.md](docs/future.md#structured-concurrency-a-design) for the compiler checks to come.
- **Fixed: `out let` on the right of `&&` or `||`** (`while running && queue.TryDequeue(out let n) { … n … }`) was an
  internal compiler error, as `n` was declared inside the `if` that the short circuit became, out of the body's sight.
  It's now declared before the condition, which stays a plain `&&`.

- **Fixed, in the editor: the outline failing** ("Request textDocument/documentSymbol failed: selectionRange must be
  contained in fullRange") for any file with a plugin's declarations whose names come from the plugin's template: a
  `config` record's loaders, the `UseSecurity` a `policies` block makes, an `args` record's fields, `[Notify]` and
  `[Command]` properties. Such a name is now placed at its declaration's start.

- **Idempotent endpoints:** `post "/issues" idempotent …`, or `idempotent` on a group for its posts, puts, patches
  and deletes. A request with an `Idempotency-Key` header runs once, and a retry is answered with the first one's
  response (marked `Idempotent-Replayed: true`): 409 while the first runs, 422 for the key on a different request.
  Failures (500 and over) aren't kept, so they run again, and keys are per signed-in user. Keys are kept by the
  registered `IIdempotencyStore`: in memory, or in a database the data plugin's `idempotency;` adds a table to
  (`DatabaseIdempotency.Store(…)`), shared by a service's pods. `idempotent` on a `get` is an error. A `server` adds
  the middleware after its policies'. apps/tracker's posts are idempotent, with keys in its database.

- **CI** (`.github/workflows/piper.yml`, at the repository's root): every push to main and pull request touching piper/
  runs the tests and the four apps' tests, on Linux and Windows (Linux with Docker, so the RabbitMQ tests run against a
  real broker). A tag such as `piper-v1.1.0` (or `piper-v1.1.0-beta.1`) also packs the tool, Piper.Build and
  Piper.Runtime as that version, assemblies included, and keeps them as the run's artifact; publishing them is left to
  a person. Everything passes on Linux as on Windows (checked in the .NET 10 SDK's container).

- **apps/grid's cluster tests are simulated:** the job shared by workers on several nodes, one cut off mid-chunk, and
  the coordinator restarting mid-job run in a `SimulatedCluster` (`simulate runs 5`) instead of on real nodes over
  HTTP/2, so the 30-second lease the cut-off test waits out takes no real time, and they no longer depend on how busy
  the machine is. Every app's tests are now deterministic.

- **Each local pack of the tool is a newer version**, so `dotnet tool update -g Piper --add-source artifacts` replaces
  the installed piper (it stayed 1.0.0, so an update saw nothing new and it had to be uninstalled and installed again).
  A `dotnet pack` numbers the package after the release it builds on with the minutes since 2026-01-01 as a fourth
  part (`1.0.0.463511`); `-p:PiperRelease=true` packs the release's own number. Assembly versions stay at the release,
  so plugins keep loading across local packs. `piper --version` and the language server (its server info, and the
  PIP0408 hint, which now says to close the editor before updating) report the package's version.

- **Quick fix: add the missing `using`.** Where a name doesn't resolve (PIP0200), a type doesn't exist (PIP0211) or a
  type has no such method (PIP0243), the editor offers `Add 'using System.Text;'` for each namespace that would make
  it resolve: a .NET type the program references, an extension method (`ToImmutableArray` for
  `System.Collections.Immutable`), or a public declaration in another of the program's own namespaces. The using goes
  in order among the file's usings, after its `namespace` if it has none, or below the comments heading the file. Only
  a sole candidate is the preferred fix, and a name more than six namespaces define offers none.

- **Workflow versions:** `workflow Fulfil version 2 { … }` beside the old code kept as `workflow FulfilV1 as Fulfil
  version 1 { … }` (`as` names the workflow it's a version of). Each instance records the version it started on
  (`StepsAsync` shows `started Fulfil version 2`); new ones start on the latest registered
  (`WorkflowEngine.LatestVersion`), and each replays on its own (`Register(type, version, body)`); one whose version
  isn't registered is left waiting, not failed, with a message saying which to keep. Each version has its own shape
  (`Fulfil.shape.json` for 1, `Fulfil.v2.shape.json` for 2), and the replay-breaking error suggests a new version.

- **Fixed, in the editor: one unknown plugin no longer breaks everything.** A piper.json naming a plugin the copy of
  piper running the language server doesn't have (a newer one, as the installed tool lagged this repository's) made
  the whole project fail to load, so no plugin or package was loaded and every keyword and type showed an error, with
  the reason only in piper.json. The editor now loads the project leniently (`LoadedProject.Load(…, lenient: true)`):
  the other plugins load, and each of the program's files has a warning (PIP0408) saying what piper.json couldn't
  load, and for an unknown plugin which piper the server is and to update it. Builds still fail on it.

- **`deploy` (Kubernetes manifests):** the new deploy plugin's `deploy Name [to "path"] { … }` says how a program runs
  on Kubernetes: `role`s (each a Deployment: `replicas`, `args` with `pod.ip`/`pod.name` from the downward API,
  `port … [public [n]] [service]`, `health … ready …` probes, `env X = "…" | secret "s" "k" | pod.ip`, `stopping`,
  `resources`, `recreate`, `volume`), `postgres "name" database "db"` (Secret, Service, StatefulSet with a volume), and
  `image`. `piper build`, `run` and `test` write the manifest (`Name.k8s.yaml`, or `to`), to check in; the editor never
  does. Checked: unknown clauses, values, two public roles, a volume on several replicas.
  - apps/stock's and apps/grid's hand-written k8s/*.yaml are now made from their `deploy.piper` (the same resources,
    labelled with their role too); apps/orders has one (a volume for its SQLite database), and its address is a
    setting (`Orders:Urls`), so its published image runs in a container: run with the manifest's settings, an order
    placed and paid through it shipped.

- **Fixed: a package redefining one of .NET's public types** made calls to .NET's ambiguous in the generated C#, an
  internal compiler error: `dict.GetValueOrDefault(key)` in a program using EF Core, whose
  Microsoft.Extensions.DependencyModel has its own public `System.Collections.Generic.CollectionExtensions`. Such a
  package is now referenced by an extern alias (as two packages declaring one type already were), .NET's own keeping
  the plain name.
- **apps/stock's cluster tests are simulated:** the six that ran three nodes in real time with fast heartbeats (and
  could miss their waits on a busy machine) are `simulate` tests on `SimulatedCluster`, each over several seeds; the
  load test, which runs real HTTP servers, keeps real nodes.

- **A workflow's changes checked:** its shape (input fields, result, and steps in the order written: `1: activity
  shop.Charge`, `2: timer`, `3: signal paid`, `recorded now`) is recorded in `Name.shape.json`, and a change instances
  already running couldn't replay (a step inserted, removed or moved before others; a new required input; another
  result) is an error saying so; a step added after the others, or an optional input, isn't. `--accept-breaking`
  records a deliberate one. The shape check (`PluginAnalysis.CheckWireShape(…, replayed: true)`,
  `WireShapes.Breaking(…, replayed)`) words such breaks for running instances rather than callers.
- **apps/orders:** an order service on workflows. `workflow Fulfil` reserves the stock, waits for the payment (a
  `PaymentReceived` message the `PaymentsIn` consumer passes on as a signal) for a configured window, then ships, or
  releases the stock when it's short or late; idempotent activities over SQLite (EF Core); `workflows engine: Fulfil(…)
  from Events(connection);`, `bus` and `consume` in its server. Simulated tests (forty runs restarting it at random
  moments, the stock always adding up) and HTTP tests; and by hand, a process killed while an order waited for its
  payment, restarted, then paid: it shipped, reserving once.
  - `workflows name: … from store;` names the engine for the clauses after (`consume PaymentsIn(engine);`).

- **Workflows in a server or host:** `workflows Fulfil(shop), Returns(shop) from store;` makes a `WorkflowEngine` over
  the store, registers each workflow with its dependencies, registers the engine for handlers to start them, resumes
  the unfinished as the app starts, and stops them as it stops (`WorkflowEngine.RunUntil`). Given twice, an error.
- **Signals (sagas):** `await flow.Signal<T>(name)` waits for a signal from outside, and `flow.Signal<T>(name, within)`
  waits up to a recorded deadline, giving an Option, so a workflow can compensate when it doesn't come.
  `engine.SignalAsync(type, id, name, value)`, or the generated `Fulfil.Signal(engine, id, name, value)`, sends one (a
  consumer passing a message on). Signals wait in the instance's inbox stream until taken, and which was taken is
  recorded, so a replay takes the same.
- **Fixed, in simulations:** .NET's `Task.Delay(t, TimeProvider)` finishes on the thread pool for anything but an
  `await`, so `Task.WhenAny` of one left the simulation and runs stopped repeating (found by exploring a workflow's
  signals: a seed that failed only after others had run). `PiperRuntime.Delay(t, token)` is a simulation's own timer
  in one; the program's routed `Task.Delay`, and the actors', clusters', messaging's and workflows' waits, use it.

- **`workflow`, `activity` and `timer`:** the workflows plugin's syntax. `workflow Fulfil(shop: Shop) { start (order:
  Order) => Receipt { … } }` declares a workflow (its dependencies, its input, saved as `FulfilStart`, and its result),
  becoming a class with `Register(engine)`, `Start(engine, id, …)` and `Outcome(engine, id)`; in its `start` block,
  `activity x = call;` (or `activity call;`) is a step run once, named after what it calls, and `timer t;` a durable
  wait, both errors elsewhere; `flow` is its context.
- **PIP0513:** a workflow's code must replay the same, so in its `start` block, and what it calls, reading the clock,
  `Task.Delay` and `Thread.Sleep`, random numbers, new GUIDs, background work, threads, timers, blocking, the
  environment and I/O (files, the network, EF Core) are errors, each saying what to use instead (`flow.Now()`,
  `timer`, `flow.Record`, `flow.NewGuid()`, an activity) and the way there. What's in an activity isn't checked. It
  applies to any function marked `[Piper.Runtime.PiperDeterministic]`, skipping arguments to `[PiperEffects]`
  parameters (new in Piper.Runtime); the simulated tests' check (PIP0512) shares its walk.
  - `WorkflowContext.Activity` takes a function that's async or not, with a result or without.

- **Durable workflows (the runtime):** the new `workflows` plugin brings Piper.Workflows' `WorkflowEngine`, for
  processes that take minutes or weeks and survive restarts by replaying their history, kept as a stream of an
  `IEventStore` (`Workflow/Type/id`). `Register<TIn, TOut>(type, async (flow, input) => …)`, `StartAsync` (idempotent
  per id), `ResumeAsync` (unfinished instances, from the `Workflows` index stream), `ResultAsync`, `StatusAsync` and
  `StepsAsync`. In a workflow: `flow.Activity(name, run, options)` runs once and its result replays (retried by
  `ActivityOptions`, then thrown as `ActivityFailedException`, recorded, to compensate), `flow.Delay(t)` is a durable
  timer, and `flow.Record`, `flow.Now()` and `flow.NewGuid()` record values. A replay asking for a different step than
  the history has fails the instance (`NonDeterministicWorkflowException`, naming the step); a second runner is refused
  by the store. Time comes from the runtime, so it runs in simulations. No syntax yet: `workflow` and `activity`
  declarations, checked for determinism as they compile, come next.

- **Messaging in a server or host:** three clauses, shared by `server` and `host`. `bus from "Messaging";` makes the
  transport the configuration chooses (the in-memory one if none) and a `Bus` over it, registering both
  (`IMessageTransport`, `Bus`); `consume Billing(settings);` runs a consumer, and `relay store;` an outbox's relay, each
  for the app's life and stopped with it (`Bus.RunUntil`, `Bus.RelayUntil`). `consume` and `relay` without a `bus`
  before them are errors. apps/tracker's server uses them, in place of its hand-written `Notifications` worker.
- **A cluster test made deterministic:** `Actors_in_a_cluster_run_once_and_move_as_nodes_come_and_go` runs in a
  simulation over twenty seeds (`SimulatedCluster`), rather than on real time with fast heartbeats, which a busy
  machine could make miss its waits.

- **RabbitMQ:** the new `rabbitmq` plugin carries the messaging plugin's bus over RabbitMQ (RabbitMQ.Client 7.1.2, open
  source), chosen by configuration (`Messaging:Provider=rabbitmq`, `Messaging:Connection=amqp://…`, optionally
  `Messaging:Prefetch`) or in code (`Bus(RabbitMqTransport(uri))`). A command is a durable queue; an event a fanout
  exchange with a queue per consumer (`topic.Consumer`), single-active-consumer and one at a time when it has a `key`;
  a delay or `RetryLater` a delay queue per step (about a quarter apart; no broker plugin) dead-lettering to where the
  message is going; a reject the consumer's `.dead` queue, with `piper-reason`. Persistent messages, publisher
  confirms, and acknowledgement after the handler: at least once. Queues and exchanges are declared as first used.
  - `Subscription.Ordered`, set by the bus for an event with a key, tells a transport to keep its order.
  - apps/tracker lists the plugin, so `Messaging__Provider=rabbitmq` sends its notifications through RabbitMQ.
  - `RabbitMqTests` run against `rabbitmq:4-alpine` in Docker (or `PIPER_RABBITMQ`), saying they didn't run without one.

- **The outbox:** a change and its messages saved together. The data plugin's `outbox;` in a `database` adds a
  `PiperOutbox` table and an `Outbox` property; the messaging plugin's `bus.In(db.Outbox).Send(…)` / `.Publish(…)` add
  each message to the database's unit of work, so `SaveChangesAsync()` saves the change and its messages, or none of
  them. `bus.Relay(DatabaseOutbox.Store(() => Db(options)), options)` sends what's saved, oldest first, in the
  background (`OutboxOptions`: `Batch`, `Lease`, `Poll`, `Background`), with `FlushAsync()` for tests and
  `DatabaseOutbox.WaitingAsync(db)` for the backlog. Relays in several pods share a table, each claiming a batch for a
  lease timed by the database's clock; delivery is at least once (a message keeps its id). Piper.Runtime's
  `IOutbox`, `IOutboxStore` and `MemoryOutbox` (for tests) are the contract between the two plugins.
  - Piper.Data's leases (cluster membership and the outbox) share one `DatabaseClock`.
  - apps/tracker's webhook uses it: a transition saves an `IssueChanged` with the change (`TrackerDb`'s `outbox;`),
    and the server's worker runs a relay and a `Webhook` consumer, which posts the issue as it is when sent. A webhook
    down for longer than `[Retry]`'s three quick tries is retried by the bus with backoff, then dead-lettered; the
    old `Notify`, which logged and gave up, is gone. `Transition` takes the bus. Messages go over the in-memory
    transport unless `Messaging:Provider` says otherwise.
- **Messaging in simulations:** the in-memory transport, the bus's message ids and delays, and the outbox's relay take
  their clock and scheduling from the runtime, so they run in a `simulate` test: a `RetryLater` of an hour passes at
  once, and a run repeats by its seed. Neither cancels with `CancelAsync` (which runs what waits on a real thread),
  and PIP0512 now reports a program's own `CancelAsync` in what a simulated test reaches.

- **Simulated failures are shrunk:** a run's choices (what runs next, each frame's latency and loss, the run's own
  faults) are a list (`PiperChoices`, as a property test's), in which 0 is always the calmest: the work ready longest
  runs next, nothing lost, the shortest latency, no fault. A failing run is shrunk while it still fails the same way
  (the same exception type and message, numbers aside), in up to 500 runs (`SimulationOptions.ShrinkAttempts`), and
  the shrunk run is what's reported, with "shrunk N times to K choices from the calmest run" and its timeline.
  `--seed` repeats the shrinking too. `SimulationFailedException` has `Shrinks` and `Departures`.
  - A simulated property test's run draws on the choices its values came from, so they're shrunk together (shown as
    `simulation = (the simulated run, shrunk with the values)` rather than a seed).
  - The scheduler takes the work ready longest for choice 0 (it took any, and swapped the list's last into its place),
    `Chance` is false for choice 0, and a simulation's `Random()` is the run's own. So seeds' runs differ from before.
- **Faults the run chooses:** `SimulatedCluster.StartChaos(options)` crashes and restarts nodes, cuts them off and
  heals the network at times the run chooses (`ChaosOptions`: pauses, and which kinds), until `CalmAsync()` stops it,
  heals, restarts every node that's down and settles. Each is a choice whose calmest is nothing, so shrinking keeps
  only the faults a failure needs. apps/stock's simulated test uses it rather than faults written into the test.
- **PIP0512:** a warning where a `simulate` test reaches, through the program's functions and methods, what its
  simulation can't run: `Thread.Sleep`, a `Stopwatch` (`StartNew`), `Environment.TickCount`, `TimeProvider.System`,
  real threads (`new Thread`, `ThreadPool`, `Task.Factory.StartNew`, `Parallel`), real timers, and blocking waits
  (`.Wait()`, `.Result`, `GetResult()`), naming the test, the functions leading there, and what to use instead.

- **Deterministic simulation:** `PiperSimulation` (Piper.Runtime) runs a test on one thread with a simulated clock,
  every choice (what runs next, each network frame's latency and loss) from one seed, so a seed's run is the same every
  time and simulated minutes pass in milliseconds. `PiperSimulation.Explore(runs, body)` tries seeds until one fails,
  naming it with the run's timeline; `PiperSimulation.Run(seed, body)` repeats it. Piper.Actors.Remote adds
  `SimulatedNetwork` (`NetworkFaults`: latency, loss, duplication, reordering; splits) and `SimulatedCluster`
  (`PiperCluster` in a simulation, with `CrashAsync`, `RestartAsync`, `SettleAsync`).
  - `PiperRuntime.Current` is where Piper's runtime takes its clock, randomness and background work from: the real
    ones, or a simulation's. The actor system, clusters, `[Timeout]`, `[CircuitBreaker]` and `restart n per minute`
    use it.
  - An actor's mailbox no longer reads with `ReadAllAsync` (which leaves for the thread pool), and the cluster's
    pings list nodes in order, so runs repeat.
  - `test "…" simulate { … }` (and `simulate runs 50`) runs a test over that many seeds (100 by default), each in a
    simulation; a failure names its seed, which `piper test --seed` repeats, with the end of its timeline. A property
    test can be simulated too, each case with a generated seed shown with its values. `PiperSimulation.Running` is the
    running simulation, usable without a `using`, as `PiperGen` is.
  - The compiler routes a program's `DateTime.Now`/`UtcNow`/`Today`, `DateTimeOffset.Now`/`UtcNow`, `Task.Delay`,
    `Task.Run`, `Random()`, `Random.Shared`, `Guid.NewGuid()` and `Stopwatch.GetTimestamp`/`GetElapsedTime` through
    Piper.Runtime's `PiperRouting`, in every build: the same as .NET's, except in a simulation. Generated C# shows
    `global::Piper.Runtime.PiperRouting.Delay(…)` where it showed `Task.Delay(…)`.
  - apps/stock has a simulated test: reservations from every node over a lossy network while a node crashes, the
    network splits and heals, and the node comes back; none is ever oversold.
- **Asks fail when their node is found down:** an ask waiting on a node the cluster finds down fails then, with a
  `NodeDownException` (a `TimeoutException`), rather than at its timeout. It isn't retried, as the node may have handled
  the message before it went. A stopped node's own asks no longer retry, nor does it start actors.

- **Cluster membership in a shared database:** the data plugin's `DatabaseClusterMembership` keeps each node's lease as
  a row of `PiperMembers`, so nodes find each other with no seed list. Chosen by configuration as the event store is
  (`Cluster:Provider` `sqlite`, `sqlserver` or `postgres`, with `Cluster:Connection`, and `Cluster:Lease` in seconds,
  thirty by default). A lease is renewed every third of its length and given up as the node leaves; a crashed node's
  runs out. Members are read at most every sixth of the lease (pings, not the table, find a node down quickly).
  Leases are timed by the database's clock (`DateTime.UtcNow` in the queries, which EF Core turns into the database's
  own), so nodes whose clocks disagree still agree on them; this node's clock is used if a provider can't translate it.
  - `IClusterMembership` moved to Piper.Runtime (namespace `Piper.Runtime`), beside `IEventStore`. A cluster now calls
    its `JoinAsync` every round as well as on joining, so a provider can renew a lease there.
  - A leaving node stops its heartbeats before giving up its membership, so a round under way can't renew it after.
  - apps/stock finds its nodes this way in Docker Compose and Kubernetes, where it's now a Deployment (each pod known
    by its IP) rather than a StatefulSet with a headless service and a list of seeds. `--nodes` still works.

- **Clusters of actors:** nodes join a cluster (`Cluster.JoinAsync(node, membership, name)`), which shares actors out
  among them: each (a kind and a key, `Product/tea`) runs on one node, its owner, by rendezvous hashing over the members
  up, and moves when a node stops, crashes or joins. `Product.InCluster(node, key)` is a handle with no fixed node, and
  `Product.HostInCluster(node.Cluster, (n, name, key) => Product.Spawn(…))` lets a node run them, each started on its
  owner as the first message for it arrives. Asks through such a handle retry dead letters on the owner while it moves.
  - Membership is a provider, `IClusterMembership` (`Cluster:Provider`): `memory` (local) and `static` (seed nodes). The
    nodes ping each other over their transport (`ClusterOptions`: every second, down after five), and share whom they
    know, so a node needs only one seed. Stopping a node leaves the cluster first, handing its actors over.
  - A persisted actor that its store refuses (`EventStreamConflictException`: two copies, while nodes disagree on the
    members) stops, so the next message starts it again from what was saved.
  - `PiperCluster`'s nodes are now in a cluster of their own (pinging every tenth of a second), with `KillAsync(node)`
    for a crash and `WaitForMembersAsync(node, count)`; faults now apply to every message between nodes, pings
    included, as they're applied in the actor system rather than the plugin's code.
  - apps/stock is a cluster: no `hash % count`, fixed node list or warehouse starting products; its products' events
    are in PostgreSQL the nodes share (in Docker Compose and Kubernetes), so a pod stopped, lost or added moves products
    with their counts. `--nodes` is now the seed nodes.
  - Fixed: `DatabaseEventStore` opened by several processes at once (nodes starting together on one database) raced to
    create its tables, and all but one crashed.

- **Remote actors on Piper's own runtime, with Proto.Actor optional:** the remote actors plugin no longer brings in
  Proto.Actor. Piper.Actors.Remote has its own `ActorSystem` (mailboxes, asks, stopping, dead letters), with nodes
  talking over an `IActorTransport` chosen by the program: `http` (HTTP/2 on ASP.NET Core's Kestrel, one long-lived
  stream to each node, so messages arrive in order; no packages) and `memory` (nodes in one process, messages still
  crossing as JSON), registered as providers (`Actors:Provider`). The new `proto-actor` plugin adds a `proto`
  transport on Proto.Remote's gRPC, for a program that lists it. Delivery is at most once on every transport.
  - Messages and answers cross as JSON of their declared types (the message enum, the handler's result type) rather
    than .NET type names, so nodes needn't run the same build, only compatible shapes, as `Name.shape.json` checks.
  - `Result` now reads and writes JSON (`{"ok": …}` or `{"err": …}`), so it can be an ask's answer.
  - `PiperNode.StartAsync(host, port)` starts a node over the network, `PiperNode.StartInMemoryAsync()` one in memory,
    and `node.StopAsync()` stops one (was `node.Remote().ShutdownAsync(true)`). `DeadLetterException` is
    Piper.Actors.Remote's. A handler that throws answers with its message (no `CounterFault` record).
  - `PiperCluster.StartAsync` runs its nodes in memory, and `StartOverNetworkAsync` over HTTP/2 on loopback.
    apps/stock and apps/grid, both samples and the tests are moved over; grid's cluster test runs over HTTP/2.

- **Editor help for persisted actors:** hovering `transient`, `snapshot` and `every` in an actor describes them, as
  `persisted` and `on failure` were. samples/actors gains a persisted actor (`Visits`) using all three, which the
  samples' test runs and the hover tests point at.

- **A cluster in one process, with faults, for tests:** Piper.Actors.Remote's `PiperCluster.StartAsync(3, setup)`
  starts nodes on free loopback ports talking over gRPC, and injects a network's faults: `Isolate(node)`,
  `Partition(a, b)`, `Drop(from, to)`, `Slow(node, by)`, `Heal()`, `StopAsync(node)` (a pod deleted) and
  `AddNodeAsync()`. Faults apply to the messages the remote actors plugin's code sends between nodes, by address; a
  lost ask times out, as a real one would. `PiperNode.StartAsync(host, port)` replaces the `StartNode` stock and grid
  each wrote. Stock's tests now use the helper, with a new one for a node cut off and healed (its dashboard view of
  other nodes now waits two seconds for each, not ten); grid's gRPC test cuts a worker off mid-chunk, and checks its
  late answer is ignored once it's back.

- **Snapshots for persisted actors:** `snapshot every 100;` keeps a persisted actor's state every 100 events, and an
  actor starting again restores the latest and replays only the events since. Each value is kept with a fingerprint
  of its type's shape, so a snapshot from before the state's types changed is ignored and every event replayed.
  `IEventStore` gains `LoadSnapshotAsync`, `SaveSnapshotAsync` and `LoadAsync(stream, from)`, as default methods, so
  existing stores still work, without snapshots; `MemoryEventStore` and `DatabaseEventStore` (a new `PiperSnapshots`
  table, made as the store opens, in a database from before too) keep them. apps/grid's `Jobs` takes one every 100.
  A snapshot the store fails to keep doesn't fail the message, but is reported: logged under `Piper.Events` (through
  Piper.Runtime's new `PiperDiagnostics.Log`, which servers and hosts point at their `ILogger`, and standard error
  otherwise), once until it recovers; added to the current span; and counted on the `Piper.Events` meter.

- **`transient` state in a persisted actor, and grid's jobs kept as events:** `transient mut asked = …;` is state
  a persisted actor's handlers change themselves, with no event: not kept, started afresh whenever the actor starts,
  but put back like the rest when a handler fails (an event's body can't change it). apps/grid's `Jobs` is now
  persisted: its events are its rules' inputs with their times, so a coordinator that restarts carries on with its
  job and history, and workers told to wait (every half second) are only noted in transient state. Its store is
  configuration (`Events__Provider=sqlite`), in memory if none.

- **The `messaging` plugin (commands and events between services):** `messages Orders { command PlaceOrder(…) queue
  "orders"; event OrderPlaced(…) topic "order-events" key orderId; }` declares the messages, and
  `consumer Shop(bus: Bus) { on PlaceOrder(order) { …; Handled.Done } }` handles them. A handler says what became of
  its message: `Handled.Done`, `Handled.RetryLater(after)` or `Handled.Reject(reason)` (dead-lettered); one that
  throws is retried with backoff, then dead-lettered. A command has one receiver and an event goes to every consumer
  of it. The transport is a provider (`IMessageTransport`), chosen by configuration; `memory` is the local one, with
  `Sent`, `DeadLetters` and `Idle()` for tests. Checked as it compiles: a message's fields must be immutable, a
  handler's message must be declared, a command is handled by one consumer, and a messages block's shape is recorded
  (`Orders.shape.json`) so a change that breaks senders or readers is an error.

- **Providers, chosen by configuration:** a cloud-type service is a trait or interface whose implementations a program
  chooses among by configuration at startup. Piper.Runtime's `PiperProviders` holds each service's providers by name,
  its local one (used when nothing is configured) built in, and others registered by the plugins a program brings
  (a plugin's new `Providers`, which the compiler has the program call as it starts). A server's or host's
  `provider store: IEventStore from "Events";` makes the one `Events:Provider` names, from the rest of the section,
  and registers it; `PiperHost.Provider<T>(configuration, section)` does the same anywhere. For `IEventStore`: `memory`
  (local), and with the data plugin `sqlite`, `sqlserver` and `postgres`. An unknown name or missing setting stops
  the program at startup, saying which. apps/stock now takes its store from configuration
  (`Events__Provider=sqlite`) rather than its own `--events` option.

- **`sources` in a server or host:** `sources (configuration: IConfigurationBuilder) => …;` adds a configuration source
  from any provider (a key vault, a cloud's secrets manager, mounted files), chosen by the app, before anything reads
  configuration, wherever it's written. Secrets are read through it; nothing in Piper is tied to one cloud. The
  roadmap now states the rule every cloud feature follows: an abstraction, a local implementation, and providers
  chosen by configuration at startup.

- **`piper fmt`:** lays out .piper files as Piper is written: four spaces for each open bracket (those one line opens
  counting once), one more for a continued line (a chain's `.Call()`, `requires …`, an expression after `=`), and
  blank lines tidied, with trailing whitespace gone. Nothing moves within a line, and a string's or markup literal's
  lines are left as they are, so it can't change what code means (a test checks every file in the repository keeps
  its tokens). `--check` lists the files it would change and fails, for CI. The language server formats a document
  the same way (Format Document). The samples and apps are formatted.

- **Secrets:** Piper.Runtime's `Secret<T>` prints, formats and is written to JSON as `***` (read from JSON, it's the
  value written), `.Reveal()` is the one way to its value, and the compiler refuses one in a string's hole (PIP0407),
  the usual way one leaks into a log or a message. A `config` field marked `secret` is read as one. apps/tracker's
  webhook URL is a secret. See [Secrets](docs/language.md#secrets).

- **apps/stock keeps its counts:** each product is a persisted actor (`Received` and `Reserved` events), and a node
  keeps its products' events where its configuration says (`Events__Provider=sqlite`, see Providers above), so a node
  that restarts carries on with their counts. Docker Compose and the Kubernetes StatefulSet give each node a volume
  of its own for its SQLite file.

- **Persisted actors' events in a database (data plugin):** Piper.Data's `DatabaseEventStore`
  (`await DatabaseEventStore.OpenAsync(DbContextOptionsBuilder().UseSqlite("Data Source=events.db").Options)`) keeps
  their events in a `PiperEvents` table through EF Core, made in the database if it isn't there, so they outlive the
  process. Its key (stream and place) refuses a second writer racing for one stream, even from another machine.

- **Persisted actors (event sourcing, both actor plugins):** `actor Shelf(sku: string) persisted by sku { … }` keeps
  its state as events: `event Received(quantity: int) { onHand += quantity; }` says how each changes the state, and
  handlers `emit` them. A message's events are saved together when its handler finishes (none if it throws, and the
  state is put back), and replayed when an actor with that identity starts again. `Spawn` takes the store:
  Piper.Runtime's `MemoryEventStore`, or any `IEventStore`; a stream written by two actors at once is caught
  (`EventStreamConflictException`). The compiler checks that state changes only through events, that events don't
  await or emit, and that the state is immutable values; a remote actor's events are in its recorded shape. See
  [plugins](docs/plugins.md#keeping-state-persisted-actors).

- **Failure policies for actors (both actor plugins):** `on failure …;` says what a handler that throws does to its
  actor: `resume` (the default, as before), `rollback` (the state is put back as it was before the message, so a
  handler finishes or changes nothing), `restart` (back to how `Spawn` made it; `restart 3 per minute` stops it past
  that), or `stop`. `rollback` and `restart` need immutable state, checked at compile time. In one process a stopped
  actor refuses messages, and fails asks already waiting; across processes Proto.Actor stops it. See
  [plugins](docs/plugins.md#actor-the-actor-model).

- **`[Timeout]` and `[CircuitBreaker]` (aspects plugin):** `[Timeout(ms)]` makes an async function's caller stop
  waiting after `ms`, with a `TimeoutException`. `[CircuitBreaker(failures, openMs)]` refuses calls at once, with a
  `CircuitOpenException`, for `openMs` after `failures` failures in a row, then lets one through to try. They nest
  with `[Retry]`, the first written outermost. See [plugins](docs/plugins.md#aspects-decorators-trace-timed-retry-timeout-circuitbreaker-and-notify).

- **A `host`'s services are checked (hosting plugin),** as a server's are: a worker or stopping hook taking a service
  of the program's own that nothing in the host registers, a class DI makes needing one, and a singleton taking a
  scoped service are errors at compile time (warnings where a registration could be out of sight). The checks are
  shared between the two plugins (plugins/Shared/ServiceChecks.cs).

- **`using` on an Option:** `using let span = source.StartActivity("x");` (an `Activity?`), `using (…)` and `await
  using` dispose the value if it's Some, as C#'s `using` skips a null; an Option of something not disposable is still
  an error. (Piper.Runtime's `Option<T>` implements `IDisposable` and `IAsyncDisposable` explicitly to do it.)

- **Structured logging from interpolation:** a `$"…"` given to `ILogger`'s `Log…` methods is a template with a named
  field for each hole (`log.LogInformation($"Reserved {quantity} of {sku}")` logs `"Reserved {quantity} of {sku}"`
  with `quantity` and `sku`), and as a statement checks the level first, so a level that's off evaluates no hole. See
  [Logging](docs/language.md#logging). apps/tracker logs a webhook that stays down as a warning this way, rather than
  writing to the console, and logs warnings rather than nothing.

- **Remote actors are traced:** a message sent within a trace carries it (`traceparent`, in Proto.Actor's message
  headers, so the messages don't change), and each handler runs in a span continuing it (`Product.Receive`; Server for
  an ask, Consumer for a tell; failed if it throws), so one request is one trace across nodes. Each handler's time is
  the `piper.actor.handler.duration` metric. It's .NET's own `Activity` and `Meter` (source `Piper.Actors`, in the new
  Piper.Actors.Remote that comes with the plugin), costing nothing unless something listens.

- **The `telemetry` plugin:** `telemetry;` (or `telemetry "name";`) in a `server` or `host` exports traces, metrics and
  logs over OpenTelemetry's protocol to `OTEL_EXPORTER_OTLP_ENDPOINT` (Aspire's dashboard, Jaeger, a collector), and
  adds nothing without one. It covers ASP.NET Core's requests (named by route), HttpClient's calls, Piper's actors,
  and .NET's runtime metrics. apps/stock and apps/grid use it. See [plugins](docs/plugins.md#telemetry-opentelemetry).

- **Fixed: a nested .NET type in the generated C#** (one Piper meets by inference, such as
  `Dictionary<string, int>.Enumerator`) was written without the types it's nested in, so the C# didn't compile.

- **`forwarded` in a server (web plugin):** behind a proxy that ends TLS (an ingress, a load balancer), `forwarded;`
  makes each request's scheme and client those the proxy saw (`X-Forwarded-Proto`, `X-Forwarded-For`), first in the
  pipeline, so `https` works behind an ingress and logs see the client. `forwarded from "10.0.0.0/8", "10.1.2.3";`
  believes only those proxies (a literal that isn't an address or network is an error). With it, `https` and only
  http:// `urls` isn't warned about. See [plugins](docs/plugins.md#a-server).

- **Breaking changes to a remote actor's messages are caught,** for rolling updates, where old and new nodes talk:
  each actor's handlers (what they take, and what they reply, or that they're tells) are recorded in
  `Name.shape.json` and checked as an api's are (below). apps/stock, apps/grid and the remote actor samples record
  theirs.

- **Breaking changes to an api are caught:** an `api`'s shape (its calls, and the records, enums and newtypes they
  send and answer, with which way each goes) is recorded in `Name.shape.json` beside it, to check in. `piper build`,
  `run` and `test` record it, and report a change that would break callers built against it as an error: a call
  removed or moved, a new value or field callers must send, a field they read removed, a type changed, an enum
  variant added to one they read or removed from one they send. Compatible changes are recorded; `--accept-breaking`
  records a deliberate break. The editor and `piper emit` only check. Plugins can do the same for what they send
  (`PluginAnalysis.CheckWireShape`). apps/tracker-core records `TrackerApi.shape.json`.

- **The `api` plugin (typed clients):** `api TrackerApi "/api" { fn Issue(id: IssueId) => IssueView? = get
  "/issues/{id:int}"; … }` declares an HTTP API's calls, as functions whose body is their route, and becomes
  `class TrackerApi(http: HttpClient)`, a client whose methods send the requests and read the answers as the web
  plugin writes them: each a `Result`, with a 404 as an Option's None and a handler's own error read back from its
  ProblemDetails (`ApiError<IssueError>`'s `Error`). `endpoints Api: TrackerApi` says a server's endpoints answer it,
  and the web plugin checks that every call is answered, by a handler taking what it sends and answering its type,
  and that nothing else is mapped. The client needs only Piper.Runtime (its new `PiperClient`, `ApiError` and
  `[ApiCall]`). See [plugins](docs/plugins.md#api-an-apis-shape-and-a-typed-client).

- **`"projects"` in piper.json:** names other program folders whose code a program shares, compiled in from source
  (Piper's own types don't survive a compiled assembly), with their plugins, packages and references, without their
  tests, and theirs in turn, each once. `piper run --watch` watches them too, and the editor analyzes a program with
  them. The tracker's domain and `api TrackerApi` moved to the new apps/tracker-core, which apps/tracker answers and
  apps/tracker-desktop calls; the desktop's hand-written client and its copies of the records are gone.

- **`[Status]` moved to Piper.Runtime,** keeping its namespace (`using Piper.Web;` still finds it), so an error enum
  can be shared with a client that doesn't reference ASP.NET Core. Piper.Web forwards the type.

- **The `hosting` plugin:** `host WorkerNode(options: Options) { config …; services …; worker …; stopping …; shutdown
  …; }` is a `server` without the web, for console programs and workers, over .NET's generic host: it becomes
  `fn WorkerNode(options) => IHost`, which `Main` runs (`await WorkerNode(options).RunAsync();`) until Ctrl+C or
  SIGTERM, stopping its workers and waiting for them. Its clauses are the server's that aren't about the web, and
  both now call Piper.Hosting (which comes with the web plugin too), so the server's `stopping`, `shutdown` and
  `logging off` are `PiperHost`'s rather than `PiperServer`'s. apps/grid's worker nodes are hosts, rather than wiring
  Ctrl+C and process exit to a cancellation source by hand. See [plugins](docs/plugins.md#hosting-programs-without-a-web-server).

- **Background work in a server (web plugin):** `worker (jobs: Jobs, stopping: CancellationToken) => …;` runs a
  handler whose parameters are services for the app's life (its token cancelled as the app stops, and waited for; if
  it throws, the app logs it and stops), and `worker every 30 seconds (lots: Lots) => …;` runs one as the app starts
  and each period after (a failing run is logged, and the next runs anyway; the period can be a `TimeSpan`, `every
  tick`). `hosted Mailer;` runs a .NET hosted service. A worker taking a service the server doesn't register is an
  error, as for health checks. apps/auction's clock is now a worker, rather than a loop beside the app in `Main`.

- **`https` in a server (web plugin):** `https;` redirects HTTP to HTTPS, and outside Development adds HSTS.
  `https certificate "server.pfx" password secret;` serves a .pfx certificate, and `https certificate "tls.crt" key
  "tls.key";` PEM files (as a Kubernetes TLS secret mounts them), loaded as the server binds, so a missing one stops
  it at startup and a test served from memory needs none. `urls` given as literals with no https:// address is a
  warning. See [plugins](docs/plugins.md#a-server).

- **`piper build --publish`:** compiles a program for a container image, into `bin/publish`: the executable (without a
  native launcher), its packages' assemblies with their native libraries for Linux (`linux-x64`, or `--runtime
  linux-arm64`), and a `Dockerfile` on .NET's `aspnet` image for a web program, or `runtime` otherwise. `docker build`
  then needs neither the SDK nor the source. Packages' native libraries can be resolved for another platform than the
  machine's (`ProgramReferences.Resolve(…, runtime)`), each remembered separately in `obj/piper`.

- **Every app is a `server`:** apps/stock and apps/grid map their routes in `endpoints` blocks (taking their `Shards`
  and `Jobs` as services), and their `/healthz`, which said "ok" whatever the node's state, is now liveness, with
  `/ready` asking the node's own actor; their Kubernetes readiness probes use it. apps/auction's server takes the
  auctioneer that Main and its tests make.

- **`config` in a server (web plugin):** `config Smtp;` reads a `config` record as the app is built, so a missing or
  invalid value stops it at startup, listing every problem, and registers it (and as `IOptions<Smtp>`). `config smtp:
  Smtp;` names it for the clauses after: `urls smtp.url;`.

- **A server's services are checked (web plugin):** a handler, health check or `stopping` hook taking a service of
  the program's own that nothing in the server registers is an error at the parameter, as is a class DI makes needing
  one, a singleton taking a scoped service (a captive dependency), and `authorize` in a server with no authentication
  scheme: what ASP.NET Core would only find at the first request. They're warnings where a registration could be out
  of sight. See [plugins](docs/plugins.md#a-server).
- **Fixed: tests running at once disturbed each other.** A captured program's output is now its own (a writer routing
  to the run it comes from), not whatever was on the console meanwhile, such as another app's log; and a property
  test's seed is its run's own. `.gitattributes` keeps text files LF in every checkout, as tests embed source and find
  markers in it. A golden file now says `Infinity`, as the program prints, rather than `∞`, which the old capture's
  culture gave.

- **`server` (web plugin):** `server Shop(settings: Settings) { urls …; services AppServices; policies Security;
  problems; openapi; endpoints Api; health "/healthz" ready "/ready" { check "db" (db: Db) => …; } stopping … ;
  shutdown 30 seconds; }` says what a web app is made of, and becomes `fn Shop(settings) => WebApplication`, with its
  middleware in the order ASP.NET Core needs (the policies' before the endpoints, ProblemDetails first). Health checks,
  readiness and stopping hooks are handlers whose parameters are services. `configure { … }` and `use { … }` take
  anything else. samples/todo-api and apps/tracker are built with one. See [plugins](docs/plugins.md#a-server).

- **Fixed: an argument with no type of its own, before the one that gives it.** In a generic call, `None`, `Ok(…)`,
  `Err(…)`, `null` and `[]` now take their type from the other arguments wherever they come: `Same(None, Some(3))`,
  `Pick([], [1, 2])` and `AssertEqual(Ok(42), Parse("42"))` were errors (PIP0240), as each argument was bound in turn.
  They're now bound after the arguments that have a type, and before lambdas.

- **Fixed (openapi plugin): optional and newtype parameters.** A query, route or header parameter that's an Option or
  a newtype (`done: bool?`, `after: BookId`) was described as a string, as ASP.NET Core binds it with its `TryParse`.
  It's now described as the value it's read as (a boolean, an integer, an enum's names), and an optional one as not
  required. Found by samples/todo-api, which now has an OpenAPI document, tests through `TestClient` and a newtype id.

- **Fixed: .NET's null through a type parameter.** In `fn Read<T>(…) => T? = JsonSerializer.Deserialize<T>(…)`, a
  result .NET may make null was read as a plain `T`, as Piper couldn't tell `T` would be a reference type, so JSON's
  `null` came back as `Some(null)`. It's now a `T?`, with null as None, for results, properties, `out` parameters and
  tasks; and a `T?` passes to .NET's `T?` parameters (`EqualityComparer<T>.Default.Equals(a, b)`), which it didn't.
  Generic code that took such a result as a plain `T` must now handle None. PiperInterop's `FromNullableReference`,
  `ToNullableReference`, `FromNullableTask` and `AfterOutReference` are no longer constrained to classes.

- **Property tests:** `test "…" for (bids: List<int>) runs 1000 { … }` says how many cases must pass (100 without it;
  PIP0115 for one that isn't a whole number above 0). `PiperGen` and `PiperGen<T>` need no `using Piper.Runtime;`,
  as only those of Piper.Runtime's names are seen without it.

- **The `openapi` plugin:** `"plugins": ["web", "openapi"]` brings Microsoft.AspNetCore.OpenApi and Piper.Web.OpenApi;
  `builder.Services.AddPiperOpenApi()` and `app.MapOpenApi()` serve an OpenAPI document whose schemas describe Piper's
  types as they're written in JSON: an Option as its value or null (and not required), a newtype as its value, an
  enum as its variants (a fieldless one its name, one with fields an object with `kind`), and a record with
  invariants as its fields. Without it, their converters hid them, so they were described as anything. A plugin of
  its own, so programs not using OpenAPI don't carry the package.

- **OpenAPI tags (web plugin):** `endpoints Library "/books" tags "Books" { … }` tags each endpoint, and an endpoint's
  own `tags "Loans", "Admin"` replace its group's (`WithTags`). Without them, an endpoint is tagged with its
  declaration's name, where ASP.NET Core tagged every endpoint with the application's.

- **Fixed: sorting by an optional key** (`items.OrderBy(a => a.RelativePath)`, a .NET `string?`) compiled but threw
  when it ran, as Options couldn't be compared. They now order as C#'s nullables do: None first, then the values as
  their type orders them.
- **Fixed: `Sum`, `Min`, `Max` and `Average` over optional numbers** (`List<decimal?>`) were an internal compiler
  error. Piper read .NET's `IEnumerable<decimal?>` as its own sequence of Options, which it isn't; such a sequence is
  now converted on the way, so they skip None as C#'s do. A .NET parameter holding `Nullable` some other way (a
  `List<int?>`, an `int?[]`) no longer takes a Piper value, rather than making C# that doesn't compile.

- **OpenAPI response types (web plugin):** each endpoint's metadata says what it answers, from its handler's type: an
  Option is 200 with its value or 404, a Result 200 with its value or a ProblemDetails for each status its error's
  `[Status]`es give (400 without), and markup 200 `text/html`. ASP.NET Core described the Option or Result itself as
  the 200's JSON. C# writes these by hand (`.Produces<Book>(200).Produces(404)`).
- **Fixed (web plugin):** htmx's script and live pages' stream, which Piper.Web maps for every app, were described as
  the app's API, so they appeared in its OpenAPI document.

- **Fixed: a .NET generic taking Piper.Runtime's `Option<T>` or `Result<T, E>`** (a library written for Piper) now
  infers `T` from a Piper optional or Result: `Hole<T>(Option<T> value) where T : IFormattable` is given an `int?`.
  Before, it was passed over, so such libraries needed an overload for each type. `url$` and `shell$` now take any
  optional formattable value through one, newtypes included (`{lot}`, a `LotId?`).

- **Shell strings:** `shell$"git log {branch} --author={author} -- {paths}"` is a `Shell` (Piper.Runtime): a program and
  its arguments, run without a shell, each hole one argument (or part of the one it's written against), so a hole
  can't add an argument, a pipe or a command. Quotes in the literal group words, None leaves out its argument, a list
  on its own is an argument each, and a Shell on its own its words. `await command.RunAsync()` gives its exit code and
  output (see [Shell strings](docs/language.md#shell-strings)).

- **URL strings:** `url$"/lots/{title}?q={text}&page={page}"` is a `Url` (Piper.Runtime), each hole encoded for the
  part it's in: a path segment (its `/` encoded), a query value or the fragment. A list is a segment each in the path
  and repeats its parameter in the query, None leaves its parameter out, and a `Url` goes in as it is in the path
  (`url$"{site}/lots"`) and as a value in the query (`?returnUrl={here}`). Values are written invariantly, dates in
  ISO 8601. A plain `$"…"` is a `Url` where one is wanted, and markup takes one in an attribute (see
  [URL strings](docs/language.md#url-strings)). apps/tracker-desktop's API client builds its URLs with it, rather than
  escaping each value and joining the query by hand.

- **SQL strings:** `sql$"select * from lots where id = {id}"` is an `Sql` (Piper.Runtime) whose holes are parameters
  (`@p0`, …), never text: numbers, text, dates, Guids, newtypes, Options (None is NULL), lists (for `in (…)`), and
  other `Sql`s, spliced in. Anything else is an error at the hole (PIP0402), and `Sql.Raw(…)` is the one way to put
  text in the SQL. A plain `$"…"` is an `Sql` where one is wanted, as C# target-types it, and a `FormattableString`
  where a .NET method takes one (EF Core's `FromSql($"…")`). `ToCommand(connection)` makes an ADO.NET command. A
  library can add its own kind of string with `[InterpolationKind("…")]` (see [SQL strings](docs/language.md#sql-strings)).
- **Raw SQL in the data plugin:** `shop.Orders.Query(sql$"…")`, `shop.Query<T>(sql$"…")` and
  `shop.ExecuteAsync(sql$"…")`. An interpolated string given to EF Core's `FromSqlRaw`, `SqlQueryRaw` or
  `ExecuteSqlRaw` is a warning (PIP0405), as its holes would go into the SQL as text; so is a `$"…"` given to a
  method's string overload where another takes a `FormattableString` or `Sql` (PIP0406).

- **Testing web apps in memory (web plugin):** `using let site = await TestClient.Start(() => BuildApp());` serves the
  app its build function makes from memory, with no port, and is an `HttpClient` calling it: it keeps cookies,
  `PostForm(url, fields, htmx: true)` sends a form's anti-forgery token for it, responses stream (so live pages'
  events can be read), and `site.App` is what the function returned. The server is Piper.Web's own, swapped in while
  the app is built (as WebApplicationFactory does), so the app's code is unchanged and no package is added.
  apps/auction's and apps/tracker's HTTP tests use it, as does `piper new --template web`'s program.
- **Fixed (web plugin):** a request ASP.NET Core couldn't bind (a missing body, say), to a handler returning an
  Option, a Result or markup, was a 500 from Piper.Web's response filter rather than ASP.NET Core's 400.
- **Generators written in Piper:** a property test's `in` takes a `PiperGen<T>`, made with `PiperGen.For<T>()`,
  `Range`, `In`, `Constant`, `OneOf`, `ListOf` and `Shuffled`, and changed with `Select`, `Where` and `SelectMany`:
  `fn Evens() => PiperGen<int> = PiperGen.For<int>().Where(n => n % 2 == 0);`. Their values shrink as any do (a
  shuffle towards the order given). apps/grid's order test uses `Shuffled`.
- **Editor support for markup:** typing a start tag's `>` closes the element after the cursor (the language server's
  `piper/autoClose`, which the VS Code extension asks; `piper.autoClosingTags` turns it off), and hover on HTML's
  elements and attributes, and htmx's, says what each is for, with a link to MDN or htmx's site.
- **Newtypes compare as their value:** one whose value can be compared (numbers, strings, chars, and .NET types
  implementing `IComparable<T>`) has `<`, `<=`, `>` and `>=`: `newtype Money = decimal;` gives `price < limit`.
- **A newtype in a form (web plugin)** is read as its value and then made, so its `requires … else "…"` message is
  the field's error, rather than "'0' isn't a money". apps/auction's prices and bids are now `Money`, always more than
  nothing.
- **`if` without `else` in markup takes any hole's value:** `{if bids > 1 { $"{bids} bids" }}` and
  `{if hot { count }}` work, as text and numbers do in holes, rather than only markup.
- **Property tests:** `test "name" for (xs: List<int>, n: int in 1..100, c: char in "aeiou") requires … { … }` runs
  its body over 100 sets of generated values, and when it fails, shrinks them to the smallest that still fail, shown
  with the seed that found them: `piper test --seed <n>` repeats it. Values are made from each parameter's type
  (numbers, text, collections, Options, Results, tuples, and the program's records, newtypes and enums, a record's
  `requires` refusing its own), or from an `in` range or collection; the test's `requires` throws away the cases it
  refuses. Generating and shrinking are Piper.Runtime's own (`PiperGen`, `PiperProperty`), with no other library:
  shrinking works on the random choices the values were made from, as Hypothesis does, so every type shrinks alike. A
  range of a type that isn't an integer or char is an error (PIP0399), as is one missing an end (PIP0398).
  apps/auction tests that bids never fall, whatever bids come in, and apps/grid that a job's figures don't depend on
  the order its chunks come back in.
- **Editor support for markup:** the VS Code grammar reads markup as markup (tags, attributes, text and entities, with
  holes as Piper, and `<script>`/`<style>` as text), where it coloured it as code. In the language server, a
  component tag is its function: coloured, hovered and gone to, and renamed in both its tags (the lexer now keeps where
  a closing tag names its element). An error in a component's attribute is reported at the attribute. Completion in
  markup offers tags (HTML's elements and the components), attributes (a component's parameters, HTML's and htmx's),
  and some values: the record's fields for an input's `name` inside `<form for={NewLot}>`, an input's `type`, a form's
  `method`, `hx-swap`. Text offers nothing, and a hole is code, as elsewhere.
- **Checked field names (web plugin):** `FormErrors` is now `FormErrors<T>`, and a constant field name given to
  `errors.For("…")` or `form.Text("…")` must be one of the record's fields (PIP0511). Any .NET method can ask for the
  check by marking a string parameter `[PiperFieldName]` (Piper.Runtime). Programs naming the type write
  `FormErrors<NewLot>` instead of `FormErrors`.
- **Functions in an `actor`** (both actor plugins): `fn` and `async fn` beside its state and handlers, for what its
  handlers share; they can change its state, and run only from its handlers.
- **Query values in route functions (web plugin):** a handler's parameters outside its route, of a simple type or
  optional, are parameters of its route function too, after the route's: `SiteRoutes.Search(q: "clock", page: 2)` is
  `/search?q=clock&page=2`, and optional ones default to None and are left out. A required one of the program's own
  type isn't seen, as the plugin judges by the type as written.

- **A form's every mistake at once (web plugin):** each of a record's `requires` is checked wherever the fields it
  mentions were read, so a field that isn't a number no longer hides what's wrong with the others, and several failed
  conditions are all reported. For it, the compiler gives each record with `requires` a static `__PiperRequirements`
  table (Piper.Runtime's `PiperRequirement`): the fields each condition mentions, and a check giving its message.
  `FormErrors.Form` puts several form-wide messages together. A `requires` without `else` that mentions one field is
  now that field's error, as one with `else` was.
- **Typed forms:** `<form for={NewLot}>` checks the name of each `input`, `select` and `textarea` written in it against
  the record's fields (PIP0510, listing them); `for` isn't written out. apps/auction's forms use it.
- **List literals type `None`s from their other elements:** `[None, None, Some("ada")]` is a `List<string?>`, `[[1], []]`
  a `List<List<int>>`; `null`, `Ok(…)`, `Err(…)` and untyped lambdas likewise, with no annotation.
- **`[return: NotNullIfNotNull]`:** `WebUtility.HtmlDecode(text)`, `Path.GetFileName(path)` and the like give a plain
  value for a plain argument, rather than an Option. Code that wrote `?? fallback` after such a call is now an error
  (PIP0261, as `??` needs an optional), and can drop it: the samples' `interop.piper` did.
- **Functions in a `statemachine`:** `private fn Take(…) { … }` beside the machine's data and states, for what its
  transitions share; they're its class's methods. apps/auction's `LotLife` takes a bid in one.

- **apps/auction:** an auction site proving Piper's web UI: markup pages, typed links, forms read into records with
  `requires` messages, htmx, and live pages (each bid, and each lot's countdown, pushed to every page watching it). Each
  lot is an actor taking its bids one at a time, and its life a state machine. Tried in a browser as well as tested.
  From it:
  - **Actors' ask handlers infer from their declared result** (both actor plugins): `Ok(view)`, `Err(…)` and `None` in
    a handler's body take their type from its `=> Result<…>`, as in a function.

- **Markup:** HTML in expressions, as JSX has it, of a new builtin type `Html`: `<td><a href={url}>{title}</a></td>`.
  Holes are escaped by their type (a string is text, an `Html` is markup, a number takes a format, `{price:C}`), and
  what can't go in markup (a Task, a bool, a function) is an error saying so. Components are functions used as tags
  (`<Badge text="x" />`, children as a `children` argument); `{for x in xs { … }}` and `if` without `else` work in
  holes; `name={flag}` is a boolean attribute; void elements, `<script>` and `<style>` and `<!doctype html>` are HTML's.
  It compiles to calls to Piper.Runtime's `PiperHtml`, and a web handler returning `Html` answers with a page.
- **Live pages (web plugin):** `Live.Publish("lot-7", "price", fragment)` pushes markup to every page watching the
  topic, which htmx's server-sent events extension (htmx-ext-sse 2.2.2, Zero-Clause BSD, loaded by `<Htmx.Script />`)
  swaps in: `<section hx-ext="sse" sse-connect={Live.Url("lot-7")}><span sse-swap="price">…</span></section>`. Apps
  serve the connections at `/_piper/live`, with keep-alives, and drop a slow page's oldest updates rather than wait.
  Checked in a browser (headless Edge): htmx's swaps and pushed updates both arrive.
- **htmx (web plugin):** apps serve htmx 2.0.4 (Zero-Clause BSD, in Piper.Web) at `/_piper/htmx.min.js`, which
  `<Htmx.Script />` loads with the anti-forgery token sent as a header on htmx's requests. `endpoints Site layout Page`
  puts each endpoint's markup in `Page(body)` for a browser, and sends the fragment alone when htmx asks. An
  `HtmxRequest` handler parameter reads htmx's request headers and sets its response ones (`Redirect`, `PushUrl`,
  `TriggerEvent`, `Retarget`, …).
- **`[NotNullWhen(true)] out T?`** (`Version.TryParse`, `HttpHeaders.TryGetValues`) gives a plain value where the call
  returned true, as `[MaybeNullWhen(false)]` already did.
- **Messages for `requires`:** `requires title.Trim().Length > 0 else "Give the lot a title."` fails with that message
  (any string, built only on failure), as a `PiperContractException` naming the field or parameter the condition is
  about when it mentions just one. Forms show it beside that field, JSON reports it, and a constant call breaking it
  is reported with it. Conditions without `else` are unchanged.
- **Typed links and forms (web plugin):** a named endpoint (`get Lot "/lots/{id:int}" (id: LotId) => …`) has a route
  function, `SiteRoutes.Lot(id: LotId) => string`, so links are checked against the routes and their types. A handler
  taking a `Form<T>` gets a posted form read into a record: `form.Result` is `Ok(record)`, or `Err(errors)` with a
  message per field (missing, or not a value of its type) and for a failed `requires`. Every `<form method="post">` in
  markup carries an anti-forgery token, which `Form<T>` checks.
- **Piper.Runtime's `Option` and `Result` from .NET are Piper's:** a library member returning them (such as
  `form.Result`) is matched with `Some`/`None` and `Ok`/`Err`, as Piper's own are.
- **Generic .NET methods' constraints are checked:** an overload whose inferred type arguments break its constraints
  (`where T : IFormattable` for an Option) is passed over, as C# does, rather than chosen and then rejected by C# as an
  internal compiler error.

- **`piper run --watch` reloads the browser:** after a restart, a web program's open pages reload once it answers
  again. Each watched run of a program using ASP.NET Core loads Piper.Watch (beside piper), an ASP.NET Core hosting
  startup putting a small script in the HTML pages a browser asks for, which listens to an event stream the watcher
  serves on loopback. Only pages are touched: JSON, files and event streams the program serves pass through unbuffered.

- **Newtypes:** `newtype IssueId = int;` makes a distinct type over a value, which can't be mixed up with its type or
  another newtype over it (`FindIssue(ProjectId(5))` is an error). It's a record struct with one field, `value`, so it
  costs nothing at run time, and takes `requires` (`newtype Email = string requires value.Contains("@");`). It stands for
  its value: it prints as it, formats and sorts as it, is it in JSON, and has `IssueId.Parse(text) => IssueId?` and the
  `TryParse` ASP.NET Core binds routes with. The data plugin stores it as its value, and numbers keys of `int` and
  `long` newtypes. [apps/tracker](apps/tracker/README.md) now keys its issues, projects and comments with newtypes,
  which also split its `NotFound(id: int)` error, used for both, into `NoIssue` and `NoProject`.

- **apps/tracker:** an issue tracker API written in Piper, to find what a real program needs. It has a domain with a
  workflow state machine, SQLite storage, a web API with policies, and tests at each layer.
  [apps/tracker/NOTES.md](apps/tracker/NOTES.md) records each gap it found, and what was done. From it:
  - **Enums:** every enum has `value.Name`, and a fieldless one has `E.Values` and `E.Parse(text) => E?`. A variant's
    JSON and printing show only its fields.
  - **Interpolated raw strings:** `$"""…{x}…"""`, and `$$"""…{{x}}…"""` where single braces are text, as in C#.
  - **`let _ = f();`** is a discard, so a block can have several.
  - **Nullable tasks:** a .NET call returning `Task<T?>` or `ValueTask<T?>` (`FirstOrDefaultAsync`, `ReadLineAsync`,
    `GetFromJsonAsync`) gives an Option when awaited, None for null, rather than a hidden null.
  - **State machines:** `Machine.Resume(state, …)` makes one in a saved state, and `CanFire(event)` checks an event
    given as data.
  - **Data:** fieldless enums are columns holding the variant's name (optional ones nullable), and `let` fields are
    columns.
  - **Web:** a `policies` block can take parameters, so limits can come from configuration.
  - **.NET statics:** a .NET type's static property or field can be assigned (`Environment.ExitCode = 2`).
  - **Name lookup:** a type position looks past a function or variable of the name, so a function `List` doesn't hide
    `List<T>`.
  - **Branches build a Result or Option together:** `match r { Ok(v) => Ok(v * 2), Err(e) => Err(e) }` and
    `if … { Some(x) } else { None }` infer their type from all the branches, where nothing else says it.
  - **Web:** a warning for an error type's field named `status`, which its ProblemDetails would leave out.
  - The tracker also has a webhook notifier with `[Retry]`, and an admin command line (`add-project`, `import`,
    `list`) from an `args` declaration. tests/AppsTests runs its tests with the rest.
- **apps/tracker-desktop:** an Avalonia window on the tracker's API, laid out in code with an MVVM view model using
  `[Notify]` and `[Command]`, and tested against a fake API. From it:
  - **Inherited .NET statics:** a static property or field declared by a base class can be named through a derived
    type, as in C# (`ComboBox.SelectedIndexProperty`, declared by `SelectingItemsControl`). Static methods already could.
  - **`_` lambda parameters** bind nothing, so `(_, _) => …` is allowed, as in C#.
  - **Aspects:** a `[Notify]` class's `OnCommandFailed(command: string, error: Exception)` method hears of each
    `[Command]`'s failure. Without one, an async command's exception is thrown on the synchronization context it
    started on, as `async void`'s is, rather than lost in a task nobody awaits.
- **apps/stock:** products as actors spread over several nodes on the remote actors plugin, behind an HTTP API on
  each, with a Dockerfile, a Docker Compose file and a Kubernetes manifest (a StatefulSet and a headless service) for
  running it as pods on AKS. A load run reserves one product through every node at once without overselling it, and a
  dashboard served by every node shows the nodes, their products and which node answered each request. From
  it:
  - **A file's class** (where its top-level functions go in C#) is no longer hidden by a member of the same name: a
    `Products()` method calling a function of products.piper was an internal compiler error.
  - **Setting a .NET `T?` value-type property** (`request.Headers.ConnectionClose = true`, or `None`) converts the
    Option to a Nullable, rather than being an internal compiler error.
- **apps/grid:** grid computing on the remote actors plugin. A coordinator shares a Monte Carlo Value at Risk among
  workers that ask it for chunks, so workers (Kubernetes pods in a Deployment) can be scaled mid-job. A chunk not sent
  back within its lease goes to another worker, and per-chunk seeds give the same figures however the work was
  shared. Its dashboard shows the chunks, the workers, the result and the history. It needed no compiler fixes.

- **Expression trees:** a lambda passed where .NET takes an `Expression<Func<…>>` is an expression tree, as in C#. So
  a query on an `IQueryable` uses `Queryable`'s operators, which a provider such as EF Core translates to SQL, rather
  than `Enumerable`'s, which ran it in memory. `let f: Expression<Func<int, bool>> = x => x > 1;` works too.
  - What a tree can't contain is error PIP0397: statements, assignment, `await`, `throw`, `match`, `is` patterns,
    tuples, and `??` or `?.` on optionals (compare with `None` instead).
- **Fix:** where a .NET method has a generic and a non-generic overload, `IEnumerable<T>` over a Piper type now counts
  as more specific than `IEnumerable`, as C# does, so `people.AsQueryable()` is an `IQueryable<Person>`.
- **Fix:** overriding a .NET `protected internal` method (`DbContext.OnModelCreating`) emits `protected`, as C#
  requires across assemblies, rather than C# that didn't compile.
- **Native libraries in packages** (SQLite's `e_sqlite3`) are loaded for `piper run` and `piper test`, and copied next
  to a program by `piper build`.
- **`config` plugin:** `config Smtp from "Smtp" { port: int = 587 requires port > 0; … }` becomes a record, and
  `LoadSmtp(configuration)` / `SmtpFrom(configuration)` reading it from an `IConfiguration`. Every missing, unparsable
  or invalid value is reported at once, with its key. Fields can be optional, lists, or nested configs.
- **`data` plugin:** `database Shop { table Orders: Order key id index customerId; }` becomes an EF Core `DbContext`.
  Options are nullable columns, compared with `None` as SQL's `IS NULL`, through Piper.Data, which comes with the
  plugin. A `key` or `index` naming a missing field is an error.
  samples/data uses it with SQLite, and with `config` for its settings.

- **Web: who may call an endpoint, and how often.** `authorize`, `authorize "admin"`, `anonymous`,
  `ratelimit "api"`, `ratelimit off`, `cors "partners"` and `cache "short"` after a group's or an endpoint's route
  (an endpoint's replacing its group's for the same concern), becoming ASP.NET Core's endpoint conventions.
  - A `policies` declaration defines them: authorization policies with `roles` and `claim`; `fixed`, `sliding`,
    `bucket` and `concurrent` rate limits (`fixed 100 per minute`), answered 429; CORS `origins`; and output cache
    `expire` times. It becomes `fn Name(services)`, registering them, and `fn UseName(app)`, adding the middleware
    in the order ASP.NET Core needs.
  - Checked: `authorize` with `anonymous`, a repeated word, and a missing policy name are errors. An endpoint naming
    an undeclared policy, a policy no endpoint uses, and policy words without any `policies` block are warnings.
  - Plugins can keep notes on an expansion for their analysis: `PluginParser.Data`, read back as `PluginExpansion.Data`.

- **Decorators:** a plugin can add attributes that rewrite the function or method they're on,
  `[Retry(3)] fn Fetch() { … }`, for cross-cutting concerns such as tracing, timing and retrying. It's code
  generation at compile time: the result is ordinary Piper, type-checked as usual, the attribute never reaches C#, and
  nothing happens at run time beyond the code written.
  - Plugin API: `Decorator`, given a `DecoratorContext` with the function, the attribute's arguments, a template,
    fresh names, and `WithPrologue` and `WithBody` to rewrite it.
  - Several on one function apply nearest first. `requires` clauses are still checked before a decorator's code.
  - A decorator on anything but a function or method with a body is error PIP0507.
  - A new bundled plugin, `aspects`, has `[Trace]`, `[Timed]` and `[Retry(times, delayMs)]`.
  - **On classes and records** too, as a C# source generator: a `Decorator`'s `Type` handler gets a
    `TypeDecoratorContext` and returns the type rewritten, plus any declarations to add beside it (a record's new
    methods, in an `extend` block). `ClassMembers`, `WithMembers`, `WithBase` and `DecorateMethods` help write it.
  - `[Notify]` (in `aspects`) makes a class implement `INotifyPropertyChanged`, for WPF and Avalonia data binding. Each
    settable property and public `mut` field raises `PropertyChanged` when it changes, and so do the computed
    properties that use it. `[Trace]` on a class traces each of its methods.
  - **Markers:** a `Decorator` with `ReadBy` set does nothing itself. It stays on a class's members for the named
    type decorator to read, and is error PIP0508 anywhere else.
  - `[Notify]` reads three: `[Command]` on a method adds an `ICommand` property (`Save` gets `SaveCommand`), whose
    `CanExecute` is a `CanSave` method or property and which raises `CanExecuteChanged` when what that reads changes;
    `[NotifyIgnore]` leaves a property alone; and `[AlsoNotify("Total")]` raises other properties too.
- **Fix:** a lambda or local function in a record struct's method can use `this` and its fields. The emitted C# didn't
  compile (CS1673); the method now copies `this`, which is readonly, for its closures to use.

- **Dictionary literals:** `["Ada": 36, "Alan": 41]` is a `Dictionary<string, int>`, and `[]` one where a dictionary
  is expected. Spreads add another dictionary's entries, with later keys replacing earlier ones:
  `[...defaults, "port": "8080"]`.
  - A constant key written twice is error PIP0395, and an element that isn't an entry or a spread PIP0396.
  - `Piper.Runtime` gains `PiperDictionary.Of`, which literals with spreads compile to.

- **Record structs:** `record struct Vec(x: double, y: double);` is C#'s `readonly record struct`: a record that's a
  value type, copied rather than shared and allocating nothing, with everything a record has.
  - A record struct that holds itself inline is error PIP0394.
  - `Vec[3]` works when its default (all zeros) is a valid value: every field's default is, and it has no invariants.

- **Bitwise operators:** `&`, `|`, `^`, `~`, `<<` and `>>`, with C#'s precedence, typing and results.
  - On integers and chars, on bools (`&`, `|` and `^`, which always evaluate both sides), on .NET enums as flags
    (`FileAttributes.ReadOnly | FileAttributes.Hidden`, `~FileAttributes.ReadOnly`), and through .NET types' own
    operators (`BigInteger`).
  - `>>` is two `>` tokens joined by the parser, so `List<List<int>>` still closes.
  - Constants fold, so `let mask: byte = 0x0F | 0x30;` fits a byte.
  - The precedence levels in SyntaxFacts are renumbered to fit them in; the order of the existing operators is unchanged.
- **More compound assignments:** `&=`, `|=`, `^=`, `<<=`, `>>=`, and `??=`, which sets an optional only when it has no
  value, evaluating the right side only then. Its target must be a name, a member of one or an index of one (PIP0389),
  and optional (PIP0388).
- **`not`, `and` and grouping in patterns:** `x is not null`, `c is >= 'a' and <= 'z'`, `not (Red | Green)`, as C#'s
  `not`, `and` and parentheses.
  - They count towards exhaustiveness as what they mean, so `match o { not null => …, null => … }` needs no `_`.
  - `not` can't bind variables (PIP0390); a `not` or `and` pattern nothing matches is an error (PIP0393), as in C#.
  - `not` and `and` are keywords only in patterns, so they can still be names.
- **Labelled loops:** `outer: for … { for … { break outer; } }`, and `continue outer;`, on `while`, `for` and
  `await for`, also in match arms. They become `goto`s to labels after the loop or at the end of its body.
  - New errors PIP0112 (a label on something other than a loop), PIP0391 (a label already used by a loop around it)
    and PIP0392 (no loop around the jump has that label).

- **Events in classes:** `event Changed: EventHandler;` is a C# field-like event, of a delegate type returning nothing
  (a .NET one or a function type).
  - The class raises it by calling it (`Changed(this, args)`), and anyone adds and removes handlers with `+=` and `-=`.
  - A class implements a .NET interface's events, so `class Person: INotifyPropertyChanged` can now be written, for
    WPF and Avalonia data binding.
  - New errors PIP0385 (an event's type) and PIP0386 (raising it outside its class).
- **Type aliases:** `type Lookup = Dictionary<string, List<int>>;`, interchangeable with the type, as C#'s
  `using Lookup = …;`. Aliases may name other aliases, in any order; a cycle is error PIP0381 and type parameters
  PIP0110.
- **`params` parameters:** `fn Log(format: string, params args: object[])` takes any number of arguments as an
  array, or an array itself, as in C#. Only on a function's or method's last parameter (PIP0111, PIP0382).
  - A collection literal passed to a generic `T[]` parameter now infers T from its elements (`First([1, 2])`).
- **`lock`:** `lock gate { … }` (or `lock (gate) { … }`), as C#'s, on a .NET 9 `Lock` or any object.
  - Locking a value is error PIP0383, and awaiting inside the block PIP0384.
- **Data-race warnings (PIP0387):** in a parallel loop's body (`parallel for`, `Parallel.ForEach`) or a group of
  tasks (`concurrency { }`, `Task.WhenAll(Task.Run(…), …)`), changing a variable, field or non-thread-safe collection
  that other threads use, outside a `lock`, is a warning.
- **Counting from the end and slicing:** `xs[^1]` is the last element, and `xs[1..3]` a slice, on arrays, strings and
  .NET collections (anything with a `Length` or `Count`, and for slices a `Slice(int, int)`, such as `List<T>`).
  - As in a `for` loop, a range includes its end unless it's `..<`, so `xs[1..3]` is three elements and `xs[1..<3]`
    two (C#'s `xs[1..3]`).
  - Either end can be left out (`xs[2..]`, `xs[..<2]`) or count from the end (`xs[1..^1]`, to the last element).
  - They're written as C#'s `^` and `..`.
  - New errors PIP0373–PIP0379, for a `^` outside `[…]`, something that can't be counted from the end or sliced, a
    slice that isn't alone in `[…]`, `^0` as an element, `..<` without an end, and a `for` loop over an open range.
- **`nameof` and `typeof`,** as in C#.
  - `nameof(order.Total)` is the constant `"Total"`, once the name is checked; it can name a variable, member,
    instance member through its type, enum variant, type or namespace. Renaming what it names renames it too.
  - `typeof(Point)` is a `System.Type`, and works in attribute arguments: `[JsonDerivedType(typeof(Circle), "circle")]`.
  - Both are keywords only before `(`. New error PIP0380, for a `nameof` of something that isn't a name.
- **State machines plugin:** `plugins/Piper.Plugins.StateMachines` adds `statemachine` declarations, with states,
  events with parameters, `on Pay(amount: decimal) => Paid if … { … }` transitions with guards and actions, and
  `enter`/`exit` blocks.
  - Each machine becomes a class with a method and a `Can…` check for each event, `State`, `IsFinal` and `Events`,
    plus an enum of its states and one of its events (`Fire` replays those).
  - Its shape is checked, and states nothing reaches are warned about.
  - It comes with piper as `state-machines`. See `samples/state-machines`.
- **Workspace trust:** in a folder the editor hasn't trusted, the language server loads only the plugins that come
  with piper or were added. It leaves out plugins given by path, packages (whose restore runs the folder's MSBuild
  files) and assemblies, with a PIP0404 warning on piper.json.
  - VS Code's extension supports untrusted workspaces in a limited way, ignores a workspace's `piper.server.*`
    settings, and loads the rest once the workspace is trusted.
- **Plugin keywords in the editor:** `PluginKeyword.Description`, `Snippet` and `Words` give hover text for a keyword
  and its construct's words, and completion offers a plugin's keywords where they can start, with a snippet of the
  construct. Every bundled plugin has them.
- **`piper plugin new <folder>`:** makes a plugin project (adding a `repeat` statement) and an example program.
  - Each build adds the plugin to piper.
  - In this repository it references the compiler's project; elsewhere, the installed compiler.
- **Clearer plugin version errors:** a plugin that fails because it was built against another compiler (a missing
  method or type) is told to rebuild. One built against a newer compiler is refused when it's loaded.
- **`piper plugin add`:** adds a plugin under a name a piper.json can use, as it does those that come with piper,
  without a new copy of piper. The plugin's folder is copied to `~/.piper/plugins` (or `$PIPER_PLUGINS`).
  - Bundled names win, and the error for an unknown name lists the plugins that have been added.
  - `piper plugin list` and `piper plugin remove` manage them.
  - The language server asks the editor to watch that folder, so a plugin added while the editor is open is picked up
    straight away, highlighting included.
- **Plugins' words are highlighted:** the editor colours the words special to a plugin's construct as keywords, as it
  does the plugin's keyword: `on` in an actor; `state`, `on`, `enter` and `exit` in a state machine; `singleton` in
  `services`; `get` in `endpoints`. A plugin reads them with the new `PluginParser.ReadWord()` or with `ExpectWord`.
- **Grid computing sample:** `samples/grid` uses the remote actors plugin to spread a job over worker nodes on many
  machines. Workers take the next chunk when they're free, and a failed or unreachable node's chunks go to the
  others.
- **Options and Results as web responses:** an `endpoints` handler returning `T?` answers 200 with the value or 404
  for None, and one returning `Result<T, E>` answers 200 or the error as ProblemDetails, so the
  `match … { Some(x) => Results.Ok(x), None => Results.NotFound() }` boilerplate goes away.
  - `[Status(409)]` (from `Piper.Web`) on an enum variant, or on a whole enum or record, chooses an error's status;
    without one it's a 400. The error's JSON (a variant's `kind` and fields) becomes ProblemDetails extension members,
    and a string error is its `detail`.
  - It's an endpoint filter added only to handlers returning an Option or Result, so others are unchanged.
- **Attributes on enum variants:** `enum LoanError { [Status(404)] UnknownBook(code: string), … }`, put on the
  variant's C# class.
- **Plugins can ship assemblies** for the code they write to call (`IPiperPlugin.References`, relative to the plugin's
  assembly). A program using the plugin references them, and a built one gets copies.

- **Trailing closures:** a block after a call is its last argument, as a lambda: `Repeat(3) { i => … }`,
  `Guarded { … }`. In an `if`, `while`, `for`, `match` or `requires` head, a `{` is still the body. A closure that
  awaits is async.
- **Fix:** a lambda capturing a range loop's variable (`for i in 0..<3 { actions.Add(() => i); }`) now captures each
  iteration's value, as it already did when looping over a collection, rather than the last one.
- **Better errors:** an error in a lambda passed to an overloaded .NET method (`Parallel.ForEach`, `Task.Run`,
  `List.ForEach`) is reported where it is, rather than as "no overload accepts these arguments".
- **Packages, frameworks and assemblies:**
  - `piper.json` can list `"packages"` (NuGet), `"frameworks"` (such as `Microsoft.AspNetCore.App`) and `"references"`
    (.NET assemblies).
  - The binder, `piper run`, `piper test` and the editor use them, and `piper build --exe` copies the package and
    referenced assemblies next to the program, naming the frameworks in its runtimeconfig.json.
  - Packages are restored with the .NET SDK and cached in the folder's `obj/piper`.
  - New errors PIP0401–PIP0403.
  - See `samples/web`, a minimal API with dependency injection.
- **Classes:** `class Account(owner: string) { mut balance = 0m; fn Deposit(amount: decimal) { balance += amount; } }`
  declares a reference type with identity and state that can change, as a C# class with a primary constructor.
  - Fields (`let`, `mut`) are private and methods public unless marked otherwise; inside methods, fields, constructor
    parameters and other methods are named bare, or through `this`.
  - A class derives from one Piper or .NET class (`class NotFound(id: int): Exception("No item {id}")`), then any
    traits and .NET interfaces. `abstract`, `sealed`, `virtual`, `override` and `abstract fn` work as in C#, and
    overriding public and protected .NET methods (`ToString`, `Collection<T>.InsertItem`) is checked against their
    signatures.
  - `base.F()` calls the base class's version of a method, Piper or .NET (protected ones too).
  - Type patterns test for a type and bind it: `match shape { c: Circle => …, _ => … }`, `if o is s: string { … }`,
    as C#'s `shape is Circle c`. They work on classes, records, traits, interfaces, .NET types and `object`.
  - Casts go down to a derived class: `(Circle)shape`.
  - **Static fields:** `static mut issued = 0;`, `public static let Default = Config("default");`. One per class (per
    instantiation for a generic class), named bare inside it and as `Ticket.issued` outside. Their initializers run in
    the order they're declared, and using one before it's initialized is an error rather than C#'s silent default.
  - **Invariants:** `class Account(owner: string, opening: decimal) requires opening >= 0m { … }`. The conditions are
    over the constructor's arguments, and are checked whenever one is made, before the field initializers and the base
    constructor run, and at compile time for constant arguments (PIP0340), as for records.
  - Field initializers can call the class's static methods bare.
  - **Properties:** `prop Balance => decimal = balance;`, `prop Count: int { get; private set; } = 0;`,
    `prop Name => string { get = name; set { name = value.Trim(); } }`. Read and set like fields; public unless marked
    `private`, with an optional `private set`. They can be static, `virtual`, `abstract` and `override`, override .NET
    properties, and implement .NET interfaces' properties (`IReadOnlyCollection<T>.Count`), so a class can now derive
    from a .NET base with abstract properties, such as `Stream`. New errors PIP0355 and PIP0356.
  - **Indexers:** `prop this[x: int, y: int] => int { get = …; set { … } }`, used as `grid[1, 2] = 5`. They can be
    overloaded by parameter types, be virtual, abstract or overridden, and implement .NET interfaces' indexers
    (`IReadOnlyList<T>`).
  - **Constructors** besides the primary one, written as in C#: `Account(owner: string): this(owner, 0m) { … }`. A
    creation, or a derived class's base list, chooses among them by argument types. They can be private or protected.
  - **`init` properties:** `prop Opened: DateOnly { get; init; } = …;`, settable only in constructors.
  - **`protected`** fields, properties, methods, indexers, constructors and setters (`protected set`), checked as C#
    does. Overrides keep the access of what they override, so an override of a protected .NET method can no longer be
    called from outside, which C# rejected.
  - **Attributes** on a class's fields, properties, indexers and constructors (`[JsonPropertyName("id")] public let Id`).
  - New errors PIP0357–PIP0359.
  - The editor's outline shows a class's fields, properties, indexers, constructors and methods; completion offers a
    class's public fields and properties; `prop`, `get`, `set`, `init` and `protected` are highlighted.
- **Properties in traits and `extend` blocks:** read-only, `trait Shape { prop Area => double; prop Label => string =
  "shape"; }`, implemented in `extend` blocks or by a class's properties, and read through a bound like
  methods. `extend int { prop IsEven => bool = this % 2 == 0; }` adds a C# 14 extension property.
- **Properties are written as functions or fields are:** one that computes its value is written as a function is,
  `prop Count => int = todos.Count;`, `prop Name => string { get = name; set { … } }`, `prop Area => double;` in a
  trait; one that stores it is written as a field is, `prop Count: int { get; private set; } = 0;`. The C# forms,
  `prop Count: int => …` and `get => …`, are errors saying how to write them.
- **Object initializers:** `Window("Piper") { Width: 640, Height: 480 }` sets properties and fields after the call that
  makes the object, as C#'s `new Window("Piper") { Width = 640, Height = 480 }` does, for Piper classes and .NET
  types. Each is set as an assignment would set it, except that `init` properties can be set there too; a `{` after a
  call that isn't followed by `Name:` is still a trailing closure. Setting a .NET type's `init` property elsewhere is now
  reported (PIP0359) rather than failing in the C# compiler. New errors PIP0360 and PIP0361.
- **`piper new`:** `piper new hello` makes a program folder with a `piper.json`, a `main.piper` (with a test) and a
  `.gitignore`; `--template web` makes a small web API instead, using the services and web plugins.
- **`piper run --watch`:** runs the program, and when a `.piper` file or `piper.json` changes (not under `bin/` or
  `obj/`), stops it and runs it again; a version that doesn't compile waits for the next save. A web server is
  restarted on its port, and a run ends with its watcher however that ends, so none is left holding the port.
- **Library visibility (breaking for libraries):** a top-level declaration is now visible throughout the program or
  library but not outside it, as C#'s `internal` (and C#'s default) is; `public` exports it as part of a library's API
  (`public record Point(…)`, `public fn Distance(…)`, `public extend string { … }`), and `private` still keeps it to
  its file. Programs are unaffected. A library built before needs `public` on what its users call. A public
  declaration can't mention a type that isn't public (PIP0363), and a declaration can't be both (PIP0362). Hovers
  show `public`.
- **Nested object and collection initializers:** in an object initializer, `Font: { Size: 14 }` sets properties of
  the object `Font` already holds, and `Tags: { "a", "b" }` adds to the collection `Tags` holds with its `Add`
  (`Scores: { { "x", 1 } }` for an `Add` taking several arguments), as C#'s `Font = { Size = 14 }` and
  `Tags = { "a", "b" }` do; they nest. The member must hold an object, not an optional (PIP0365) or a value type
  (PIP0366), and a collection must implement `IEnumerable` (PIP0367). Its `init` properties can't be set there, as in
  C#. A block straight after `Name:` in an initializer now needs parentheses. New errors PIP0364 to PIP0368.
- **Settable and static trait properties:** `prop Name => string { get; set; }` in a trait requires a settable
  property, which a class implements with a public settable property (or `public mut` field) and code sets through the
  trait or a bound (`pet.Name = "Rex"`, `item.Name += "!"`); only a class can implement one (PIP0371), and a compound
  assignment through an expression other than a name is reported (PIP0372). `static prop Zero => T;` is a static
  abstract property, with an optional default, implemented like a static method and read as `T.Zero` or `Money.Zero`.
  As C#: `string Name { get; set; }` and `static abstract T Zero { get; }`. **Fix:** a class can now implement a trait
  with static methods or properties; it was reported as using the trait as a value's type (PIP0319). A class property
  that doesn't match a trait's is now reported as not matching (PIP0246) rather than missing.
- **Fix:** a record with invariants is now JSON as any other record: its fields' attributes (`[JsonPropertyName]`,
  `[JsonIgnore]`), defaults and the serializer's options apply. Its converter used to read the fields itself and
  ignored them; the compiler now nests one in the record that has System.Text.Json read a copy of its fields, without
  reflection. An enum variant with a field JSON would name `kind`, where the variant's name goes, is now an error
  saying so when the enum is first read or written, rather than JSON that can't be read back.
- **Piper's types in JSON and web requests:** System.Text.Json now writes an Option as its value or `null` (it was
  `{"IsSome":…,"Value":…}`, and `null` couldn't be read back), reading a missing field as None; an enum's variant as its
  name or an object naming it in `"kind"` (it was `{}`); and JSON a record's `requires` rejects as a `JsonException`, so
  a web API answers it 400 rather than 500. An optional handler parameter in `endpoints` (`name: string?`) is None when
  the request leaves it out, rather than a 400, through `Option`'s new `TryParse`. The todo-api sample uses all of them.
- **.NET interface indexers that may take or give null:** a class can implement one (`string? this[string? key]`) with an
  indexer of Options (`prop this[key: string?] => string?`), as it already could a property; it was reported as an
  interface Piper can't implement.
- **`required` members:** making a .NET type with `required` members needs them set in its initializer
  (`Person() { Name: "Ada", Age: 36 }`), as in C#, unless its constructor is `[SetsRequiredMembers]`; one left out is
  reported (PIP0369) where it used to fail in the C# compiler, and so is one changed in braces rather than given a
  value (PIP0370). A Piper class deriving from such a type has them too, unless its base constructor sets them, and
  then its own constructors are marked as setting them. **Fix:** such types' constructors could not be called at all,
  as C# marks them obsolete for older compilers; that marker is now ignored, as C# ignores it.
- **The args plugin:** `args Options { port: int = 5080 "The port"; verbose: bool; files: List<string>; }` declares a
  program's command-line options as a record, with `Options.FromArgs(args)` (which prints the usage on `--help`, and
  the mistake and usage on a bad command line, and exits) and `Options.Parse(args)` (a Result, for tests). Flags,
  optional and required options, defaults, `--name=value`, `--` and the other arguments; typos suggest the nearest
  option. It comes with piper, and samples/todo-api and the web template use it.
- **Plugins come with piper:** the `piper` tool carries the actors, remote-actors, parallel, services and web plugins,
  which a `piper.json` names, `"plugins": ["services", "web"]`, rather than giving a path to a built copy. The samples
  name theirs, so they no longer need the plugins built first. A path still works for any other plugin.
- **Fix:** an Option of a value type passed where .NET takes `object?` (`Console.WriteLine(xs?.Count)`) generated C#
  that didn't compile; the value is now boxed, or null for None.
- **Bare calls in trait defaults:** a trait's default methods name its other methods and properties bare (`Name()` for
  `this.Name()`), as a class's methods do.
- **Fix:** a class deriving from a generic Piper class (`class Names: Repository<string>`) didn't see what it inherited:
  its fields, properties, indexers and methods weren't found, and its overrides were reported as overriding nothing.
  They're now found, with the base's type parameters replaced by the class's type arguments, as are the .NET members of
  a generic .NET class at the root (`class Ints: Bag<int>` where `class Bag<T>: List<T>`).
- **Services plugin:** `services { }` moves from the web plugin to its own, `plugins/Piper.Plugins.Services`, which
  brings in only the `Microsoft.Extensions.DependencyInjection` package, so dependency injection works in any program
  (see `samples/services`). The web plugin keeps `endpoints { }`; a web program lists both plugins.
- **Fix:** a .NET generic given a Piper trait or class as its type argument didn't take what implements or derives
  from it: `services.AddSingleton<Clock>(SystemClock())` and `List<Shape>().Add(Circle(1.0))` reported no matching
  overload. They now convert as Piper converts (boxing a builtin that implements the trait).
- **.NET variance:** `List<Circle>` converts to `IEnumerable<Shape>`, `Func<Dog>` to `Func<Animal>`, and an
  `IComparer<Shape>` to an `IComparer<Circle>`, as in C#, for assignments and arguments alike.
- **Functions as any delegate type:** a function named as a value converts to any delegate type with its signature, not
  just `Func` and `Action`: `numbers.ConvertAll(Square)`, `numbers.FindAll(IsEven)`, `words.Sort(ByLength)`,
  `Thread(Work)`, with a generic method's type arguments inferred from it (`Array.ConvertAll(array, Describe)`). A static
  method of a record, enum or class can be a function value too: `numbers.ConvertAll(Point.Make)`.
- **Fix:** lambdas' parameters were renamed apart in the generated C# when several in a function shared a name (`id`,
  `id_1`, `id_2`). ASP.NET Core binds route values by name, so every `endpoints` handler after the first taking `{id}`
  answered 400 Bad Request. Lambda parameters now keep their names unless that would hide something the lambda uses.
- **Sample:** `samples/todo-api`, a to-do REST server using `services` and `endpoints`; `--serve` keeps it running on
  localhost:5080.
- **`piper run app --port 8001`:** an option `piper run` doesn't know, after the program, goes to the program (with
  everything after it), as with `dotnet run`, so the `--` before the program's arguments can be left out. That also
  helps where a shell (Windows PowerShell) drops a bare `--`.
- **.NET enum casts:** a .NET enum casts to and from numbers, and to another enum, as in C#: `(int)response.StatusCode`,
  `(DayOfWeek)3`.
- **`None` for overloaded .NET methods:** `client.PutAsync(url, None)` used to fail, since `None` alone has no type and
  the method has `string` and `Uri` overloads. `None` now fits any parameter .NET lets be null, and takes its type from
  the overload chosen.
- **Fix (editor, Windows):** VS Code names files as `file:///d%3A/…`, which the language server read as
  `D:\d:\…`. It then couldn't find a program's `piper.json`, so a program's other files, its plugins (`services`
  reported as unexpected) and its references were missing, and problems were listed against a file that doesn't
  exist.
- **Fix (editor):** code a plugin generates has its construct's span (so errors point there), and the editor treated it
  as source: `services AppServices` was coloured as a namespace, and `endpoints` as a variable with a
  `: RouteGroupBuilder` hint. Generated code is now left out of hover, colouring, references and hints, and plugin
  keywords are coloured as keywords.
- **Go to Type Definition** in the editor: from a variable, field, parameter, expression or function to the declaration
  of its type, or of the Piper types in it (`List<Point>`, `Point?` and `(Point, Circle)` lead to Point and Circle).
- **Fix:** attributes on a class itself were silently dropped; they're now applied.
- **Fix:** a class method's parameter named like a primary constructor parameter was renamed in the C# (`owner_1`),
  breaking named arguments to it.
- **Fix:** a record invariant using an operator that needs lowering (`requires (max ?? 0) >= 0`) no longer crashes the
  compiler.
  - New errors PIP0109 and PIP0349–PIP0354.
- **Better error:** a collection literal whose elements have no common type says so, rather than calling the element a
  match arm.
- **Conditional expressions:** `c ? a : b`, shorthand for `if c { a } else { b }`, as in C#. A `?` is the conditional
  only when a `:` follows, so `x? + 1` keeps its meaning, and it works inside interpolation holes.
- **`?>`, the optional pipe:** `x ?> F(a)` calls `F(v, a)` when `x` is `Some(v)` (or `Ok(v)`), and passes `None` (or
  the `Err`) along otherwise, flattening steps that return an optional (or Result).
- **`ToString` on records and enums:** `extend Money { override fn ToString() => string = "£{this.amount}"; }` chooses
  how a record or enum prints.
- **.NET implicit conversions** apply as in C#: `BigInteger.Pow(2, 70)`, `let big: BigInteger = 5;`,
  `XElement("root", …)`.
- **`IEnumerable<T>`:** a record, enum or class implements it with one iterator method,
  `fn GetEnumerator() => IEnumerator<T> { … yield return x; … }`, and then works with `for`, LINQ and .NET APIs that
  take sequences. Piper supplies the non-generic `GetEnumerator`.
- **Comparison operators:** a type implementing `IComparable<T>` for itself gets `<`, `<=`, `>` and `>=`, as C#
  operators calling its `CompareTo`. Comparing types without it suggests implementing it.
- **Local functions:** `fn` inside a block declares a function usable throughout it (recursively, and before its
  declaration), which captures the variables around it, as C#'s local functions do. It can be generic, `async` or an
  iterator.
- **Remote actors plugin:** `plugins/Piper.Plugins.Actors.Remote` reads the same `actor` syntax on Proto.Actor, so actors
  can be used from other processes and machines: `Account.Spawn(system, "ada", …)` hosts one,
  `Account.Connect(system, "host:port", "ada")` reaches it, messages travel as JSON over gRPC, and faults and timeouts
  reach the caller. See `samples/remote-actors`.
- **`Main(args: string[])`:** `Main` can take the command-line arguments, as in C#; `piper run app -- one two` passes
  them.
- **Interop fixes:**
  - An interface's inherited properties can be read (`IList<T>.Count`).
  - An extension method is used when no instance method of the same name applies, as in C# (Proto.Actor's
    `RequestAsync` with a timeout).
  - A block ending in `throw` can be an argument or an initializer (`F({ throw E() })`), as `F(x ?? throw e)` can in C#.
- **Running programs:**
  - `piper run` names the program as `piper build` would, so libraries that send its types between processes agree with
    a built copy.
  - Package restores work for program folders given as relative paths.
- **Type tests in patterns:** a record or variant pattern can match a value typed `object`, a trait or an interface
  that the type implements, testing its type as C#'s `o is Point(var x, var y)` does: `match message { Add(n) => …, _ =>
  … }`.
- **Actors plugin:** `plugins/Piper.Plugins.Actors` adds `actor Name(params) { mut state = …; on Tell(x: int) { … }
  on Ask() => int { … } }`. Each actor has private state and handles one message at a time, on .NET channels: tells
  return at once, asks await the reply, and `Name.Spawn(…)` / `await using` start and stop it. See `samples/actors`.
- **Plugin analysis:** a plugin can implement `Analyze`, which sees the type-checked program and the code its keywords
  became, and reports errors or warnings (PIP0505, PIP0506). The actors plugin uses it to warn about messages
  carrying something mutable. Templates can splice parameter lists, and `PluginParser` adds `ParseAsyncBlock` and
  `ParseVariableDeclaration`.
- **Web plugin:** `plugins/Piper.Plugins.Web` adds two declarations, `services Name { singleton Clock = …; scoped
  Greeter => FriendlyGreeter; }` for dependency injection and `endpoints Name "/api" { get "/hello/{name}" (name:
  string) => …; }` for minimal APIs. It checks for duplicate services and routes, route parameters without a handler
  parameter, and untyped handler parameters, and brings in ASP.NET Core itself. See `samples/webapp`.
- **Lambdas as `Delegate`:** a lambda whose parameters have types can be passed where .NET takes any `Delegate`, as
  ASP.NET Core's `MapGet` does.
- **Compiler plugins:** .NET assemblies listed in `piper.json`'s `"plugins"` add statement and declaration keywords,
  which they rewrite into ordinary Piper as it's parsed. Everything after parsing works as for code the user wrote,
  including type checking, error locations and the language server. The new diagnostics are PIP0500–PIP0504. See
  [docs/plugins.md](docs/plugins.md), the sample plugin in `plugins/Piper.Plugins.Parallel` (`parallel for`,
  `concurrency { }`, which is awaited in async code) and `samples/plugins`. `SyntaxWalker` moved from the language
  server to `Piper.Syntax`, so plugins can use it.

## 1.0.0

The first release. From here on, a program that compiles with one 1.x release compiles with every later 1.x release
and means the same thing (see [Compatibility](README.md#compatibility)).

### The language

- **Records and enums:** immutable records with value equality and `{ ...p, y: 5 }` copies; enums as sum types whose
  variants carry fields.
- **Pattern matching:** exhaustive `match` with guards, nested patterns, literals, ranges (`1..9`, `0..<n`),
  comparisons (`< 0`), list patterns (`[first, ...rest]`), property patterns (`Point { x: 0 }`, `{ Length: > 5 }`),
  `|` alternatives that bind, and `is` tests. Missing cases are errors that name one; unreachable arms are warnings.
- **No null references:** `T?` is `Option<T>`, with `?.`, `??`, `?` and `null` as its None. .NET's nullable APIs are
  read as optionals.
- **Errors as values:** `Result<T, E>`, with `?` to propagate errors.
- **Everything is an expression:** blocks, `if` and `match` have values.
- **Traits, generics and extensions:** traits with default, static and generic methods and inheritance; generic records,
  enums, functions and methods with trait bounds; `extend` to add methods and traits to any type, including `int`,
  `string` and .NET types. Records and enums can declare arithmetic operators.
- **C#'s types and rules:** C#'s numeric types, conversions, overflow, casts, printing and equality.
- **.NET interop:** call any .NET API, implement .NET interfaces, use `out` and `ref` parameters, events, properties,
  indexers, operators (`date > other`, `end - start`), LINQ and .NET attributes (`[JsonPropertyName("id")]`, `[Obsolete]`).
- **Collections and lambdas:** collection literals with spreads (`[0, ...xs]`), arrays, tuples and deconstruction, lambdas
  and function types, the pipe operator `|>`.
- **Control flow:** `for x in xs`, `for i in 1..10`, `while`, `try`/`catch`/`finally`, `throw`, `using`, `defer`.
- **Async and iterators:** `async`/`await`, `await for`, `await using`, and `yield` iterators.
- **Programs:** namespaces, `using`, `private`, and programs of many files.
- **Preconditions and invariants:** `requires` clauses on functions and records, checked on every call and construction,
  and at compile time when the arguments are constants.
- **Bindings and calls:** `let … else` to unwrap or leave; named arguments and default parameter values, for Piper
  and .NET calls.
- **Strings:** C#'s alignment and format specifiers in interpolation (`{price:C}`, `{name,-10}`).
- **Doc comments:** `///` documentation, shown in the editor and written as C# XML documentation.
- **Tests:** `test "name" { … }` blocks with `Assert` and `AssertEqual`.
- **Conventions:** functions and methods are PascalCase (`Print`, `Main`), variables and fields camelCase, as in C#.
  `let` binds immutably and `mut` mutably. Some words are reserved for future versions, such as `const`, `type` and
  `class`.

### Tools

- **The `piper` tool** (`dotnet tool install -g Piper`):
  - `piper run` transpiles, compiles and runs a program;
  - `piper build` writes the C#, or with `--exe` or `--library` compiles an assembly with a PDB;
  - `piper test` runs a program's tests;
  - `piper emit` prints the C#;
  - `piper lsp` runs the language server.
- **`piper.json`** in a program's folder can set its assembly `name` and `output` (`exe` or `library`).
- **The Piper.Build package** compiles a .NET project's `.piper` files with its C#.
- **Source mapping:** errors, stack traces and the debugger point at `.piper` lines.
- **The language server** gives any LSP editor:
  - diagnostics as you type;
  - hover and go to definition;
  - find references and rename;
  - completion and signature help;
  - inlay hints for inferred types;
  - semantic highlighting;
  - a quick fix that adds a match's missing arms;
  - an outline.
- **The VS Code extension:**
  - syntax highlighting and the language server's features;
  - running a program, and its tests, from the editor;
  - debugging with breakpoints in `.piper` files, through the C# extension's debugger.
