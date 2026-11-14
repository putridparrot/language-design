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
- **Spans:** `Span<T>` and `ReadOnlySpan<T>` (and ref structs generally), so APIs such as `AsSpan` and span overloads can be called. They can't be captured by lambdas or live across `await`, as in C#.
- **Namespace-qualified variants in patterns** (`Library.Loans.LoanError.UnknownBook(code)`).
- **Explicit attribute targets** (`[return: …]`), and `[Obsolete]` warnings for using an obsolete enum variant.
- **More contracts:** `requires` on trait methods (implementations must not demand more than the trait does), invariants on
  enum variants, and postconditions (`ensures result >= 0`).
- **Obsolete warnings for setting obsolete .NET properties and subscribing to obsolete events** (reading and calling are
  already reported).
- **.NET's `T?` through a Piper type parameter** (found by apps/tracker-desktop): in `fn Read<T>(…) => T? =
  JsonSerializer.Deserialize<T>(…)`, .NET may give null for any reference `T`, but Piper reads the result as plain `T`,
  as it can't tell whether `T` will be a reference type. That lets a null in. The result should be `T?`, converted by an
  unconstrained helper (`value is null ? None : Some(value)`), for results, properties, `out` parameters and tasks alike.
  The `PiperInterop` helpers are constrained to `class` today, and `ToNullableReference` has no unconstrained
  equivalent for passing a `T?` the other way.
- **Async event handlers** (found by apps/tracker-desktop): `button.Click += async (_, e) => { await Save(); }` is an
  error, as an async lambda must return a Task. C# allows `async void` there, losing the exceptions it throws to the
  synchronization context. To decide: allow it for event handlers only, as C# guidance suggests, perhaps with exceptions
  reported rather than lost.
- **Found by apps/auction:**
  - ~~**Helper methods in a `statemachine`** (and an `actor`)~~, done: a machine can declare `fn`s, its class's methods.
    And in an `actor`, as local functions where its loop starts, beside its state.
  - ~~**A list literal's `None`s typed by a later `Some`**~~, done: elements with no type of their own take the others'.
  - ~~**`[NotNullIfNotNull]` results**~~, done: given a plain value, the result is one.
- **Newtypes, later:** a newtype with type parameters, for phantom-typed ids (`newtype Id<T> = Guid;`, so an
  `Id<Issue>` isn't an `Id<Project>`); and newtypes as JSON dictionary keys (`Dictionary<IssueId, …>` written as an
  object, which needs the converter's property-name methods).

### Ideas from other languages

Weighed against what the apps needed (see each); in rough order of value for the cost.

- **Property-based tests** (QuickCheck, Hypothesis, FsCheck): `test "name" for (a: int in 1..500, …) { … }` runs the
  body over generated values, and shrinks a failure to the smallest input that fails. Several app tests check a
  property with one hand-picked example (apps/grid's "the chunks cover the range" with 1000 and 7; "any order gives the
  same figures" with one order). Piper's tests are built in, so this fits there: generators for the builtin types,
  records and enums, with a seed printed on failure to repeat it.
- **Structured concurrency** (Swift's `async let` and task groups, Kotlin's coroutine scopes): tasks started in a scope
  are awaited or cancelled when it ends, and their exceptions aren't lost. apps/grid and apps/stock thread
  cancellation tokens through every worker loop, pair `StartCluster` with `StopCluster` in each test, and stop with a
  hand-written "cancel, then WhenAll"; the aspects plugin had to stop `[Command]` losing an async exception.
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

## Production web and services

The `endpoints` and `services` plugins are thin layers over ASP.NET Core and Microsoft.Extensions.DependencyInjection.
To be production-ready they need Piper's own types to work at the boundary, and to use what the compiler knows (types,
contracts, the dependency graph) to catch at compile time what ASP.NET Core and DI only find when the program runs.

### Web, in order

Piper's types on the wire are done: Options, enums and invariants in JSON, optional handler parameters, and Options and
Results answered as 200, 404 or ProblemDetails (see [JSON](language.md#json) and [plugins](plugins.md)). What's left:

1. **Testing.** A test helper serving an app in memory (ASP.NET Core's `TestServer`), so `test` blocks call endpoints
   without a port: `let client = TestClient(App);`.
2. **Endpoint metadata.** Authorization, anonymous access, rate limiting, CORS and output caching are done (`authorize`,
   `anonymous`, `ratelimit`, `cors` and `cache` on groups and endpoints, defined by a `policies` declaration). Still to
   come: OpenAPI tags, and OpenAPI response types (`.Produces<T>(200)`, `.Produces(404)`) inferred from each handler's
   type-checked result, which C# writes by hand and lets drift. Also per-user rate limits, partitioned by the signed-in
   user or the client's address rather than shared by everyone.
3. **Hosting and operations.** A `server` declaration replacing the builder code in `Main`: services and endpoints,
   configuration bound to records (`appsettings.json`, environment), health checks, ProblemDetails error handling,
   HTTPS and graceful shutdown. Publishing a self-contained build or a container image (`piper build --publish`).

Server-side HTML belongs with [UI](#ui) (htmx over plain endpoints), not Razor.

### Services

- **Checking the dependency graph at compile time.** The plugin sees each implementation's constructor, so it can
  report what DI only finds at run time (and `ValidateOnBuild` only partly): a dependency nothing registers, a singleton
  depending on a scoped service (a captive dependency), and a cycle.
- **Several implementations of a service,** resolved as `IEnumerable<T>`, which the duplicate check now forbids; an
  explicit form (`many Handler => AuditHandler;`) keeps that check for mistakes.
- **Keyed services** (`singleton Cache["fast"] => MemoryCache;`, injected with `[FromKeyedServices("fast")]`) and
  **open generics** (`scoped Repository<> => SqlRepository<>;`).
- **Configuration and options:** typed configuration is done (the `config` plugin). Still to come: registering a config
  with DI (`config Smtp;` in `services`, as `IOptions<Smtp>` too), and reloading it when appsettings.json changes.
- **Databases:** the `data` plugin covers EF Core models and queries. Still to come: registering a database with DI
  (`AddDbContext`, its connection string from a `config`), migrations from `piper` (`piper db migrate`), relationships
  between tables (navigation properties), and comparing an optional column with an optional variable in a query.
- **Background work:** `hosted Worker;` (`AddHostedService`), and a Piper-friendly worker: a class with an
  `async fn Run(stopping: CancellationToken)`.
- **Composition and testing:** a `services` block including another (a library's), and replacing a registration in a
  test (`services Test: AppServices { singleton Clock = FixedClock("noon"); }`).
- **Hosts beyond the web:** `Host.CreateApplicationBuilder` for console programs and workers, so they get configuration,
  logging and lifetime as a web app does, rather than a bare `ServiceCollection`.

## Cloud and distributed

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
2. **Tracing through actors** (OpenTelemetry). The stock and grid apps needed hand-built dashboards and `servedBy`
   fields to see which node did what. The plugin generates each remote message's envelope, so it can carry the trace
   context: one request becomes one trace through HTTP, node A and the product actor on node B, with handler times and
   mailbox depth as metrics. Locally, .NET Aspire's dashboard shows them with no setup.
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
   stored through Proto.Persistence or the data plugin. With clustering, it would replace the stock app's fixed
   `--nodes` list, `hash % count` and StatefulSet. The largest of these.
4. **Failure policies.** The same patterns were written by hand several times: stock retries once after a dead letter,
   grid lends chunks with a lease and ignores late results, the tracker retries its webhook (with `[Retry]`). The
   aspects plugin could add `[Timeout]` and `[CircuitBreaker]` beside `[Retry]`, and asks to remote actors take the
   same policies.
5. **An outbox, and queues.** The tracker tells its webhook after saving a change; if the process dies in between, the
   notification is lost. An outbox writes the message in the change's own transaction and sends it afterwards, so it
   can't be lost, which suits the data plugin. A queue plugin would give jobs that must survive a restart somewhere to
   live, as the grid's README suggests for long ones: see [the messaging plugin](#a-messaging-plugin) below.
6. **Testing distributed programs.** Both apps wrote their own `StartCluster` and `StopCluster`, and the grid's
   "vanished worker" test was put together by hand. A test helper could give a cluster in one process, with faults to
   inject: stop a node, drop or delay messages to one, split the network. Property-based tests (above) suit it, with
   the faults among the generated inputs.
7. **Deploying without hand-written files.** Each app has a Dockerfile copying the repository to build the compiler,
   and Kubernetes YAML with the same `POD_IP` plumbing. `piper build --publish` (planned above) could use the .NET SDK's
   container publishing, so there's no Dockerfile; and a declaration of the app's roles (a coordinator and workers,
   their ports and replicas) could generate the Kubernetes or Aspire manifests.

More, around the services themselves:

- **Typed clients generated from `endpoints`.** [apps/tracker-desktop](../apps/tracker-desktop/README.md)'s
  `TrackerClient` was written by hand, with its own copies of the API's records; when the tracker's ids became
  newtypes, the copies didn't follow, and only stayed working because the JSON didn't change. Piper knows every route,
  parameter and result type, so it can generate the client, sharing the record types, and a change to the API breaks
  its clients at compile time rather than in production. Between services too, as OpenAPI does, but checked. The most
  valuable of these.
- **Ids made anywhere** (`newtype OrderId = Guid`, as UUIDv7). The tracker's ids are numbered by the database, so an
  issue has none until it's saved; with messaging and an outbox, an event needs one first. Time-ordered GUIDs
  (`Guid.CreateVersion7()`, in .NET) index well and are made on any node. With newtypes this is small: a standard way
  to declare such an id and make a new one, which the data plugin stores.
- **Secrets that can't leak.** A `Secret<T>`, or a `secret` field of a `config` record (the tracker's webhook URL, a
  connection string), prints and serializes as `***`; the compiler stops it being interpolated into a string or a
  log, and `.reveal()` is the one way to its value. It could come from Azure Key Vault through the config plugin.
- **Structured logging from interpolation.** The apps logged with `Console.Error.WriteLine($"…")`.
  `log.Info($"Reserved {quantity} of {sku}")` could compile to a logging template with named fields, as C#'s
  `LoggerMessage` generator does, so logs are queried by field (in Application Insights, Seq) while staying natural
  Piper. It goes with tracing (item 2).
- **Serverless hosts, and Native AOT.** A `consumer` or an endpoint could also run as an Azure Function (a queue or
  HTTP trigger), the code unchanged and only the host different. That wants Native AOT and trimming, for fast starts
  when scaling from zero, and Piper's runtime uses reflection in places (enum JSON, the data plugin's converters,
  newtypes' delegates). Worth checking Piper programs against AOT early, before more reflection goes in, with
  source-generated JSON where it's needed.
- **Idempotent endpoints.** A client retrying a POST after a timeout can add an issue twice.
  `post "/issues" idempotent …` keeps each request's `Idempotency-Key` header and its response, and answers a repeat
  with the same response. Message handlers get the same through the outbox's de-duplication.

### A messaging plugin

Service buses and message queues (Azure Service Bus first, then RabbitMQ) as declarations: what messages there are,
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

**In order:** the runtime with the in-memory and Service Bus transports, `messages` and `consumer` with `Handled`, and
the compile-time checks; then the outbox and topology; then RabbitMQ; then versions and sagas.

**To decide:** whether Piper services must interoperate with existing C# services on the same bus. Their envelope
(headers, type names, correlation) is a protocol; if they use MassTransit's or NServiceBus's, the transports would need
an option to read and write it.

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
     offers tags, attributes and an input's fields in markup. Still to come: completion of the closing tag as `>` is
     typed, and hover on HTML's elements and attributes.
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
have no newtype for money. The plan, as written, still lists what's left for it (caching, pods, persistence,
property-based tests, messaging). As the tracker proved the API side, people list items, others browse and search,
then bid live as the clock runs down. Each part of it exercises one of the above:

- **Listing pages and search:** server-rendered, cached (the web plugin's `cache`), with search as you type through
  htmx.
- **Listing an item:** a form bound to `NewLot`, whose `requires` check the starting price, the reserve and the end.
- **A lot's life:** a `statemachine`, Scheduled → Open → Closing (extended by a last-minute bid) → Sold or Unsold,
  which each view `match`es on.
- **Bidding:** each lot an actor, taking bids one at a time, so two people can't both win at a price: the stock app's
  guarantee, in a UI people watch. Lots spread over pods with the remote actors plugin.
- **The lot page, live:** the current price, a countdown, "you've been outbid" and the bids, pushed to everyone
  watching.
- **Types and tests:** newtypes (`LotId`, `BidderId`, a `Money` requiring more than 0), and, once they exist,
  [property-based tests](#ideas-from-other-languages) that the highest bid wins and bids never fall.
- **Later:** telling the winner and the seller through the [messaging plugin](#a-messaging-plugin) and an outbox, and
  the bids kept by persisted actors, so a restart loses nothing.

**In order:** typed HTML with routes and forms (with which the auction's pages and forms work, server-rendered); htmx
support; `piper watch`; live pages, proved by the auction's bidding. Like the tracker, the app records each gap it
finds, and what was done.

## Tools

- **`piper fmt`:** a formatter.
- **Language server:**
  - `piper.json` errors while typing, not just on save;
  - a quick fix to add a missing `using`;
  - signature help inside lambdas.
