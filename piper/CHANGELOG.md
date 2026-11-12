# Changelog

## Unreleased

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
