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
- **Attributes on enum variants,** and explicit targets (`[return: …]`).
- **More contracts:** `requires` on trait methods (implementations must not demand more than the trait does), invariants on
  enum variants, and postconditions (`ensures result >= 0`).
- **Obsolete warnings for setting obsolete .NET properties and subscribing to obsolete events** (reading and calling are
  already reported).

## Production web and services

The `endpoints` and `services` plugins are thin layers over ASP.NET Core and Microsoft.Extensions.DependencyInjection.
To be production-ready they need Piper's own types to work at the boundary, and to use what the compiler knows (types,
contracts, the dependency graph) to catch at compile time what ASP.NET Core and DI only find when the program runs.

### Web, in order

Piper's types on the wire are done: Options, enums and invariants in JSON, and optional handler parameters (see
[JSON](language.md#json)). What's left:

1. **Option and Result as responses.** A handler returning `T?` answers 200 or 404; one returning `Result<T, E>`
   answers 200 or an error, the error enum choosing the status (`[Status(409)]` on a variant, or a default), as
   ProblemDetails. The usual `match … { Some(x) => Results.Ok(x), None => Results.NotFound() }` goes away.
2. **Testing.** A test helper serving an app in memory (ASP.NET Core's `TestServer`), so `test` blocks call endpoints
   without a port: `let client = TestClient(App);`.
3. **Endpoint metadata.** Authorization, CORS, rate limiting, output caching and OpenAPI tags, on a group or an endpoint
   (`endpoints Admin "/admin" authorize "admin" { … }`). OpenAPI response types (`.Produces<T>(200)`, `.Produces(404)`)
   inferred from each handler's type-checked result, which C# writes by hand and lets drift.
4. **Hosting and operations.** A `server` declaration replacing the builder code in `Main`: services and endpoints,
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
- **Configuration and options:** `options Smtp from "Smtp";` binding a configuration section to a Piper record
  (`IOptions<Smtp>`), with Options for missing values and the record's `requires` validated at startup.
- **Background work:** `hosted Worker;` (`AddHostedService`), and a Piper-friendly worker: a class with an
  `async fn Run(stopping: CancellationToken)`.
- **Composition and testing:** a `services` block including another (a library's), and replacing a registration in a
  test (`services Test: AppServices { singleton Clock = FixedClock("noon"); }`).
- **Hosts beyond the web:** `Host.CreateApplicationBuilder` for console programs and workers, so they get configuration,
  logging and lifetime as a web app does, rather than a bare `ServiceCollection`.

## UI

Piper programs can be consoles and servers; a UI should come from .NET's UI frameworks, built in code rather than XAML
(nested object and collection initializers make a control tree read naturally).

- **Desktop through interop (first):** a small code-first sample using Avalonia (cross-platform, from NuGet) and one
  using WPF (`"frameworks": ["Microsoft.WindowsDesktop.App"]`), to find what's missing. Two gaps are known:
  - `[STAThread]` on `Main` doesn't reach the generated entry point (`PiperEntryPoint.Main`), which WPF and WinForms need.
  - No piper.json setting for a windowed program (MSBuild's `WinExe`), so a console window opens alongside it.
  XAML is out of scope: it relies on generated `InitializeComponent` partials the binder can't see.
- **An MVU plugin:** Model-View-Update (as Elm and F#'s Fabulous) suits Piper: a record for the model, updated by
  spreading; an enum of messages, handled by an exhaustive `match`; and a view function returning a control tree. The
  plugin would supply the runtime (diffing the view, dispatching messages) over Avalonia, avoiding
  `INotifyPropertyChanged`, which C# usually gets from source generators Piper can't run.
- **Web UI:** server-rendered HTML through the web plugin, with htmx for interactivity, and perhaps an HTML templating
  plugin. Blazor is a poor fit, as Razor components need the Razor compiler.
- **Terminal UI:** Spectre.Console or Terminal.Gui as packages should already work; a sample would show it.

## Tools

- **`piper fmt`:** a formatter.
- **Language server:**
  - `piper.json` errors while typing, not just on save;
  - a quick fix to add a missing `using`;
  - signature help inside lambdas.
