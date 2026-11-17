# Extending Piper without changing the compiler

Piper's core is meant to stay small. Features such as concurrency blocks, parallel loops, dependency-injection wiring
and web endpoints shouldn't each need a compiler change. There are two ways to add them, and neither touches the
compiler:

| | **Trailing closures** (a library) | **Plugins** (a compiler add-on) |
|---|---|---|
| What you write | an ordinary Piper or .NET function | a .NET assembly implementing `IPiperPlugin` |
| What it looks like | `Retry(3) { … }`, `Guarded { … }` | `parallel for x in items { … }`, `services App { singleton Clock => SystemClock; }` |
| Syntax | a call with a block after it | any syntax after a keyword the plugin adds |
| Use it when | the feature is "run this code, somehow" | the feature has its own shape: declarations, clauses, words of its own |

Start with a library function and a trailing closure. Reach for a plugin when the construct can't be written as a
call.

## Trailing closures

A block after a call is the call's last argument, as a lambda. This is the same as in Kotlin and Swift:

```piper
Repeat(3) { i =>              // Repeat(3, i => { … })
    Print($"tick {i}");
}

Guarded {                     // Guarded(() => { … }): no parentheses when the closure is the only argument
    RiskyWork();
}

let total = Measure("sum") {  // the block's last expression is the closure's value
    numbers.Sum()
};

names.Sort() { (a: string, b: string) => a.Length - b.Length }
```

- The block can start with parameters and `=>`, as `x =>` or `(a: int, b) =>`. Without them, the closure has no
  parameters.
- A statement that ends with a trailing closure needs no `;`, just as with `if`, `match` and `{ … }`.
- In the head of an `if`, `while`, `for`, `match` or `requires`, a `{` starts the body, so `if ready { … }` means what
  it always has. A closure inside brackets is fine there: `if all(items.Map { x => x.Ok }) { … }`.
- After a trailing closure, `.`, `?.` and `?` continue the expression (`Run { … }.Wait()`). A `(`, `[` or `{` on the
  next line starts a new statement.
- A closure that awaits is async, so `await Retry(3) { await Download(url) }` passes an `async () => { … }`. Awaits in
  a lambda inside it don't count, and neither does being in an `async fn`: a closure that doesn't await stays
  synchronous, so it can still be passed where an `Action` is wanted.

It's purely syntax: the parser turns it into an ordinary call with a lambda, so typing, overloads and the generated C#
are exactly those of the call written out.

## Plugins

A plugin adds keywords. When the parser meets one at the start of a statement (or of a top-level declaration), it
hands the rest of the construct to the plugin. The plugin reads it with Piper's own parser and returns **ordinary Piper
syntax**, which then goes through the binder, lowering and C# emitter like anything the user wrote. As a result:

- **The type checker checks everything.** A plugin can't produce code Piper wouldn't accept.
- **Errors point at the user's code.** User syntax keeps its source positions inside the rewritten code, and code the
  plugin generated is reported at the construct.
- **The language server works unchanged.** Hover, go-to-definition and completion inside a plugin's blocks work, because
  those blocks are plain Piper.
- **Plugins don't emit C#.** That keeps the guarantees above. (If a plugin ever needs code Piper can't express, the
  answer is a library function it calls.)

### Using a plugin

List the plugin in the program folder's `piper.json`. The plugins in this repository's [plugins](../plugins) folder
come with piper (in a `plugins` folder beside it), and are named: `actors`, `api`, `args`, `aspects`, `config`, `data`, `hosting`, `openapi`, `remote-actors`, `parallel`,
`services`, `state-machines` and `web`. Any other is given as the path of its assembly, relative to the folder:

```json
{
  "plugins": ["parallel", "../my-plugin/bin/My.Plugin.dll"]
}
```

`piper run`, `piper test`, `piper build` and `piper emit` on that folder load them, and so does the language server
for files in the folder. `piper new <folder> --template web` makes a program using `services` and `web`.
[samples/plugins](../samples/plugins) is a runnable example, using
[plugins/Piper.Plugins.Parallel](../plugins/Piper.Plugins.Parallel):

```piper
parallel for n in [1, 2, 3, 4, 5] {     // Parallel.ForEach([1, 2, 3, 4, 5], n => { … });
    Interlocked.Add(ref total, Square(n));
}

concurrency {                            // Task.WaitAll(Task.Run(() => { … }), Task.Run(() => { … }));
    first = Square(6);
    second = Square(7);
}
```

`piper emit` shows the C# the rewritten code becomes.

#### Adding a plugin by name

`piper plugin add` adds a plugin that a piper.json can then name, as it does those that come with piper:

```text
piper plugin add plugins/Piper.Plugins.MyPlugin          # a plugin project's folder (its bin/plugin), a folder, or a .dll
piper plugin add ../my-plugin --name my-plugin           # the name is the plugin's own unless --name gives another
piper plugin list                                        # those that come with piper, and those added
piper plugin remove my-plugin
```

- **Where it goes:** the plugin's folder is copied to `~/.piper/plugins/<name>` (`%USERPROFILE%\.piper\plugins` on
  Windows), or to the folder that `PIPER_PLUGINS` names.
- **Lookup:** names are looked up among the plugins that come with piper first, then those added. So an added
  plugin can't take a bundled plugin's name.
- **Finding the assembly:** a plugin's folder should hold one `Piper.Plugins.*.dll`, or only one .dll.
- **No reinstall:** a new plugin doesn't need a new copy of piper.
- **In the editor:** the language server is told when the plugins folder changes, so a plugin added while the editor
  is open is used straight away, keywords and highlighting included. This works in any editor whose client can watch
  files outside the workspace, as VS Code's can.

A plugin given by its path needs none of this. It's reloaded whenever its assembly is rebuilt, and it's read into
memory, so the editor never locks it.

#### Folders you haven't trusted

Loading a plugin runs its code. So in a folder the editor hasn't been told to trust, the language server leaves out
everything in its piper.json that could run code from the folder:

- **Plugins given by path.**
- **Packages:** restoring them runs MSBuild, which imports the folder's `Directory.Build` files.
- **Assemblies:** reading them can run their code.

The plugins that come with piper, and those added with `piper plugin add`, still load. What was left out is a warning
on piper.json (PIP0404), and code using it shows errors until the folder is trusted.

In VS Code, this is Workspace Trust: trusting the folder loads the rest at once. In an untrusted workspace, VS Code
also ignores the workspace's own `piper.server.path` and `piper.server.args`. The command line always trusts the
folder it's given.

A plugin keyword is a keyword only where its construct can start, and only when the token after it couldn't follow a
name. So the word is still an ordinary name elsewhere: `let parallel = 4;`, `parallel(items)` and `parallel.Count`
don't involve the plugin. One consequence is that a plugin's syntax can't continue its keyword with an operator, `(`,
`[`, `.` or `=`.

### Writing a plugin

A plugin is a class library that references `Piper.Compiler` (without copying it, since the compiler provides it):

```xml
<PropertyGroup>
  <EnableDynamicLoading>true</EnableDynamicLoading>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\..\src\Piper.Compiler\Piper.Compiler.csproj" Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

Every public `IPiperPlugin` type in it with a parameterless constructor is loaded:

```csharp
public sealed class ParallelPlugin : IPiperPlugin
{
    public string Name => "parallel";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new StatementKeyword("parallel", ParallelFor),
    ];

    // parallelFor = "parallel" "for" IDENT "in" expression block ;
    private static IEnumerable<StatementSyntax> ParallelFor(PluginParser p)
    {
        p.Expect(TokenKind.ForKeyword);
        var variable = p.Expect(TokenKind.Identifier);
        p.Expect(TokenKind.InKeyword);
        var items = p.ParseExpressionBeforeBlock();
        var body = p.ParseBlock();
        return [p.Template.Statement("System.Threading.Tasks.Parallel.ForEach($items, $x => $body);",
            ("items", items), ("x", variable), ("body", body))];
    }
}
```

- **Keywords:** a `StatementKeyword` starts a statement inside a block and returns statements. A `DeclarationKeyword`
  starts a top-level declaration and returns declarations.
- **Reading:** `PluginParser` gives the tokens after the keyword (`Current`, `Peek`, `NextToken` and `Expect`).
  - **Words special to the construct**, such as `singleton`: `AtWord` checks for one, and `ExpectWord` or `ReadWord`
    reads it.
  - **Highlighting:** words read with `ExpectWord` or `ReadWord` are coloured as keywords in the editor, as the
    plugin's own keyword is. Read them with these, not `NextToken`. It also gives Piper's parser for the
  pieces: `ParseExpression`, `ParseExpressionBeforeBlock` (where a `{` follows), `ParseBlock`, `ParseAsyncBlock`
  (for a block the rewritten code runs in async code, so it can `await`), `ParseType`, `ParsePattern`,
  `ParseLambdaParameters`, `ParseVariableDeclaration` and `ParseDeclaration`. Like Piper's parser, these never throw:
  a missing token is reported and synthesized, so the plugin can carry on.
- **Errors:** `p.Report(span, message)` reports an error in the construct (PIP0503), followed by the plugin's name.
- **Writing the result:** `p.Template` builds syntax from Piper source with `$name` holes:
  - **What a hole can hold:** an expression or block, a type, a token, or a string (as a name). Where a list goes (call
    arguments, collection elements, a block's statements), a hole can hold a sequence, which is spliced in.
  - **Parameter lists:** write `fn $name($parameters: unit)`, with a sequence of `ParameterSyntax`. (A parameter
    needs a type to parse; the `unit` is ignored.)
  - **Holes in place of a block:** where Piper needs a block, write `{ $body }`. With a block as its value, that's the
    block itself.
  - **Source positions:** the template's own syntax is given the construct's position, and the holes' syntax keeps its
    own.
  - **Plugin bugs:** a template that doesn't parse, or a hole that isn't filled, throws.
- **Code Piper can't express:** put it in a .NET assembly shipped beside the plugin, list it in the plugin's
  `References` (paths relative to the plugin's assembly), and call it from the rewritten code. A program using the
  plugin references it, and a built one gets a copy. The web plugin's Piper.Web is one: an ASP.NET Core endpoint
  filter.
- **Providers it brings:** a plugin whose assembly implements a service (a cloud's store, a broker's transport) lists
  in `Providers` the static methods registering them with Piper.Runtime's `PiperProviders`
  (`["Piper.Data.DatabaseEventStore.RegisterProviders"]`). A program using the plugin calls them as it starts, so
  bringing the plugin is all it takes for configuration to choose its providers by name (see
  [Providers](#providers-chosen-by-configuration)).
- **Fresh names:** `p.FreshName("tasks")` gives a name like `__tasks_1` for a variable the rewritten code needs. It
  won't clash with the user's names or with another expansion's.
- **Async:** `p.AsyncContext` says whether the rewritten code can `await`:
  - `Async` in an `async fn` or `async` lambda.
  - `Sync` in other functions and lambdas.
  - `IfAwaited` in a trailing closure. The closure becomes async if its final code awaits, but the function it's
    passed to may want a synchronous one. So there, await only when the user's code in the construct does.
  - To check that, `SyntaxWalker.Awaits(node)` tells whether syntax awaits, ignoring nested lambdas.
- **Looking into syntax:** `SyntaxWalker.DescendantsAndSelf` and `ChildrenOf` walk any syntax node.
- **Failures:** a plugin that throws is reported (PIP0504), and its construct is skipped, so the rest of the file is
  still checked.
- **Built against another piper:** a plugin built against an older compiler fails when it uses something that has
  changed, as a missing method or type. That error, when loading, reading or analyzing, says to rebuild the plugin.
  A plugin built against a newer compiler than the one loading it is refused when it's loaded.
- **Editor help:** a keyword's `Description`, `Snippet` and `Words` (all optional, set with `init`) are what the
  editor shows:

  ```csharp
  new StatementKeyword("parallel", ParallelFor)
  {
      Description = "`parallel for x in items { … }` runs the body for each item at once.",   // hover, in Markdown
      Snippet = "parallel for ${1:item} in ${2:items} {\n\t$0\n}",                            // completion, in LSP snippet syntax
      Words = new Dictionary<string, string> { ["for"] = "…" },                                // hover on words read with ReadWord
  }
  ```

  Completion offers a declaration keyword at the top level, and a statement keyword inside a declaration.

#### Decorators

A `Decorator` is an attribute that a plugin handles: `[Retry(3)] fn Fetch() { … }`. The parser hands the function or
method it's on to the plugin, which returns the function rewritten. This is how a plugin adds a cross-cutting concern,
such as tracing, timing, retrying or authorization, to code that's already written.

```csharp
new Decorator("Timed", d =>
{
    var stopwatch = d.FreshName("stopwatch");
    return d.WithPrologue(d.Template.Statements("""
        let $stopwatch = System.Diagnostics.Stopwatch.StartNew();
        defer System.Console.Error.WriteLine($stopwatch.Elapsed);
        """, ("stopwatch", stopwatch)));
})
```

- **Compile time only:** a decorator is code generation, not reflection. The rewritten function is ordinary Piper that's
  type-checked as usual, and the attribute itself is removed, so it never reaches C#. At run time there are no
  proxies, no weaving and no attribute lookups, only the code the decorator wrote, which `piper emit` shows.
- **What a decorator gets:** a `DecoratorContext`:
  - The function (`Function`, `Name`, `Body`, `ReturnType`, `ReturnsUnit`, `IsAsync`, `IsIterator`, `IsMethod`).
  - The attribute and its `Arguments`, as expressions to put in the code it writes.
  - `Template` (whose syntax is placed at the attribute), `FreshName` and `Report`.
- **Rewriting:** `d.WithPrologue(statements)` runs statements before the body. With a `defer` among them, it wraps
  the body as `finally` would, so it also works in async functions and iterators. `d.WithBody(block)` replaces the
  body. Either way the function keeps its name, parameters, other attributes and `requires` clauses. Preconditions
  are still checked first, before anything a decorator adds.
- **Several on one function** apply nearest first, so the first one written is outermost:
  `[Trace] [Retry(3)] fn F()` traces one call, with its retries inside.
- **Where they go:** a decorator given a function handler goes on functions and methods with a body (of records, enums,
  classes and `extend` blocks). One given a `Type` handler goes on classes and records (see below). Anywhere else, such
  as a field, a parameter, an enum or a trait's required method, is error PIP0507, which says where it can go.
- **Names:** a decorator's name is unqualified, and is shared with keywords: a decorator and a keyword can't have the
  same name (PIP0502). An attribute that isn't a loaded plugin's decorator is an ordinary .NET attribute.
- **Analysis:** each use is an expansion, whose `KeywordToken` is the attribute's name and whose `Result` is what it
  returned. That's the very syntax that gets bound, so an analysis finds the symbols by reference.
- **Failures:** a decorator that throws is reported (PIP0504), and what it was on is left as it was.

##### Decorating classes and records

A decorator with a `Type` handler rewrites a class or record, as a C# source generator would. It can add members,
bases and declarations beside the type. One name can have both handlers: `[Trace]` on a function traces it, and on a
class traces each of its methods.

```csharp
new Decorator("Trace", Trace)                           // on functions and methods
{
    Type = d => [d.DecorateMethods(Trace)],             // on a class: each method, as if each had the attribute
}
```

- **What it gets:** a `TypeDecoratorContext`:
  - `Declaration`, and `Class` or `Record`: the type as nearer decorators left it, its methods' own decorators already
    applied.
  - `Attribute`, `Arguments`, `Template`, `FreshName` and `Report`, as for functions.
- **Writing members:** `d.ClassMembers("…", holes)` reads members written as in a class's body, returning its fields
  (properties included), methods and events. `d.WithMembers(fields, methods, events)` adds them, and `d.WithBase(type)`
  adds a trait or interface. To replace or remove members, rewrite the class with `with`.
- **What it returns:** the type, rewritten or not, and any declarations to add beside it. A record's methods go in an
  `extend` block, so a decorator adds methods to a record by returning one. Returning no version of the type is a bug
  in the plugin (PIP0504).
- **Several on one type** apply nearest first, as on functions.
- **Markers** are how a type decorator takes options per member. A `Decorator` with `ReadBy = "Notify"` has no
  handler: it stays on the class's methods, fields, properties and events for that type decorator to find with
  `TypeDecoratorContext.Marker(member.Attributes, "Command")`. It's removed once the class's type decorators have
  run. A marker on a member of a class without the decorator that reads it, or anywhere else, is error PIP0508.

  ```csharp
  new Decorator("Command") { ReadBy = "Notify" }
  ```

#### Starting a plugin with `piper plugin new`

```text
piper plugin new plugins/Piper.Plugins.Greet      # or any folder: my-plugin makes Piper.Plugins.MyPlugin, named my-plugin
cd plugins/Piper.Plugins.Greet
dotnet build                                      # builds it into bin/plugin, and adds it to piper
piper run example                                 # a program using it: "plugins": ["greet"]
```

It makes:

- **The project:** a plugin project with the settings above, and a plugin adding a `repeat 3 { … }` statement, to
  change into your own.
- **An example program:** in `example/`, which uses the plugin.
- **Its reference to the compiler:** in a clone of this repository, the compiler's project. Elsewhere, the
  `Piper.Compiler.dll` of the piper that made it.
- **A build step:** each build runs `piper plugin add` on the project, so a piper.json can name the plugin and an open
  editor picks up the change. To turn that off, set `PiperPluginName` in the project to empty.

### Analyzing the type-checked program

Rewriting happens while parsing, before anything has a type, so a plugin can't check types there. For checks that need
types, a plugin implements `Analyze`, which the compiler calls once the program is bound (and as the editor analyzes):

```csharp
public void Analyze(PluginAnalysis analysis)
{
    foreach (var expansion in analysis.Expansions)                      // each use of this plugin's keywords
    {
        var messages = expansion.Result.OfType<EnumDeclarationSyntax>().Single();
        var symbol = analysis.Program.Types.OfType<EnumSymbol>().Single(e => ReferenceEquals(e.Declaration, messages));
        // … look at the symbol's variants and their fields' types …
        analysis.ReportWarning(expansion.File, span, "…");
    }
}
```

- **What it sees:** `analysis.Program` is the bound program: its types, functions and methods, with their symbols.
  `analysis.Expansions` lists each use of the plugin's keywords, with the syntax it was rewritten into.
- **Finding the symbols:** the binder works on that very syntax, so a declaration the plugin wrote can be found by
  reference, as above.
- **Reporting:** `ReportError` (PIP0503) or `ReportWarning` (PIP0505), at any span in the user's files. Analysis only
  reports; it can't change the program.
- **Failures:** an analysis that throws is reported (PIP0506), and other plugins' analyses still run.

The actors plugin uses this to warn about messages that carry something mutable.

### Loading and errors

`PluginLoader` loads each assembly into its own load context:

- The context shares `Piper.Compiler` and the framework with the compiler, and resolves the plugin's other
  dependencies from its folder.
- It reads the assembly into memory instead of loading it from its file. So the language server never locks the
  file, and a rebuilt plugin is picked up on the next analysis.

| Code | Error |
|---|---|
| PIP0500 | A plugin can't be loaded: the file is missing, it isn't an assembly, or it has no plugin types. |
| PIP0501 | A plugin's keyword is one of Piper's keywords or reserved words, or isn't a name. |
| PIP0502 | Two plugins add the same keyword. |
| PIP0503 | An error a plugin found in its construct. |
| PIP0504 | A plugin threw while reading its construct. |
| PIP0505 | A warning a plugin's analysis found. |
| PIP0506 | A plugin threw while analyzing the program. |
| PIP0507 | A decorator is where it has no handler: on a function without a function handler, a class or record without a type handler, or anything else. |
| PIP0508 | A marker (such as `[Command]`) is on a member of a class without the decorator that reads it, or anywhere else. |

Loading errors are reported against `piper.json`, and in the editor they appear on that file.

## Features as plugins

### `parallel` and `concurrency`

Both are in [Piper.Plugins.Parallel](../plugins/Piper.Plugins.Parallel/ParallelPlugin.cs) and run in
[samples/plugins](../samples/plugins).

`concurrency` runs each statement as a task and waits for all of them. A `let` inside it is an error, since each
statement runs on its own.

```piper
concurrency {
    first = Square(6);
    second = Square(7);
}
```

becomes:

```piper
System.Threading.Tasks.Task.WaitAll(
    System.Threading.Tasks.Task.Run(() => { first = Square(6); }),
    System.Threading.Tasks.Task.Run(() => { second = Square(7); }));
```

In async code it's awaited instead, and the statements can themselves await:

```piper
concurrency {                            // await Task.WhenAll(
    a = await Fetch(1);                  //     Task.Run(async () => { a = await Fetch(1); }),
    b = await Fetch(2);                  //     Task.Run(async () => { b = await Fetch(2); }));
}
```

The plugin chooses using `p.AsyncContext`:

- In an `async fn`, it awaits.
- In a plain function, it waits.
- In a trailing closure, it awaits only if one of its statements does, which makes the closure async anyway.

Only the statements that await become async tasks, so Piper doesn't warn about async lambdas without an `await`.

The same could be a library function with a trailing closure taking a list of actions. The plugin's benefit is that
each statement is a task, with no lambdas to write.

### `aspects` (decorators: `[Trace]`, `[Timed]`, `[Retry]`, `[Timeout]`, `[CircuitBreaker]` and `[Notify]`)

[Piper.Plugins.Aspects](../plugins/Piper.Plugins.Aspects/AspectsPlugin.cs) adds [decorators](#decorators).
`[Trace]`, `[Timed]`, `[Retry]`, `[Timeout]` and `[CircuitBreaker]` go on functions and methods; `[Trace]` also goes on a
class, tracing each of its methods. `[Notify]` goes on classes:

```piper
[Trace]                       // writes "→ Fetch(url = …)" and "← Fetch" to standard error
[Retry(3, 100)]               // up to 3 attempts, 100 ms apart
fn Fetch(url: string) => string requires url != "" { … }

[Timed(Console.Out)]          // writes "Report took 12.3 ms", here to standard output
fn Report() { … }
```

- **`[Trace]`** writes each call, with its arguments, and each return. **`[Timed]`** writes how long each call took.
  Both write to standard error, or to the `TextWriter` given as an argument. Each is a statement or two and a `defer`
  before the body, so they work on async functions and iterators too.
- **`[Retry(times)]`** and **`[Retry(times, delayMs)]`** run the body again when it throws, up to `times` attempts in
  all. The last attempt's exception escapes. In an async function the delay is awaited. Preconditions are checked
  once, before the first attempt, so a broken one isn't retried. An iterator can't be retried, as it runs only when
  it's read.

`[Retry(3)]` on `fn Load() => string { … }` becomes:

```piper
fn Load() => string {
    fn __attempt_1() => string { … }          // the body, whose `return`s still return its value
    mut __tries_2 = 1;
    while __tries_2 < 3 {
        try { return __attempt_1(); } catch (_) { __tries_2 += 1; }
    }
    return __attempt_1();                     // the last attempt, whose exception escapes
}
```

Two more policies, for calls to something that can be slow or down (both in Piper.Runtime's
[PiperPolicies](../src/Piper.Runtime/PiperPolicies.cs)):

```piper
[Retry(3, 200)]                 // outermost: a refused or timed-out attempt is tried again
[CircuitBreaker(5, 30000)]      // 5 failures in a row: refuse calls at once for 30 s
[Timeout(2000)]                 // innermost: each attempt waits at most 2 s
async fn Quote(sku: string) => Task<Price> { … }
```

- **`[Timeout(ms)]`:** an async function that hasn't finished in `ms` milliseconds throws a `TimeoutException` ("Quote
  took more than 2000 ms.") to its caller. The work isn't stopped, only waited for no longer, since nothing outside
  it can stop it safely; a function that should stop takes a `CancellationToken` and passes it on. A synchronous
  function can't have one: its caller can't stop waiting for it.
- **`[CircuitBreaker(failures, openMs)]`:** after `failures` failures in a row, calls throw a `CircuitOpenException`
  at once for `openMs` milliseconds, rather than waiting on what's failing and adding to its load. Its `RetryAfter`
  says how long is left. Then one call is let through to try: if it succeeds the circuit closes, and if it throws it
  opens again. A success resets the count. Each decorated function has one circuit, shared by all its callers (a
  service that's down is down for everyone); it works on async and synchronous functions.
- **Together,** decorators nest with the first written outermost, so the order above retries a refused or timed-out
  attempt, counts each timeout as a failure, and times each attempt on its own.

**`[Notify]`** makes a class implement `INotifyPropertyChanged`, which WPF and Avalonia data binding use. This is
what C# gets from a source generator:

```piper
[Notify]
class Person(first: string, last: string) {
    prop First: string { get; set; } = first;
    prop Last: string { get; private set; } = last;
    public mut age: int = 0;
    prop FullName => string = $"{First} {Last}";
}
```

- **What's notified:** each settable instance property written as a field is (`prop P: T { get; set; } = …`, with a
  `private` or `protected set` too), and each `public mut` field. Setting one to a different value raises
  `PropertyChanged` with its name. Setting it to the same value raises nothing.
- **Computed properties:** a getter-only property that uses a notified property, directly (`First`, `this.First`) or
  through another computed property, is raised too when that property changes. Here, setting `First` raises `First`
  and `FullName`.
- **Left alone:** `init` properties (set only while the object is made), statics, and `virtual`, `override` and
  `abstract` properties.
- **What it needs:** each notified property needs a type and an initial value, so its setter can be written and its
  value kept in a field. A missing one is an error saying what to add.
- **What it adds:** the `INotifyPropertyChanged` base, the `PropertyChanged` event, and
  `protected fn OnPropertyChanged(name: string)`, which raises it. Each is added only if the class doesn't already
  have it, and a property's setter can't be written by hand while the decorator writes it, so call
  `OnPropertyChanged` from a method to raise one yourself.
- **On a record** it's an error: a record can't change.
- **Adjusting it:** `[NotifyIgnore]` on a property leaves it alone. `[AlsoNotify("Total", …)]` on a property raises
  the properties named too, for dependencies the decorator can't see, such as one through a method.
- **Commands:** `[Command]` on a method adds an `ICommand` property for buttons and menus to bind to. `Save` gets
  `SaveCommand`, and an `Async` suffix is dropped (`LoadAsync` gets `LoadCommand`).
  - A `CanSave` method or computed property, if there is one, is the command's `CanExecute`. Without one, the
    command can always run.
  - When a property that `CanSave` reads changes, directly or through computed properties, the command raises
    `CanExecuteChanged`, so the button enables and disables itself.
  - The method must be an instance method, not private, taking no parameters. An async one is started and not
    awaited, as a button's click would be.
  - **Failures:** a method `OnCommandFailed(command: string, error: Exception)` on the class, if there is one, hears
    of each command's exception (`"Save"` and what it threw), async or not, as a place to show it. Without one, a
    plain command's exception escapes `Execute`, and an async one's is thrown where C#'s `async void` would throw it:
    on the synchronization context the command started on (the UI thread, so the framework's unhandled-exception
    handling sees it), or else the thread pool. It's never lost in a task nobody awaits.
  - Each command is a small class implementing `ICommand`, made the first time the property is read.

```piper
[Notify]
class Editor {
    prop Name: string { get; set; } = "";
    [NotifyIgnore] prop Draft: string { get; set; } = "";
    prop IsValid => bool = Name.Length > 0;

    [Command] fn Save() { … }
    fn CanSave() => bool = IsValid;      // setting Name re-raises SaveCommand's CanExecuteChanged
}
```

`First` becomes a field and a property whose setter raises the changes:

```piper
mut __first_1: string = first;
prop First => string {
    get = __first_1;
    set {
        if __first_1 != value {
            __first_1 = value;
            OnPropertyChanged("First");
            OnPropertyChanged("FullName");
        }
    }
}
```

### `services` and `endpoints` (dependency injection and web APIs)

`services` is in [Piper.Plugins.Services](../plugins/Piper.Plugins.Services/ServicesPlugin.cs), which works in any
program, and `endpoints` in [Piper.Plugins.Web](../plugins/Piper.Plugins.Web/WebPlugin.cs). A web program lists both
plugins in its piper.json and uses them together, as [samples/webapp](../samples/webapp/main.piper) does;
[samples/services](../samples/services/main.piper) uses `services` in a console program. Both are
`DeclarationKeyword`s, and each becomes a function.

```piper
services AppServices {
    singleton Clock = FixedClock("noon");   // made by this expression (or a `provider => …` factory)
    scoped Greeter => FriendlyGreeter;      // a service, and the type implementing it, which DI constructs
    transient Mailer;                       // a type DI constructs
}

endpoints Api "/api" {
    get "/hello/{name}" (name: string, greeter: Greeter) => greeter.Greet(name);
    post "/todos/{id:int}" async (id: int, todos: Todos) => { await todos.Save(id); id }
}
```

become:

```piper
fn AppServices(services: IServiceCollection) => IServiceCollection {
    ServiceCollectionServiceExtensions.AddSingleton<Clock>(services, __provider_1 => FixedClock("noon"));
    ServiceCollectionServiceExtensions.AddScoped<Greeter, FriendlyGreeter>(services);
    ServiceCollectionServiceExtensions.AddTransient<Mailer>(services);
    services
}

fn Api(app: IEndpointRouteBuilder) {
    let __group_2 = EndpointRouteBuilderExtensions.MapGroup(app, "/api");
    EndpointRouteBuilderExtensions.MapGet(__group_2, "/hello/{name}", (name: string, greeter: Greeter) => greeter.Greet(name));
    EndpointRouteBuilderExtensions.MapPost(__group_2, "/todos/{id:int}", async (id: int, todos: Todos) => { … });
}
```

A program calls them as `AppServices(builder.Services)` and `Api(app)`.

- **Writing the output:** the plugin calls the extension methods through their classes (fully qualified, shortened
  above), so the code works whatever the user's file imports. A plugin can't add a `using`.
- **Without the web:** a console program builds a provider from the function and asks it for services:

  ```piper
  let provider = AppServices(ServiceCollection()).BuildServiceProvider();
  let reminder = provider.GetRequiredService<Reminder>();
  ```

- **Packages and frameworks:** the services plugin lists the `Microsoft.Extensions.DependencyInjection` package, and
  the web plugin the `Microsoft.AspNetCore.App` framework, so a program needs only `"plugins"` in its piper.json. A web
  program gets dependency injection from ASP.NET Core, whose newer version wins over the package's.
- **Answering an api:** `endpoints Api: TrackerApi "/api" { … }` answers an `api` declaration (the [api
  plugin](#api-an-apis-shape-and-a-typed-client)), whose typed client calls it, and is checked against it: every call
  answered, each handler taking what its call sends and answering its type.
- **Routes** are written as ASP.NET Core documents them, `"/users/{id}"`: as in C#, only a `$"…"` string
  interpolates, so a plain string's braces are the route's own.
- **Typed links:** an endpoint can be named, between its verb and its route, and then has a route function in
  `<Group>Routes`, which makes its path. Its parameters are the route's values, with the types the handler takes them
  as, so a link with the wrong type, or to an endpoint that isn't there, doesn't compile:

  ```piper
  endpoints Site {
      get Lot "/lots/{id:int}" (id: LotId) => LotPage(id);
      get Files "/files/{*path}" (path: string) => FilePage(path);
  }

  <a href={SiteRoutes.Lot(lot.id)}>{lot.title}</a>     // "/lots/7"
  SiteRoutes.Files("photos/a b.jpg")                    // "/files/photos/a%20b.jpg"
  ```

  Each value is escaped for the path (a catch-all, `{*path}`, keeping its `/`s), and numbers, dates and ids are written
  in the invariant culture, as ASP.NET Core reads them. The group's prefix is included.

  **Query values** are parameters too, after the route's: those the handler takes that aren't in the route and are of a
  simple type (`string`, a number, `bool`, a date, `Guid`) or optional. Required ones come first; optional ones default
  to None, and are left out of the link when they are:

  ```piper
  get Search "/search" (q: string?, page: int?, auction: Auctioneer) => …;   // `auction`, a service, isn't one

  SiteRoutes.Search()                         // "/search"
  SiteRoutes.Search(q: "a&b", page: 2)        // "/search?q=a%26b&page=2"
  ```

  The plugin judges them from the types as written, before types are known, so a required query value of the
  program's own type (`page: PageNumber`) isn't seen; make it optional, or a builtin type.
- **Forms:** a handler taking a `Form<T>` (from Piper.Web) gets a posted HTML form read into the record `T`:

  ```piper
  record NewLot(title: string, startPrice: decimal, reserve: decimal?, category: Category, featured: bool)
      requires (reserve ?? startPrice) >= startPrice;

  post Create "/lots" (form: Form<NewLot>) => match form.Result {
      Ok(lot) => Listed(Save(lot)),
      Err(errors) => NewLotPage(Some(form), Some(errors)),   // shown again, as it was filled in, with what's wrong
  };
  ```

  - Each field is read from the form field of its name, by its type: text as it is; numbers (in the invariant culture,
    as browsers send them), dates, `Guid`s, enums and newtypes by parsing; a `bool` from a checkbox; and an optional
    field is None when empty.
  - `form.Result` is `Ok(record)`, or `Err(errors)`: a `FormErrors<NewLot>`, with `errors.For("startPrice")` the
    message for a field that's missing or isn't a value of its type ("'lots' isn't a number."), `errors.Form` the
    message for the whole form, and `errors.All` every message. `form.Text("title")` is what was sent, to fill the form
    in again. The field names given to `For` and `Text` are checked against the record's fields when they're
    constants: `errors.For("price")` is an error (PIP0511) listing them. (Any .NET method can ask for the same check,
    by marking a parameter `[PiperFieldName]`, from Piper.Runtime: its string names a field of a type argument of the
    method's type.)
  - **Every mistake at once:** each of the record's `requires` is checked wherever the fields it mentions were read, so
    a start price that isn't a number doesn't hide an empty title. Give each a message for people
    ([`else "…"`](language.md#preconditions-and-invariants)): one whose condition mentions a single field
    (`requires title.Trim().Length > 0 else "Give the lot a title."`) is that field's error (unless the field has one
    already), and any other the form's (several are put together, as sentences). Without a message, the condition
    itself is shown, which suits only a developer. (The compiler gives each record with `requires` a table of them,
    `__PiperRequirements`, which is how the form checks them one by one.)
  - **Typed forms:** `<form for={NewLot} method="post" …>` says which record the form is read into, and the name of each
    `input`, `select` and `textarea` written in it is checked against the record's fields: `name="startprice"` is an
    error (PIP0510) listing them. `for` isn't sent to the browser. Inputs a component or a hole makes aren't seen.
  - **Anti-forgery:** every `<form method="post">` in markup gets a hidden anti-forgery field, made for the request its
    page answers, and a `Form<T>` checks it before reading the form: a post without a valid one (a forged one, from
    another site) is a 400. It needs ASP.NET Core's antiforgery: `builder.Services.AddAntiforgery()`.
- **htmx** ([htmx.org](https://htmx.org), Zero-Clause BSD) for pages that update in part, with no JavaScript written:
  - **Served by the app:** each `endpoints` group maps `/_piper/htmx.min.js` (htmx 2.0.4, in Piper.Web; once per app),
    and `<Htmx.Script />` in a page's `<head>` loads it. It also sends the page's anti-forgery token as a header with
    every request htmx makes, so an `hx-post` on a button (with no form, so no field for the token) passes a
    `Form<T>`'s check as a form does.
  - **Layouts:** `endpoints Site layout Page { … }`, with `fn Page(body: Html) => Html`. An endpoint's markup is put in
    the layout for a browser loading the page, and sent alone, as the fragment htmx swaps in, when htmx asks (but in
    full for an `hx-boost`ed link, or history being restored, which want the page). So each handler is written once,
    for both:

    ```piper
    fn Page(body: Html) => Html = <html><head><Htmx.Script /></head><body>{body}</body></html>;

    endpoints Site layout Page {
        get Lot "/lots/{id:int}" (id: LotId) => LotView(id);              // the page, or just the fragment
        post Bid "/lots/{id:int}/bids" (id: LotId, form: Form<NewBid>) => match form.Result {
            Ok(bid) => PlaceBid(id, bid),
            Err(errors) => BidForm(id, Some(errors)),
        };
    }

    <button hx-post={SiteRoutes.Bid(lot.id)} hx-include="#amount" hx-target="#bids">Bid</button>
    ```

    The answer then varies by `HX-Request` (said in its `Vary` header), for caches.
  - **Live pages:** the server pushes fragments to the pages watching a topic, which htmx swaps in, so a price, a list
    or a countdown changes on every open page when it changes on the server. A page watches with htmx's server-sent
    events extension (htmx-ext-sse 2.2.2, Zero-Clause BSD, which `<Htmx.Script />` also loads), connecting to
    `Live.Url(topic)`, and elements in it say which events they swap in:

    ```piper
    <section hx-ext="sse" sse-connect={Live.Url($"lot-{lot.id}")}>
        <span sse-swap="price">{lot.current:C}</span>
    </section>
    ```

    Anything that changes it (a handler, the actor holding the lot) publishes the new fragment, by topic and event:
    `Live.Publish($"lot-{id}", "price", <span>{price:C}</span>)`, which says how many pages it went to. The app serves the
    connections at `/_piper/live` (mapped by every endpoints group, once per app): each sends its topics' events as
    they're published, and a comment every 15 seconds so proxies keep it open. A page that can't keep up has its oldest
    updates dropped rather than holding the publisher up, and a page that closes stops watching. There's no state per
    page beyond its connection, and the topics are the process's: an app on several pods publishes on each (from the
    actor that holds the state, say), as each pod's pages connect to it.
  - **All of it together:** [apps/auction](../apps/auction/README.md) is an auction site using markup, typed links,
    forms, a layout, htmx and live pages, with each lot an actor.
  - **`HtmxRequest`:** a handler taking `(htmx: HtmxRequest)` reads what htmx sent (`htmx.IsHtmx`, `Boosted`,
    `Target`, `TriggerId`, `TriggerName`, `CurrentUrl`), and tells it what to do next with response headers:
    `Redirect(url)`, `PushUrl(url)`, `ReplaceUrl(url)`, `TriggerEvent("bid-placed")` (for other parts of the page to
    react to, with `hx-trigger="bid-placed from:body"`), `Retarget(selector)`, `Reswap("outerHTML")` and `Refresh()`.
- **Piper's types in requests and responses:**
  - An optional handler parameter, `(name: string?, page: int?)`, is None when the request leaves it out, rather than a
    400. (C# gets it as `Option<string>?`, which ASP.NET Core treats as optional, and parses with `Option`'s `TryParse`.)
  - Records, Options and enums are JSON as [the language describes](language.md#json): None is `null`, and a variant
    is its name or an object naming it in `"kind"`.
  - A request body that a record's `requires` rejects is answered 400 before the handler runs, so a record's invariants
    validate the request.
  - A handler returning [markup](language.md#markup) (`Html`) answers with a page, `text/html`, as do `Some(page)` and
    `Ok(page)`: `get "/lots/{id:int}" (id: LotId) => LotPage(id);`, where `LotPage` returns `Html?`, is the page or a 404.
  - A handler returning an Option answers 200 with the value, or 404 for None, and one returning a Result answers 200
    with the value, or the error as ProblemDetails. Awaited results count too, and `Ok(None)` is a 404:

    ```piper
    using Piper.Web;

    [Status(409)]                                  // every error of this enum is a 409 Conflict…
    enum LoanError {
        [Status(404)] UnknownBook(code: string),   // …but this one is a 404
        OutOnLoan(code: string, until: string),
    }

    endpoints Library {
        get "/books/{code}" (code: string, books: Books) => books.Find(code);   // Book?: 200 or 404
        post "/loans/{code}" (code: string, books: Books) => books.Lend(code);  // Result<Loan, LoanError>
    }
    ```

    `Err(LoanError.OutOnLoan("emma", "Friday"))` answers 409 with
    `{"type": "…", "title": "Conflict", "status": 409, "kind": "OutOnLoan", "code": "emma", "until": "Friday"}`: the
    error's JSON (a variant's `kind` and fields, or a record's fields) as extension members. A field named `detail`,
    `title`, `type` or `instance` fills that member instead, if it's a string (and one named `status` is left out). A
    string error is the `detail`. Without `[Status]` on the variant or its enum (or record), an error is a 400.
  - That's done at run time by Piper.Web, which comes with the plugin: each endpoint gets an ASP.NET Core endpoint
    filter, but only if its handler returns an Option or Result, so other handlers cost nothing and answer as before.
  - **OpenAPI** (and anything else reading an endpoint's metadata, such as Swashbuckle or
    Microsoft.AspNetCore.OpenApi) is told what each endpoint answers, from its handler's type, as it's answered: the
    library's `/books/{code}` is 200 with a `Book`, or 404; `/loans/{code}` is 200 with a `Loan`, or a ProblemDetails
    with 404 or 409, the statuses `LoanError`'s `[Status]`es give. Markup is 200 `text/html`. In C#, these are
    `.Produces<Book>(200).Produces(404)`, written by hand beside the handler, where they can come to disagree with
    it. Piper.Web's own routes (htmx's script, live pages' stream) are left out of the description.
  - **OpenAPI tags** group endpoints in API documentation: `tags "Books"` after a group's route tags each of its
    endpoints, and an endpoint's own (`post "/{code}/loans" tags "Loans", "Admin" (…) => …;`) replace the group's.
    Without them, an endpoint is tagged with its declaration's name (`Library`), rather than ASP.NET Core's default,
    the application's name for every endpoint. Tags aren't policies, so they need no `policies` block.
- **Checks** report at the clause what ASP.NET Core would only find when the program runs, or not at all:
  - a service registered twice (the later would silently replace the earlier)
  - a route mapped twice with the same verb
  - a route parameter (`{postId}`) that no handler parameter takes
  - a handler parameter without a type (ASP.NET Core reads the types to bind the request)
  - an error type answered as ProblemDetails with a field named `status`, which would be left out, as ProblemDetails
    has its own (a warning, at the field)

  Errors in a handler's own code are reported in the handler, as for any lambda.

#### Who may call an endpoint, and how often

Words after a group's route apply to every endpoint in it. Words after an endpoint's route apply to it, replacing the
group's word for the same concern:

```piper
endpoints Admin "/admin" authorize "admin" ratelimit "api" {
    get "/users" (users: Users) => users.All();
    get "/status" anonymous () => "up";                     // anyone, in a group needing "admin"
    post "/import" authorize "owner" ratelimit "uploads" (users: Users) => …;
    get "/export" ratelimit off (users: Users) => …;        // not limited
    get "/report" cors "partners" cache "short" () => …;
}
```

| Word | Means | ASP.NET Core |
|---|---|---|
| `authorize` | signed-in users only | `RequireAuthorization()` |
| `authorize "a", "b"` | users the policies named allow | `RequireAuthorization("a", "b")` |
| `anonymous` | anyone | `AllowAnonymous()` |
| `ratelimit "p"`, `ratelimit off` | limited by the policy named, or not limited | `RequireRateLimiting("p")`, `DisableRateLimiting()` |
| `cors "p"` | other sites' pages may call it, as the policy says | `RequireCors("p")` |
| `cache`, `cache "p"` | responses are cached | `CacheOutput()`, `CacheOutput("p")` |

`authorize` with `anonymous`, or a word given twice in one place, is an error, as is `ratelimit` or `cors` without a
policy's name.

A `policies` declaration defines the policies:

```piper
policies Security {
    authorize "admin" roles "Admin";
    authorize "staff" claim "department" = "IT", "Ops";
    authorize "members";                                    // any signed-in user
    ratelimit "api" fixed 100 per minute;
    ratelimit "burst" sliding 20 per 10 seconds segments 5;
    ratelimit "tokens" bucket 50 refill 10 per second;
    ratelimit "uploads" concurrent 2;
    cors "partners" origins "https://partner.example";
    cache "short" expire 30 seconds;
}
```

It becomes two functions, since policies are registered before the app is built and the middleware is added after:

```piper
let builder = WebApplication.CreateBuilder();
Security(builder.Services);                                         // AddAuthorization, AddRateLimiter, AddCors, …
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies"); // how users sign in: the app's choice
let app = builder.Build();
UseSecurity(app);                                                   // the middleware, in the order ASP.NET Core needs
Admin(app);
```

- **Rate limits:**
  - `fixed`: at most N calls in each window.
  - `sliding`: at most N calls in any window, counted in segments.
  - `bucket`: a bucket of tokens, refilled each period.
  - `concurrent`: at most N calls at once.

  A call over the limit is answered **429 Too Many Requests**, not ASP.NET Core's default 503. Counts are
  expressions, so constants and configuration work. Times are `per minute`, `per 10 seconds`, or `expire 30 seconds`,
  in milliseconds, seconds, minutes or hours.
- **Authorization:** `roles` requires one of the roles, `claim` a claim (with one of the values given, if any), and a
  policy with neither any signed-in user.
- **Signing in** (cookies, JWT bearer tokens, an identity provider) is the program's own setup, as JWT bearer needs a
  package and its keys.
- **Middleware:** `UseSecurity` adds only what the policies need: CORS, authentication and authorization, the rate
  limiter, then output caching, in that order.
- **Checks:** the plugin compares the endpoints with the `policies` blocks. These are warnings, since a program can
  register policies in its own code:
  - an endpoint naming a policy no `policies` block declares (a typo such as `ratelimit "apii"`)
  - a declared policy no endpoint uses
  - endpoints using policy words in a program with no `policies` block at all

#### A server

`server` says what a web app is made of, rather than the builder code that makes it, and becomes the function
building it, with its middleware in the order ASP.NET Core needs:

```piper
server TrackerServer(settings: Settings, clock: Clock) {
    config smtp: Smtp;                              // a `config`, read as it starts, registered, and named
    urls settings.url;                              // a URL or a list of them
    https certificate settings.pfx;                 // HTTP redirected to HTTPS, serving this certificate
    logging off;
    services AppServices;                           // a `services` block, with its arguments if it takes any
    scoped TrackerDb = TrackerDb(settings.options); // or registrations of its own, as `services` writes them
    singleton Clock = clock;
    policies Security(settings);                    // registered, and its middleware added before the endpoints
    configure {                                     // anything else, written against `builder`
        builder.Services.AddAuthentication("Cookies").AddCookie("Cookies");
    }
    problems;                                       // errors as ProblemDetails
    openapi;                                        // /openapi/v1.json, with the openapi plugin
    endpoints Api, Admin;
    health "/healthz" ready "/ready" {
        check "database" async (db: TrackerDb) => await db.Database.CanConnectAsync();
    }
    worker every 1 minute (db: TrackerDb) => Purge(db); // background work: each minute, or for the app's life
    hosted Mailer;                                  // a .NET hosted service (a BackgroundService)
    stopping (jobs: Jobs) => jobs.Finish();         // run as it's asked to stop, and waited for
    shutdown 30 seconds;
}

async fn Main() => Task = await TrackerServer(Load(), SystemClock()).RunAsync();
```

- **What it becomes:** `fn TrackerServer(settings: Settings, clock: Clock) => WebApplication`. Its parameters are
  what the app is made from, so a test gives it others (`TestClient.Start(() => TrackerServer(settings,
  FixedClock(noon)))`) and `Main` runs it. The builder is `builder` and the app `app`, in `configure` and `use`.
- **Order:** registrations run in the order written, then the app is built and its middleware added: `problems` first
  (so it sees every exception), then `https`, then `use { … }` blocks (the app's own middleware, `app.Use(…)`), then the policies'
  (CORS, authentication, authorization, rate limits, caching), then health, OpenAPI and the endpoints. Writing
  `UseSecurity(app)` after the endpoints, where it silently does nothing, can't happen.
- **`provider`:** `provider store: IEventStore from "Events";` makes the implementation of a service the
  configuration chooses (see [Providers](#providers-chosen-by-configuration)) and registers it, naming it `store` for
  the clauses after. A provider that can't be made (an unknown name, a missing setting, a package not brought) stops
  the app as it starts, saying why. A `host` has it too.
- **`sources`:** `sources (configuration: IConfigurationBuilder) => configuration.AddAzureKeyVault(…);` adds where
  configuration comes from beyond appsettings.json and the environment, from any provider (a key vault, a cloud's
  secrets manager, mounted files), chosen by the app. It's added before anything reads configuration, wherever it's
  written, so every `config` sees it. A `host` has it too.
- **`config`:** `config Smtp;` reads a [`config`](#config-typed-configuration) record from the app's configuration
  (appsettings.json, environment variables) as the app is built, so a missing or invalid value stops it at startup,
  listing every problem, rather than failing the first request that needs it. It's registered as a singleton, and as
  `IOptions<Smtp>` for code written for .NET's options. `config smtp: Smtp;` also names it for the clauses after:
  `urls smtp.url;`, `policies Security(smtp);`.
- **`https`:** HTTP requests are redirected to HTTPS (`UseHttpsRedirection`), and outside Development browsers are
  told to use only HTTPS from then on (`UseHsts`). The certificate it serves can be given, for every https:// address:
  - `https certificate "server.pfx";`, or `… password secret;` for a .pfx file with one;
  - `https certificate "/tls/tls.crt" key "/tls/tls.key";` for PEM files, as a Kubernetes TLS secret mounts them.

  Each is an expression, so it can come from a `config` (`config tls: Tls;` then `https certificate tls.path password
  tls.password;`). The file is loaded as the server binds its HTTPS address, so a missing or unreadable one stops the
  app as it starts, saying which; a test's `TestClient` serves from memory, so needs no file, and isn't redirected.
  Without `certificate`, Kestrel's own is used: its configuration (`Kestrel:Certificates:Default`), or the development
  certificate. `urls` written as literals with no https:// address is a warning, as there'd be nowhere to redirect to,
  unless the server is behind a proxy (`forwarded`).
- **`forwarded`:** behind a proxy that ends TLS (a Kubernetes ingress, a load balancer), each request's scheme and
  client address are the ones the proxy saw, from its `X-Forwarded-Proto` and `X-Forwarded-For`
  (`UseForwardedHeaders`, first in the pipeline). So `https` redirects only what really arrived as HTTP, while the app
  itself listens on http://, and links, logs and rate limits see the client rather than the proxy.
  - `forwarded;` believes any proxy, as ASP.NET Core's `ASPNETCORE_FORWARDEDHEADERS_ENABLED` does. That suits an app
    only the proxy can reach, as in a cluster.
  - `forwarded from "10.0.0.0/8", "192.168.1.1";` believes only those networks and addresses. A literal that isn't
    one is an error.

  Without `forwarded`, the headers are ignored, as anyone could send them.
- **`problems`:** an exception is a 500 ProblemDetails (`UseExceptionHandler`), and a status without a body, such as a
  route nothing maps, gets one (`UseStatusCodePages`).
- **`health`:** ASP.NET Core's health checks. With only a path, it runs every check. With `ready` too, the first is
  liveness, healthy while the app runs (so Kubernetes doesn't restart it because a database is down), and `ready`
  runs the checks (so it's sent no requests until they pass). A check is a handler whose parameters are services,
  resolved from a scope of their own: healthy when it gives `true` (or a `HealthCheckResult` saying so), unhealthy
  when it gives `false` or throws. They answer 200 `Healthy` or 503 `Unhealthy`.
- **`stopping`:** a handler whose parameters are services, run when the app is asked to stop (Ctrl+C, or SIGTERM from
  Kubernetes) and waited for before the server stops: finishing work in hand, telling others it's going. `shutdown`
  says how long that, workers and requests in flight have, ASP.NET Core's `ShutdownTimeout` (30 seconds without it).
- **`worker`:** a handler run in the background as the app starts, whose parameters are services; a
  `CancellationToken` parameter is cancelled as the app stops, and the app waits for the worker to finish. Without
  `every` it runs for the app's life, with its services from one scope, and if it throws, the app logs it and stops,
  as .NET's hosted services do, rather than carrying on without it:

  ```piper
  worker async (jobs: Jobs, stopping: CancellationToken) => await RunWorkers(jobs, stopping);
  worker every 30 seconds (lots: Lots) => lots.CloseEnded();
  worker every tick (auction: Auctioneer) => auction.Tick(DateTime.UtcNow);   // a TimeSpan, a parameter here
  ```

  With `every` (`every 30 seconds`, `every minute`, or a `TimeSpan`), it runs as the app starts and then once each
  period, each run with a scope of its own (so a database context is fresh each time); a run that throws is logged,
  and the next runs as usual. A worker taking a period as the server's parameter lets a test give it one long enough
  not to interfere ([apps/auction](../apps/auction/web/site.piper) does). A test's `TestClient` starts and stops
  workers with the app.
- **`hosted Mailer;`:** a .NET hosted service, a class implementing `IHostedService` (such as a `BackgroundService`),
  started and stopped with the app: `AddHostedService`.
- **Checks:** a clause given twice that can only be given once (`urls`, `health`…), an endpoints block mapped twice, a
  server mapping no endpoints, and an unknown clause are errors. A server mapping endpoints that use policy words
  without a `policies` clause is a warning, as they'd be answered without the middleware they need.
- **What it needs, checked against what it registers.** Once the program is type-checked, the server is checked for
  what dependency injection would only find when a request comes in (a 500, or a failed health check):
  - a handler taking a service of the program's own (a class or trait; a record is a request's body) that nothing in
    the server registers, reported at the parameter: `S registers no Store, but this handler takes it`;
  - a health check, `stopping` hook or worker taking a class, trait or record nothing registers;
  - a class DI makes (`singleton Store;`, `scoped Repo => SqlRepo;`) whose constructor takes a service nothing
    registers, or a singleton taking a scoped service: a captive dependency, where one request's instance (a database
    context) would be kept for every other;
  - endpoints using `authorize` in a server that adds no authentication scheme (`AddCookie`, `AddJwtBearer`,
    `AddScheme<…>`), which ASP.NET Core would only refuse at the first such request.

  What's registered is read from the server and the Piper functions it calls (its `services` and `policies` blocks,
  `configure`): each generic call given the service collection registers its type arguments (`AddSingleton<Clock,
  SystemClock>`, `AddDbContext<Db>`). These are errors, or warnings where a registration could be out of sight: a
  method of the program's own given the services or the builder, or a registration taking a `Type`
  (`AddSingleton(typeof(Store))`).

#### Testing a web app

`TestClient` (in Piper.Web) serves an app from memory for `test` blocks, with no port: requests go straight to the
app, and responses come back as it writes them.

```piper
using Piper.Web;

async test "a bid is taken" {
    using let site = await TestClient.Start(() => BuildApp("http://localhost:5090"));
    AssertEqual("hello Ada", await site.GetStringAsync("/hello?name=Ada"));
    let bid = await site.PostForm(SiteRoutes.Bid(LotId(1)), ["bidder": "Ada", "amount": "40"], htmx: true);
    Assert((await bid.Content.ReadAsStringAsync()).Contains("highest bidder"));
    site.App.auction.Tick(DateTime.UtcNow);                // what BuildApp returned, as `App`
}
```

- **`TestClient.Start(build)`** calls the program's own function for building its app, unchanged (a `server`'s, such
  as `() => AuctionServer(url, auction)`, or one written by hand): whatever builds
  one web app (`WebApplication.CreateBuilder()`, or a host builder) and returns it, alone or with what the test
  drives directly, as `App`. While it runs, the app is given an in-memory server in place of Kestrel, so the
  addresses it asks for aren't used. Then the app is started; disposing the client stops it.
- **It's an `HttpClient`,** at `http://localhost/`: `GetStringAsync`, `PostAsJsonAsync` and the rest work as they do
  against a real server. It keeps the cookies the app sets, as a browser does, and `Services` is the app's services.
- **`PostForm(url, fields, htmx: false)`** posts the fields as a form, with the anti-forgery token the page would
  have given it (so `Form<T>` takes it); `htmx: true` posts as htmx does, for the fragment a layout's group sends it.
- **Live pages:** a response streams as the app writes it, so a test can read a page's server-sent events, and
  disposing the response ends its request, as closing the page does.
- **Errors:** an exception the app throws before answering fails the test with it (as ASP.NET Core's `TestServer`
  does), rather than being a 500; a `BadHttpRequestException` is its status, as Kestrel answers it (a missing
  anti-forgery token is a 400).
- It's Piper.Web's own code: ASP.NET Core's `TestServer` would be another package for every web program to restore.
  [apps/auction](../apps/auction/README.md) and [apps/tracker](../apps/tracker/README.md) test their HTTP this way.

### `openapi` (OpenAPI documents)

[plugins/Piper.Plugins.OpenApi](../plugins/Piper.Plugins.OpenApi) adds no syntax. It brings Microsoft.AspNetCore.OpenApi,
and [Piper.Web.OpenApi](../plugins/Piper.Web.OpenApi/PiperOpenApi.cs), whose schemas describe Piper's types as Piper
writes them in JSON. It's a plugin of its own so that only programs wanting OpenAPI carry the package:

```piper
// piper.json: { "plugins": ["web", "openapi"] }
using Piper.Web.OpenApi;

builder.Services.AddPiperOpenApi();     // or AddOpenApi(o => o.AddPiperSchemas()), for a document set up otherwise
let app = builder.Build();
app.MapOpenApi();                       // /openapi/v1.json
```

- **Why it's needed:** Piper's types each have a JSON converter, which the OpenAPI generator can't see through, so
  without this they're described as anything at all.
- **What's described:**
  - An Option is its value or `null`, and an optional field isn't required.
  - A newtype is its value: an `IssueId` (`newtype IssueId = int;`) is an integer.
  - An enum of fieldless variants is one of their names, a string. One with fields is one of its variants: a
    fieldless variant is its name, and one with fields is an object with `kind` (its name) and its fields.
  - A record with invariants is an object of its fields.
  - A parameter from the query, route or a header that's an Option or a newtype (`done: bool?`, `after: BookId`) is
    the value it's read as: a boolean, an integer. An optional one isn't required. (ASP.NET Core binds them with
    their `TryParse`, and on its own describes them as any string.)
- **With the web plugin's metadata,** each operation has its responses (200 with its value, 404 for an Option,
  ProblemDetails with each status an error's `[Status]`es give) and its tags. Piper.Web's own routes aren't in it.

### `api` (an API's shape, and a typed client)

[Piper.Plugins.Api](../plugins/Piper.Plugins.Api/ApiPlugin.cs) declares an HTTP API once, for its server and its
clients. Each call is written as a function whose body is its route:

```piper
// piper.json: { "plugins": ["api"] }
api TrackerApi "/api" {
    fn Projects() => List<Project> = get "/projects";
    fn Issue(id: IssueId) => IssueView? = get "/issues/{id:int}";
    fn Issues(projectId: ProjectId, status: string?, q: string?) => Result<List<IssueView>, IssueError> = get "/projects/{projectId:int}/issues";
    fn AddIssue(projectId: ProjectId, issue: NewIssue) => Result<IssueView, IssueError> = post "/projects/{projectId:int}/issues";
}
```

- **The client.** It becomes `class TrackerApi(http: HttpClient)`. Each call is an async method giving a `Result`, so
  nothing throws for a failed call: `await api.Issue(id)` is a `Task<Result<IssueView?, ApiError>>`.
  - What's sent: route values fill the route; simple and optional values are the query, left out for None; anything
    else is the JSON body (at most one, and not for GET or DELETE).
  - What comes back: the answer is read as the web plugin writes it. A 200 is the value. An Option's 404 is
    `Ok(None)`. A `Result`'s error is read back from its ProblemDetails: `ApiError<IssueError>`'s `Error` is the
    `IssueError` the handler returned (None for any other failure, such as a 401 or a 500).
  - Every `ApiError` has a `Status` (0 when the server couldn't be reached) and a `Message` (the ProblemDetails'
    `detail` or `title`). Headers every call sends, such as a token, go on the HttpClient.
- **The server.** `endpoints Api: TrackerApi "/api" { … }` says the endpoints answer the api, and the web plugin
  checks it once types are known:
  - every call has an endpoint of its method and route, and every endpoint is a call;
  - each handler takes what its call sends, by name and type, and takes nothing more the client would have to send
    (a required simple value, or a body); services, the user and optional values are the server's own;
  - each handler answers its call's type.

  A change to either side that the other doesn't follow is a compile error, rather than a failure in production.
- **Sharing it.** The api and the records it names go in a folder that both programs name in piper.json's
  `"projects"`, which compiles its source into each. [apps/tracker-core](../apps/tracker-core) holds the tracker's
  domain and `api TrackerApi`. [apps/tracker](../apps/tracker) answers it, and
  [apps/tracker-desktop](../apps/tracker-desktop) calls it, with the server's own records.
- **Light.** The client's code calls Piper.Runtime's `PiperClient`, so a client needs neither the web plugin nor
  ASP.NET Core. `[Status]` lives in Piper.Runtime too (still `using Piper.Web;`), so error enums can be shared.
- **Compatibility with clients already built.** Compiling the client and server together catches drift between
  them, but not a desktop already installed, or old pods during a rolling update. So an api's shape is recorded in
  `Name.shape.json` beside it, to check in. That covers its calls (method, route, what each sends and answers) and
  every record, enum and newtype they use, with which way each is sent. `piper build`, `run` and `test` record it the
  first time, then compare each change with it:
  - **Breaking, an error:**
    - a call removed, or its route or method changed;
    - a new value callers must send;
    - a value, or an answer, of another type;
    - a field callers send that's new and needed, or a field they read that's gone (unless it was optional);
    - a field of another type;
    - a variant removed from an enum callers send, or added to one they read.
  - **Compatible, recorded:** a new call; a new optional value or a field with a default; a new field in an answer.

  `--accept-breaking` records a deliberate break. The editor and `piper emit` only check, and a program with errors
  is neither checked nor recorded. [apps/tracker-core/TrackerApi.shape.json](../apps/tracker-core/TrackerApi.shape.json)
  is the tracker's.
- **Not yet:** a call answering nothing (`unit`), or `Result<T?, E>`.

### `config` (typed configuration)

[Piper.Plugins.Config](../plugins/Piper.Plugins.Config/ConfigPlugin.cs) reads configuration (appsettings.json,
environment variables, anything an `IConfiguration` holds) into a record, checked when it's read:

```piper
config Smtp from "Smtp" {
    host: string;                                   // required: missing is reported
    port: int = 587 requires port > 0 && port < 65536;
    user: string?;                                  // optional: None when absent
    timeout: TimeSpan = TimeSpan.FromSeconds(30);
    recipients: List<string>;                       // Smtp:Recipients:0, :1, …; empty when absent
    tls: Tls;                                       // another config, read from Smtp:Tls
}

let smtp = SmtpFrom(builder.Configuration);         // throws, listing every problem
match LoadSmtp(configuration) { Ok(s) => …, Err(problems) => … }
```

- **What it becomes:** ordinary Piper, with no reflection:
  - `record Smtp(…)`, with the fields' `requires` clauses as its invariants
  - `fn LoadSmtp(configuration) => Result<Smtp, List<string>>`
  - `fn SmtpFrom(configuration) => Smtp`
- **The section** is the string after `from`, or the config's name. Keys are matched as .NET matches them, without
  regard to case, so `Smtp__Port` in the environment works.
- **Field types:**
  - `string`, `int`, `long`, `double`, `decimal`, `bool`, `Guid`, `TimeSpan` and `DateTime`, each parsed as its
    `TryParse` does, in the invariant culture
  - any of those optional (`T?`) or in a `List<T>`
  - another config, for a nested section
- **Secrets:** `secret password: string;` (or `string?`, `int`, any one value) is read as its type and kept as a
  [`Secret`](language.md#secrets), so the config prints as `Database { host = db, password = *** }` and its value is
  `settings.password.Reveal()`. apps/tracker's webhook URL, which carries its token, is one. Where they come from is
  any configuration source the app chooses: a server's or host's `sources` clause adds one (a key vault, a cloud's
  secrets manager, mounted files), before any `config` is read.
- **Problems** are all reported at once, each with its key: `Smtp:port: "abc" isn't a valid int`,
  `Smtp:host is missing`, and, once every value is there, a broken invariant (`Smtp: Invariant failed: port > 0 …`).
- **Packages:** it brings `Microsoft.Extensions.Configuration.Json` and `.EnvironmentVariables` for console programs. A
  web program has them in ASP.NET Core.

### `hosting` (programs without a web server)

[Piper.Plugins.Hosting](../plugins/Piper.Plugins.Hosting/HostingPlugin.cs) adds `host`: a [`server`](#a-server)
without the web, for a console program or a worker, over .NET's generic host (`Host.CreateApplicationBuilder`). It
gets configuration (appsettings.json, environment variables), logging, dependency injection and a lifetime: it runs
until Ctrl+C or SIGTERM, then stops its workers and waits for them.

```piper
// piper.json: { "plugins": ["hosting", "config"] }
using Microsoft.Extensions.Hosting;

host WorkerNode(node: ActorSystem, options: Options) {
    config settings: Settings;                      // the same clauses as a server's, without the web's
    logging off;
    services AppServices;
    singleton Options = options;
    worker (options: Options, stopping: CancellationToken) => RunWorkers(node, options, stopping);
    worker every 1 minute (db: Db) => Purge(db);
    hosted Mailer;
    stopping (log: Log) => log.Flush();
    shutdown 30 seconds;
}

async fn Main(args: string[]) => Task = await WorkerNode(await StartNode(), Options.FromArgs(args)).RunAsync();
```

- **What it becomes:** `fn WorkerNode(node: ActorSystem, options: Options) => IHost`. `Main` runs it (`RunAsync` is
  `Microsoft.Extensions.Hosting`'s), and a test starts it (`await host.StartAsync()`), reaches its services
  (`host.Services`) and stops it (`await host.StopAsync()`). The builder is `builder` in `configure`.
- **Its clauses** are a server's that aren't about the web, and mean the same (see [A server](#a-server)): `config`,
  `logging off`, `telemetry` (see [below](#telemetry-opentelemetry)), `services`, `singleton`/`scoped`/`transient`,
  `configure`, `worker` (for its life, or `every` period), `hosted`, `stopping` and `shutdown`. So a program moves
  between the two by changing the word and adding or removing the web's clauses. Both call Piper.Hosting, which comes
  with each plugin.
- **Checks:** a clause given twice that can only be given once, an unknown clause and a handler parameter without a
  type are errors. As a server's are, its workers' and stopping hooks' services are checked against what it
  registers: one nothing registers, a class DI makes needing one, and a singleton taking a scoped service are errors
  (warnings where a registration could be out of sight).
- **Packages:** it brings `Microsoft.Extensions.Hosting`. [apps/grid](../apps/grid/main.piper)'s worker nodes are hosts.

### Providers, chosen by configuration

Piper's cloud-type services are each a trait or interface (`IEventStore` now; a messaging transport next), with
implementations, called providers, that a program chooses among by configuration as it starts, not in code. So the
same program runs locally, or on Azure, AWS or Google Cloud, by changing its settings. Every service has a local
provider, used when nothing is configured, so a program runs and is tested with no cloud at all.

```json
// appsettings.json, or the environment: Events__Provider=sqlite, Events__Connection=…
"Events": { "Provider": "sqlite", "Connection": "Data Source=events.db" }
```

```piper
server Shop {
    provider store: IEventStore from "Events";      // registered, and named for the clauses after
    …
}
// or, anywhere there's an IConfiguration:
let store = PiperHost.Provider<IEventStore>(configuration, "Events");
```

- **Choosing:** the section's `Provider` names one, and the rest of the section is its settings. With no `Provider`
  it's the local one. An unknown name, a missing setting or a provider whose package the program hasn't brought is a
  `ProviderException` at startup, saying which, and listing the names there are.
- **Where providers come from:** the local ones with Piper.Runtime; others with the plugin that brings them, which a
  program names in piper.json (with its packages). The compiler has the program register them as it starts.

| Service | Local | Others |
|---|---|---|
| `IEventStore` (persisted actors) | `memory` | `sqlite`, `sqlserver`, `postgres` (the data plugin; each with its EF Core package) |

- **Code doesn't depend on this:** an actor, a function or a test takes the service as a value
  (`Shelf.Spawn(store, …)`), so nothing outside a program's startup knows about configuration or dependency injection;
  a server's or host's `provider` clause also registers it, for code that's injected.
- **Writing one:** register it with `PiperProviders.Register<IEventStore>("name", settings => …, local: false)` from a
  static method your plugin lists in `Providers` (see [Writing a plugin](#writing-a-plugin)); `settings["Key"]`,
  `settings.Required("Key")` and `settings.RequiredSecret("Key")` read its settings.

### `telemetry` (OpenTelemetry)

[Piper.Plugins.Telemetry](../plugins/Piper.Plugins.Telemetry/TelemetryPlugin.cs) adds no syntax. A `server` or `host`
asks for telemetry with a clause, and the plugin brings what it calls: OpenTelemetry's SDK, its OTLP exporter, its
ASP.NET Core and HttpClient instrumentation, and [Piper.Telemetry](../plugins/Piper.Telemetry/PiperTelemetry.cs).

```piper
// piper.json: { "plugins": ["web", "telemetry"] }
server StockServer(shards: Shards, url: string) {
    telemetry "stock";               // or `telemetry;`, named as the server is
    …
}
```

- **Where it goes:** traces, metrics and logs go over OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT`, configured by
  OpenTelemetry's standard `OTEL_*` settings (`OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_PROTOCOL`, …). Without an
  endpoint nothing is added, so a run with no collector pays nothing.
- **Viewing it locally:** .NET Aspire's dashboard shows traces, metrics and structured logs with no setup:

  ```
  docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
  OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 piper run apps/stock -- …
  ```

  Then open http://localhost:18888. The dashboard prints a sign-in token in its own log.
- **What's in it:**
  - Traces: ASP.NET Core's requests (`POST /stock/{sku}/receive`, with the route and status), HttpClient's calls, and
    Piper's own sources (`Piper.*`): [remote actors' handlers](#actor-across-processes-and-machines), each continuing
    the trace that sent it. So one request is one trace, through HTTP, node A, and the actor on node B.
  - Metrics: ASP.NET Core's, Kestrel's, HttpClient's, .NET's runtime (memory, GC, threads) and Piper's
    (`piper.actor.handler.duration`).
  - Logs: whatever is written through `ILogger`, each line with its trace.
- **Needs ASP.NET Core:** the instrumentation does, so a `host` with `telemetry` runs on the aspnet runtime, as a
  server does. [apps/stock](../apps/stock/README.md) and [apps/grid](../apps/grid/README.md) use it.

### `data` (EF Core databases)

[Piper.Plugins.Data](../plugins/Piper.Plugins.Data/DataPlugin.cs) declares an EF Core database, and
[samples/data](../samples/data/main.piper) uses it with SQLite:

```piper
database Shop {
    table Customers: Customer key id;
    table Orders: Order key id index customerId index (customerId, placed);
}

let shop = Shop(DbContextOptionsBuilder<Shop>().UseSqlite("Data Source=shop.db").Options);
let big = await shop.Orders.Where(o => o.total > 100m).OrderBy(o => o.placed).ToListAsync();
```

- **What it becomes:** `class Shop(options: DbContextOptions<Shop>): DbContext(options)`, with a `DbSet` property per
  table, and the keys and indexes in `OnModelCreating`. Without `key`, EF Core looks for a field named `id` or
  `<type>Id`.
- **Queries** are LINQ over the tables. Their lambdas are [expression trees](language.md#queries-and-expression-trees),
  so EF Core translates them to SQL. `ToQueryString()` shows the SQL.
- **Rows:**
  - A class with `public mut` fields is tracked: change one and `SaveChangesAsync()` updates its row.
  - A record is read, added and removed whole. To change one, `Update` a copy.
- **Optional fields** are nullable columns, None being NULL, and a query compares one with `== None` (SQL's
  `IS NULL`), `!= None` or `== Some(value)`. It can't compare one with an optional variable, as EF Core reads only
  the variable's container. That's an error when the query runs, saying to choose the query by the variable instead.
- **[Newtypes](language.md#newtypes)** are stored as their values: an `IssueId` (`newtype IssueId = int;`) column is
  an int column, and an optional one is nullable. A key that's a newtype of `int` or `long` is numbered by the
  database, as an `int` key is: add a row with `IssueId(0)` and it's given the next number. Queries compare them in SQL
  (`i.projectId == projectId`). [apps/tracker](../apps/tracker/README.md) keys its issues, projects and comments this way.
- **Raw SQL** is written [`sql$"…"`](language.md#sql-strings), whose holes are parameters:
  `shop.Orders.Query(sql$"select * from Orders where customerId = {id}")` is a query LINQ can go on with,
  `shop.Query<string>(sql$"select name as Value from …")` selects values, and `await shop.ExecuteAsync(sql$"update …")`
  runs a command, giving the rows it changed. EF Core's `FromSql($"…")` works too. Its `FromSqlRaw` and
  `ExecuteSqlRaw`, given an interpolated string, are a warning (PIP0405), as they'd put its holes in the SQL as text.
- **Packages:** the plugin brings EF Core. The program's piper.json adds its database's provider, such as
  `"packages": { "Microsoft.EntityFrameworkCore.Sqlite": "10.0.14" }`.
- **Checked:** a `key` or `index` naming a field its type doesn't have (a class's must be public, as EF Core maps
  properties), and a table declared twice.

### `args` (command-line options)

[plugins/Piper.Plugins.Args](../plugins/Piper.Plugins.Args) declares what a program accepts on its command line, as a
record ([samples/todo-api](../samples/todo-api) uses it, as does `piper new --template web`):

```piper
args Options "Greets people." {               // the description is optional, and shown by --help
    greeting: string = "Hello" "What to say";  // --greeting Hi (or --greeting=Hi); a default, then a description
    times: int = 1;                            // --times 3
    loud: bool "Shout it";                     // --loud: a flag, false unless given
    name: string? "Who to greet";              // optional: None unless given
    dryRun: bool;                              // --dry-run: a field's name in kebab case
    files: List<string> "Names to add";        // the arguments that aren't options (and all those after --)
}

fn Main(args: string[]) {
    let options = Options.FromArgs(args);
    …
}
```

- **What it becomes:** `record Options(greeting: string, times: int, …)`, with `Options.Parse(args)`, a
  `Result<Options, string>`, and `Options.FromArgs(args)`, which gives the options or ends the program: on `--help`
  (or `-h`) it prints the usage and exits with 0, and on a mistake it prints the mistake and the usage and exits with 2.
  Tests use `Parse`.
- **Types:** `bool` (a flag), `string`, `int`, `long`, `double` and `decimal`, or one of those but `bool` made optional
  with `?`; and one `List<string>` for the other arguments. A field with no default that isn't optional is required.
- **Usage:** generated from the fields, with each one's type, description and default:

  ```
  Usage: greet [options] [files...]

  Greets people.

  Options:
    --greeting <string>  What to say (default: Hello)
    --times <int>        (default: 1)
    --loud               Shout it
    ...
  ```

- **Mistakes** on the command line are reported by name: an unknown option (suggesting the nearest, for a typo), a
  missing or malformed value (`'--times' must be an int, not 'abc'`), a value given to a flag, a missing required
  option, or an argument when the program takes only options.
- **Mistakes in the declaration** are reported where they are: an unsupported type, a default on a flag or an optional
  option, two fields with the same option name, or two `List<string>` fields.
- **Reading** is done by `PiperArguments` in Piper.Runtime, which every Piper program already references, so the
  plugin needs no package.

### `actor` (the actor model)

In [Piper.Plugins.Actors](../plugins/Piper.Plugins.Actors/ActorPlugin.cs), run in
[samples/actors](../samples/actors/main.piper). An actor has private state and handles one message at a time, so many
tasks can use it at once without locks:

```piper
actor Account(owner: string, opening: decimal) {
    mut balance = opening;                                   // state: only the handlers can reach it

    on Deposit(amount: decimal) { balance += amount; }       // a tell: the caller sends it and carries on
    on Balance() => decimal { balance }                      // an ask: the caller awaits the reply
}

await using let account = Account.Spawn("Ada", 100m);
account.Deposit(5m);
Print(await account.Balance());
```

An actor can also have functions, for what its handlers share. They can read and change its state, and are only
ever run by its handlers, so one message at a time. They're the actor's own, so they're written `fn` (or `async fn`),
not `private fn`; the remote actors plugin takes them too:

```piper
actor Ledger(limit: int) {
    mut balance = 0;
    fn Fits(amount: int) => bool = balance + amount <= limit;
    on Deposit(amount: int) => bool { if Fits(amount) { balance += amount; true } else { false } }
}
```

It becomes ordinary Piper, on .NET's `System.Threading.Channels`:

- `enum AccountMessage { Deposit(amount: decimal), Balance(__reply: TaskCompletionSource<decimal>) }`, a variant for
  each handler; an ask's carries where its reply goes.
- `record Account(__mailbox: ChannelWriter<AccountMessage>, …)`, with a method for each handler (`Deposit(amount)`
  posts the message; `async Balance()` posts it and awaits the reply), `Stop()`, and `DisposeAsync()` for `await using`.
- `Account.Spawn(owner, opening)`: declares the state, and starts a loop that matches each message to its handler.
  The state lives in that loop's closure, so nothing else can reach it, and the match is checked to cover every
  message.

How it behaves:

- **Faults:** if an ask's handler throws, the caller's `await` throws. If a tell's handler throws, the exception is
  written to the error output. Then the actor does what its failure policy says.
- **Failure policies:** `on failure …;` in the actor says what a handler that throws does to it (both plugins):

  ```piper
  actor Account(owner: string) {
      mut balance = 0;
      on failure rollback;                 // or: resume, restart, restart 3 per minute, stop
      on Withdraw(amount: int) => int {
          balance -= amount;
          if balance < 0 { throw InvalidOperationException("overdrawn"); }   // balance is put back
          balance
      }
  }
  ```

  - `resume` (the default) carries on with the state as the failed handler left it, which may be half-changed.
  - `rollback` puts the state back as it was before the message, so a handler either finishes or changes nothing,
    and nothing else is lost.
  - `restart` puts the state back as `Spawn` made it. With `3 per minute` (or `per second`, `per hour`), more
    restarts than that in the window stop the actor instead. Until actors keep their state (see
    [future](future.md#cloud-and-distributed)), a restart starts again from empty.
  - `stop` ends the actor. In one process, a message sent to it then throws "Account has stopped." at once, and asks
    already waiting fail with that error; across processes, Proto.Actor stops it, and an ask gets a
    `DeadLetterException`.

  `rollback` and `restart` work by keeping the state's values and putting them back, so the state must be immutable
  values throughout: numbers, strings, records, enums, `ImmutableList` and the like, checked at compile time. A
  `List`, kept, would be the same list with its changes, so it's an error, suggesting an immutable one or `stop`.
  Each state variable is declared on its own (`mut count = 0;`) for them.
- **Async handlers:** handlers can `await`. The actor takes its next message only when the current one is done, so a
  handler that asks another actor waits for the reply first. Two actors asking each other would deadlock.
- **Stopping:** `Stop()` or disposing stops taking messages and finishes those already sent. Sending to a stopped actor
  throws.
- **Syntax checks:** untyped handler parameters, duplicate handler names, a handler named `Spawn`, `Stop` or
  `DisposeAsync`, a `return` in a handler (its result is its block's value) and an actor with no handlers.
- **Type checks (analysis):** a warning for a message field whose type could be changed after it's sent, such as a
  `List<T>`, an array, or a record containing one. Records, enums, Piper's builtin types, immutable .NET types
  (`DateTime`, `System.Collections.Immutable`) and other actors' handles are fine.

The syntax describes what an actor is, not how it runs, so another plugin can run the same declarations differently.

#### Keeping state: persisted actors

An actor's state lives only in memory, so a process that stops loses it. A `persisted` actor keeps it as events
instead, saved as it handles messages and replayed when it starts again (event sourcing), in either actor plugin:

```piper
actor Shelf(sku: string) persisted by sku {
    mut onHand = 0;
    mut reserved = 0;

    event Received(quantity: int) { onHand += quantity; }
    event Reserved(quantity: int) { onHand -= quantity; reserved += quantity; }

    on Receive(quantity: int) { emit Received(quantity); }
    on Reserve(quantity: int) => bool {
        if quantity > onHand { false } else { emit Reserved(quantity); true }
    }
}

let store = MemoryEventStore();               // Piper.Runtime's; or any IEventStore
let tea = Shelf.Spawn(store, "tea");          // Spawn(system, name, store, …) across processes
```

- **Events are the only way the state changes.** A handler decides, and `emit Received(quantity);` applies the event
  at once (the handler sees its effect) and keeps it. When the handler finishes, the message's events are saved
  together, before an ask's answer is sent. When an actor with the same identity starts, in this process or after a
  restart, it replays its events to rebuild the state, before it handles any message.
- **Identity:** `persisted by sku` names each actor's stream of events, `Shelf/tea`. The store is the `store` that
  `Spawn` takes, first: Piper.Runtime's `MemoryEventStore`, or another `IEventStore` (`LoadAsync`, `AppendAsync`).
  Events are kept as JSON, as Piper's enums are written: `{"kind":"Received","quantity":10}`.
- **Failures:** a handler that throws saves none of its events, and its state is put back as before the message,
  so a persisted actor rolls back by its nature (`on failure resume` is an error). `restart` goes back to `Spawn`'s
  state and replays the saved events, reading the stream afresh; `stop` stops it.
- **Two writers:** a save expects the stream as the actor last read it. If something else wrote to it (the same
  actor running twice, on two nodes), the save throws `EventStreamConflictException` and saves nothing, so the
  handler fails; with `on failure restart`, the actor reads the stream again and catches up.
- **Checked at compile time:** a handler assigning the state itself (rather than emitting), an `emit` of an event
  the actor doesn't have, an event's body that awaits or emits (replaying it would do that again), a persisted actor
  without events, events in an actor that isn't persisted, and state that isn't immutable values (as for `rollback`).
- **Changing events:** across processes, the events are recorded in the actor's `Name.shape.json` with its
  messages, so a change that old streams couldn't be read as (an event removed, a new field needed) is a breaking
  change, reported as one.
- **Where events are kept is configuration:** an actor takes an `IEventStore`, and which one is the app's choice at
  startup, not code (see [Providers](#providers-chosen-by-configuration)):

  ```json
  "Events": { "Provider": "postgres", "Connection": "Host=…" }
  ```

  `memory` (the local one, used when nothing is configured) comes with Piper.Runtime. With the
  [data plugin](#data-ef-core-databases), Piper.Data's `DatabaseEventStore` keeps them in a `PiperEvents` table,
  through EF Core, so they outlive the process: `sqlite` (a local file), `sqlserver` (Azure SQL, Amazon RDS) or
  `postgres` (Amazon RDS and Aurora, Google Cloud SQL, Azure's), each needing its `Connection` and its EF Core package
  in piper.json. The table can share the program's own database: opening the store makes it if it isn't there. Each
  event is a row keyed by its stream and place in it, so two writers racing for one stream can't both succeed, even on
  two machines: the second gets `EventStreamConflictException`. A test, or a program wanting its own, passes a store
  directly: `Shelf.Spawn(MemoryEventStore(), "tea")`, or `DatabaseEventStore.Open(options)`.
- **Not yet:** snapshots for long streams (every event is replayed as the actor starts).

### `actor` across processes and machines

[Piper.Plugins.Actors.Remote](../plugins/Piper.Plugins.Actors.Remote/RemoteActorPlugin.cs) reads the same `actor`
syntax, but expands it onto [Proto.Actor](https://proto.actor), whose actors talk over gRPC. An actor hosted in one
process can then be used from others, on the same machine or across a network. A program picks it instead of the
in-process plugin in its piper.json; the plugin brings in Proto.Remote and ASP.NET Core (whose server carries the gRPC)
itself. [samples/remote-actors](../samples/remote-actors/main.piper) runs it:

```piper
// Every process that hosts or uses actors starts a node, an actor system other processes can reach:
let node = ActorSystem().WithRemote(RemoteConfig.BindToLocalhost(8000));
await node.Remote().StartAsync();

let account = Account.Spawn(node, "ada", "Ada", 100m);                 // host an actor here, under a name…
let remote = Account.Connect(node, "127.0.0.1:8000", "ada");           // …or reach one in another process

remote.Deposit(Transfer(25m, "salary"));                               // a tell: sent over the network
Print(await remote.Withdraw(50m));                                     // an ask: awaits the reply
```

```text
piper run samples/remote-actors -- host 8000                        # in one terminal (or machine)
piper run samples/remote-actors -- client 8001 127.0.0.1:8000       # in another
```

What's different from in-process actors:

- **Naming:** `Spawn(system, name, …)` hosts the actor under a name, and `Connect(system, address, name)` refers
  to an actor by its node's address (`host:port`) and name, wherever it runs.
- **Messages travel as JSON:** records, enums, Piper's builtin types and immutable .NET types can be sent. The plugin's
  analysis warns about fields that can't be sent as data: mutable collections, arrays, traits, and other actors'
  handles (send an actor's name instead).
- **Failures:**
  - An ask whose handler throws sends back a fault, which the caller throws as an `InvalidOperationException`.
  - An ask with no reply within 10 seconds throws a `TimeoutException`; `actor.WithTimeout(TimeSpan.FromSeconds(30))`
    gives a copy with another limit.
  - Asking an actor that doesn't exist throws Proto.Actor's `DeadLetterException` at once.
  - `on failure …` works as in one process (see the actor plugin above); `stop` stops the actor through Proto.Actor,
    so later messages to it are dead letters.
  - A tell that's lost (its node is down) is lost silently, as a message over a network can be.
- **The same program, run or built:** a message's type is named with the program's assembly name, so the nodes must
  run the same program. `piper run` names it as `piper build` does (after its piper.json `name`, or its folder), so a
  built node and a `piper run` one understand each other.
- **Rolling updates are checked:** while a new version deploys, old and new nodes send each other messages. So each
  actor's messages are recorded in `Name.shape.json` beside it, as an [api's shape](#api-an-apis-shape-and-a-typed-client)
  is: each handler, what it takes, what it replies (or that it's a tell), and the records and enums they carry.
  `piper build`, `run` and `test` record it and check each change against it. A handler removed, a value it newly
  needs, a reply of another type, or a field the other side reads that's gone is an error, unless you build with
  `--accept-breaking`. A new handler, or a new optional value, is compatible.
  [apps/stock](../apps/stock/actors/Product.shape.json) and apps/grid record theirs.
- **Traced:** a message sent within a trace (an HTTP request's, or another handler's) carries its trace context
  (`traceparent`) in Proto.Actor's message headers, so the message itself doesn't change. Each handler runs in a span
  continuing that trace: `Product.Receive`, kind Server for an ask and Consumer for a tell, tagged with the actor, the
  message and the node, and marked failed if it throws. So one request is one trace across nodes. Each handler's time
  is the `piper.actor.handler.duration` metric. This uses .NET's own `Activity` and `Meter` (the `Piper.Actors`
  source), so it costs nothing unless something listens, such as the [telemetry plugin](#telemetry-opentelemetry), or
  a test's `ActivityListener`.

For grid computing, which spreads one job over many machines, [samples/grid](../samples/grid/main.piper) builds on the
same handles. Each machine runs a worker node, which hosts a `Worker` actor per core. The coordinator splits the job
into chunks, and one loop per worker takes the next chunk from a shared queue, so faster nodes do more. A chunk whose
ask fails (its node stopped, or the ask timed out) goes back in the queue for another worker:

```piper
async fn Drain(worker: Worker, queue: ConcurrentQueue<Chunk>, results: ConcurrentDictionary<int, ChunkResult>) => Task<bool> {
    while queue.TryDequeue(out let chunk) {
        try {
            let result = await worker.Count(chunk);       // runs on the worker's machine
            results[result.id] = result;
        } catch (e: System.Exception) {
            queue.Enqueue(chunk);                         // another worker will take it
            return false;
        }
    }
    true
}
```

```text
piper run samples/grid -- worker 8001                                    # on each machine
piper run samples/grid -- coordinator 8000 10.0.0.6:8001 10.0.0.7:8001   # then share a job among them
```

[apps/grid](../apps/grid/README.md) turns that around for Kubernetes: workers ask the coordinator for chunks, so they
need only its address, and pods can be added or removed mid-job. A chunk not sent back within its lease goes to
another worker.

[apps/stock](../apps/stock/README.md) runs actors as pods in Kubernetes (AKS). Products are actors shared out among
the nodes by a hash of their code, and every node's HTTP API reaches any product wherever it is. Its StatefulSet gives
each pod a stable DNS name, through a headless service, for the other nodes to reach it by.

Still to come: actors that move between nodes when one fails, and finding nodes without addresses. That's
Proto.Actor's clustering, or Orleans's virtual actors, which need Piper to have classes.

### `statemachine` (state machines)

[Piper.Plugins.StateMachines](../plugins/Piper.Plugins.StateMachines/StateMachinePlugin.cs) adds `statemachine`
declarations, for an object that moves between named states as events happen to it.
[samples/state-machines](../samples/state-machines/main.piper) runs it:

```piper
statemachine Order(id: string, total: decimal) {
    mut paid = 0m;                       // the machine's data: private fields…
    public mut tracking = "";            // …or public ones

    state Pending {                      // the first state is where a machine starts
        on Pay(amount: decimal) => Paid if paid + amount >= total { paid += amount; }
        on Pay(amount: decimal) => Pending { paid += amount; }
        on Cancel(reason: string) => Cancelled;
    }
    state Paid {
        enter { Print($"{id} is paid"); }
        on Ship(code: string) => Shipped { tracking = code; }
    }
    state Shipped;                       // no transitions: a final state
    state Cancelled;
}

let order = Order("order-42", 30m);
order.Pay(10m);                          // still Pending
order.Pay(20m);                          // Paid
Print($"{order.State}: {string.Join(", ", order.Events)}");   // Paid: Ship
```

A transition is `on Event(parameters) => Target`, with an optional `if` guard and an optional block, its action. The
guard comes after the target, as a `match` arm's does.

A machine can also have functions, for what its transitions share. They're methods of its class, as a class's are:
public unless `private`, and able to read and change the machine's data:

```piper
statemachine LotLife(ends: DateTime) {
    public mut leader: string? = None;
    public mut bids = 0;

    private fn Take(bidder: string) {    // what every transition taking a bid does
        leader = Some(bidder);
        bids += 1;
    }

    state Open {
        on Bid(bidder: string, at: DateTime) => Closing if ends - at < TimeSpan.FromMinutes(1) { Take(bidder); }
        on Bid(bidder: string, at: DateTime) => Open { Take(bidder); }
    }
    state Closing { … }
}
```

The plugin generates:

- **`OrderState`:** an enum of the states.
- **`OrderEvent`:** an enum of the events, with a variant for each and its parameters as fields. Events are data,
  which can be stored, sent to an actor or another process, and replayed with `order.Fire(event)`.
- **`Order`:** a class, whose constructor takes the machine's parameters. It has:
  - the machine's `let`s and `mut`s as fields
  - `State`
  - `IsFinal` (its state has no transitions)
  - `Events`: the events the current state has transitions for, whether or not their guards hold
  - a method for each event (`Pay`), and `CanPay`, which says whether `Pay` would be taken now
  - `Fire(event)`, handling an event given as data, and `CanFire(event)`, which says whether it would be taken now
  - `Order.Resume(state, …)`: a machine made in a state it reached earlier, such as one saved in a database, without
    running that state's `enter` block

How it behaves:

- **Choosing a transition:** an event takes the current state's first transition for it whose guard holds. If there's
  none, it throws an `InvalidOperationException` (`Order can't Ship while Pending`).
- **Order:** the old state's `exit` block runs, then the transition's action, then the machine moves, then the new
  state's `enter` block runs. `exit` and `enter` run only when the state changes, so a transition back to its own state
  (`on Pay(…) => Pending`) runs only its action.
- **Synchronous:** actions, guards, and `enter` and `exit` blocks can't `await`.
- **Not thread-safe:** like any class, a machine isn't safe to use from several threads at once. To share one, keep it
  in an actor.
- **Syntax checks:**
  - a target that isn't a state, or a duplicate state
  - an event whose transitions take different parameters
  - a transition that's never taken, because an earlier one for the same event in that state has no guard
  - an event named after a generated member (`Fire`, `CanFire`, `Resume`, `State`, `IsFinal`, `Events`, or `CanX`
    when there's an `X`)
  - untyped parameters, a second `enter` or `exit` in a state, and a machine with no states
- **Analysis:** a warning for a state that no chain of transitions reaches from the first.

### Packages and frameworks for plugins

`services` and `endpoints` compile to calls into ASP.NET Core and its DI. A program gets those by listing
`"frameworks": ["Microsoft.AspNetCore.App"]` in its `piper.json` (see the [web sample](../samples/web/main.piper)).
A plugin whose code needs them says so itself, so a program using the plugin gets them without listing them:

```csharp
public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];
public IReadOnlyDictionary<string, string> Packages => new Dictionary<string, string> { ["Humanizer.Core"] = "2.14.1" };
```

A version in the program's own `piper.json` wins over a plugin's.

A package's **native libraries** (SQLite's `e_sqlite3`, say) come with it too: those for the platform piper runs on
are loaded for `piper run` and `piper test`, and copied next to the program by `piper build`.

## Not yet

- **Keywords in expressions:** constructs that start mid-expression (`let x = select … from …`). Only statement and
  declaration positions exist.
- **Highlighting:** the TextMate grammar can't know plugin keywords. The language server's semantic highlighting could
  mark them.
- **More for actors:**
  - supervision policies (restart with fresh state after a fault)
  - bounded mailboxes (senders wait when one is full)
  - clustering (actors placed across nodes and moved when one fails), on Proto.Actor's cluster or Orleans
