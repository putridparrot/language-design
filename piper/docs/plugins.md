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
come with piper (in a `plugins` folder beside it), and are named: `actors`, `args`, `aspects`, `config`, `data`, `remote-actors`, `parallel`,
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

### `aspects` (decorators: `[Trace]`, `[Timed]`, `[Retry]` and `[Notify]`)

[Piper.Plugins.Aspects](../plugins/Piper.Plugins.Aspects/AspectsPlugin.cs) adds four [decorators](#decorators).
`[Trace]`, `[Timed]` and `[Retry]` go on functions and methods; `[Trace]` also goes on a class, tracing each of its
methods. `[Notify]` goes on classes:

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
- **Routes** are written as ASP.NET Core documents them, `"/users/{id}"`: as in C#, only a `$"…"` string
  interpolates, so a plain string's braces are the route's own.
- **Piper's types in requests and responses:**
  - An optional handler parameter, `(name: string?, page: int?)`, is None when the request leaves it out, rather than a
    400. (C# gets it as `Option<string>?`, which ASP.NET Core treats as optional, and parses with `Option`'s `TryParse`.)
  - Records, Options and enums are JSON as [the language describes](language.md#json): None is `null`, and a variant
    is its name or an object naming it in `"kind"`.
  - A request body that a record's `requires` rejects is answered 400 before the handler runs, so a record's invariants
    validate the request.
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
- **Checks** report at the clause what ASP.NET Core would only find when the program runs, or not at all:
  - a service registered twice (the later would silently replace the earlier)
  - a route mapped twice with the same verb
  - a route parameter (`{postId}`) that no handler parameter takes
  - a handler parameter without a type (ASP.NET Core reads the types to bind the request)

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
- **Problems** are all reported at once, each with its key: `Smtp:port: "abc" isn't a valid int`,
  `Smtp:host is missing`, and, once every value is there, a broken invariant (`Smtp: Invariant failed: port > 0 …`).
- **Packages:** it brings `Microsoft.Extensions.Configuration.Json` and `.EnvironmentVariables` for console programs. A
  web program has them in ASP.NET Core.

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
  written to the error output and the actor carries on with the next message.
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
  - A tell that's lost (its node is down) is lost silently, as a message over a network can be.
- **The same program, run or built:** a message's type is named with the program's assembly name, so the nodes must
  run the same program. `piper run` names it as `piper build` does (after its piper.json `name`, or its folder), so a
  built node and a `piper run` one understand each other.

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
