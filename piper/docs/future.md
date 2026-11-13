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
- **Web UI:** server-rendered HTML through the web plugin, with htmx for interactivity, and perhaps an HTML templating
  plugin. Blazor is a poor fit, as Razor components need the Razor compiler.
- **Terminal UI:** Spectre.Console or Terminal.Gui as packages should already work; a sample would show it.

## Tools

- **`piper fmt`:** a formatter.
- **Language server:**
  - `piper.json` errors while typing, not just on save;
  - a quick fix to add a missing `using`;
  - signature help inside lambdas.
