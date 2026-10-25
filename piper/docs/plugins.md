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
    Print("tick {i}");
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

List the plugin's assembly in the program folder's `piper.json`, with a path relative to the folder:

```json
{
  "plugins": ["../plugins/Piper.Plugins.Parallel/bin/plugin/Piper.Plugins.Parallel.dll"]
}
```

`piper run`, `piper test`, `piper build` and `piper emit` on that folder load it, and so does the language server for
files in the folder. [samples/plugins](../samples/plugins) is a runnable example, using
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
- **Reading:** `PluginParser` gives the tokens after the keyword (`Current`, `Peek`, `NextToken`, `Expect`, and
  `AtWord`/`ExpectWord` for words special to the construct, such as `singleton`). It also gives Piper's parser for the
  pieces: `ParseExpression`, `ParseExpressionBeforeBlock` (where a `{` follows), `ParseBlock`, `ParseType`,
  `ParsePattern`, `ParseLambdaParameters` and `ParseDeclaration`. Like Piper's parser, these never throw: a missing
  token is reported and synthesized, so the plugin can carry on.
- **Errors:** `p.Report(span, message)` reports an error in the construct (PIP0503), followed by the plugin's name.
- **Writing the result:** `p.Template` builds syntax from Piper source with `$name` holes:
  - **What a hole can hold:** an expression or block, a type, a token, or a string (as a name). Where a list goes (call
    arguments, collection elements, a block's statements), a hole can hold a sequence, which is spliced in.
  - **Holes in place of a block:** where Piper needs a block, write `{ $body }`. With a block as its value, that's the
    block itself.
  - **Source positions:** the template's own syntax is given the construct's position, and the holes' syntax keeps its
    own.
  - **Plugin bugs:** a template that doesn't parse, or a hole that isn't filled, throws.
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

Loading errors are reported against `piper.json`, and in the editor they appear on that file.

## Four features as plugins

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

### `services` and `endpoints` (dependency injection and web APIs)

Both are in [Piper.Plugins.Web](../plugins/Piper.Plugins.Web/WebPlugin.cs) and run in
[samples/webapp](../samples/webapp/main.piper). Both are `DeclarationKeyword`s, and each becomes a function.

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
- **Framework:** the plugin lists `Microsoft.AspNetCore.App` among its frameworks, so a program needs only
  `"plugins"` in its piper.json.
- **Routes** are written as ASP.NET Core documents them. In plain Piper `{id}` in a string is interpolation, so a
  handwritten call needs `"/users/\{id}"`. The plugin rebuilds the route from the token as written.
- **Checks** report at the clause what ASP.NET Core would only find when the program runs, or not at all:
  - a service registered twice (the later would silently replace the earlier)
  - a route mapped twice with the same verb
  - a route parameter (`{postId}`) that no handler parameter takes
  - a handler parameter without a type (ASP.NET Core reads the types to bind the request)

  Errors in a handler's own code are reported in the handler, as for any lambda.

### Packages and frameworks for plugins

`services` and `endpoints` compile to calls into ASP.NET Core and its DI. A program gets those by listing
`"frameworks": ["Microsoft.AspNetCore.App"]` in its `piper.json` (see the [web sample](../samples/web/main.piper)).
A plugin whose code needs them says so itself, so a program using the plugin gets them without listing them:

```csharp
public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];
public IReadOnlyDictionary<string, string> Packages => new Dictionary<string, string> { ["Humanizer.Core"] = "2.14.1" };
```

A version in the program's own `piper.json` wins over a plugin's.

## Not yet

- **Keywords in expressions:** constructs that start mid-expression (`let x = select … from …`). Only statement and
  declaration positions exist.
- **Highlighting:** the TextMate grammar can't know plugin keywords. The language server's semantic highlighting could
  mark them.
