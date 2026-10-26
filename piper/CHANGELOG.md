# Changelog

## Unreleased

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
  - **Properties:** `prop Balance: decimal => balance;`, `prop Count: int { get; private set; } = 0;`,
    `prop Name: string { get => name; set { name = value.Trim(); } }`. Read and set like fields; public unless marked
    `private`, with an optional `private set`. They can be static, `virtual`, `abstract` and `override`, override .NET
    properties, and implement .NET interfaces' properties (`IReadOnlyCollection<T>.Count`), so a class can now derive
    from a .NET base with abstract properties, such as `Stream`. New errors PIP0355 and PIP0356.
  - **Indexers:** `prop this[x: int, y: int]: int { get => …; set { … } }`, used as `grid[1, 2] = 5`. They can be
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
- **Properties in traits and `extend` blocks:** read-only, `trait Shape { prop Area: double { get; } prop Label:
  string => "shape"; }`, implemented in `extend` blocks or by a class's properties, and read through a bound like
  methods. `extend int { prop IsEven: bool => this % 2 == 0; }` adds a C# 14 extension property.
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
