# Samples

Small, runnable programs, one per area of the language. Each runs with `piper run samples/<name>`. The tests in
`testing.piper` and `library/` run with `piper test samples/<name>`. The test suite runs every sample, so they stay
correct.

| Sample | What it shows |
|---|---|
| [hello.piper](hello.piper) | A first program: a record, functions, a loop and string interpolation |
| [basics.piper](basics.piper) | `let` and `mut`, functions (with doc comments, defaults and named arguments), `if` and `match` as expressions, ranges (`1..5`, `0..<n`), loops, tuples, blocks with values, C#'s numeric rules, raw strings |
| [shapes.piper](shapes.piper) | Records and enums, exhaustive `match` with guards, property patterns (`Rect { width: w }`), list patterns (`[first, ...rest]`), relational patterns (`< 10.0`), `{ ...p, y: 5 }` copies, `is` |
| [option_result.piper](option_result.piper) | No null: `T?`, `?.`, `??`, `?` and `let … else` on optionals; `Result` with `Ok`/`Err` and `?` to propagate errors |
| [traits.piper](traits.piper) | Traits with default and static methods, trait inheritance, `extend` (like Rust's `impl`) on records and on `int` and `string`, generics with bounds, traits as types |
| [collections.piper](collections.piper) | Collection literals and spreads, lambdas and function types, LINQ, the pipe operator `\|>`, dictionaries, `out` parameters, grouping, tuples |
| [async.piper](async.piper) | `async`/`await`, `Task.WhenAll`, `yield` iterators, async iterators with `await for`, `using let` and `defer`, async `Main` |
| [interop.piper](interop.piper) | Calling .NET: constructing types, nullable APIs as optionals, `out` parameters, events, implementing `IComparer<T>`, JSON with attributes, catching exceptions |
| [contracts.piper](contracts.piper) | `requires`: preconditions on functions and invariants on records, checked on every call and copy, and at compile time for constant arguments |
| [classes.piper](classes.piper) | Classes: a primary constructor, private and public fields, a property, private methods, identity, an `Exception` subclass, abstract and virtual methods with overrides and `base` calls, type patterns (`c: Circle`) and casts, overriding a protected .NET method, a generic class |
| [operators.piper](operators.piper) | Arithmetic operators on records, including unary minus and a static operator for `2 * v`, and `+=` |
| [testing.piper](testing.piper) | `test` blocks, `Assert` and `AssertEqual`, next to the code they test |
| [library/](library) | A program of several files with a `piper.json`: namespaces, `using` and qualified names, `private` functions, types and methods (and what a public signature can't expose), extending a type from another file, and tests |
| [web/](web) | ASP.NET Core from `piper.json`'s `"frameworks"`: a minimal API with dependency injection, which serves on a free port, calls itself and stops |
| [services/](services) | Dependency injection in a console program, with the services plugin's `services { }` and no web framework (build `plugins/Piper.Plugins.Services` first) |
| [webapp/](webapp) | The same kind of web API declared with the services plugin's `services { }` and the web plugin's `endpoints { }` (build both plugins first), whose piper.json lists only the plugins |
| [todo-api/](todo-api) | A to-do REST server with DI services and endpoints: JSON in and out, route, query and body parameters, 201/204/400/404 responses. `piper run samples/todo-api --serve` serves on localhost:5080 (see its [README](todo-api/README.md)) |
| [actors/](actors) | Actors from the actors plugin (build `plugins/Piper.Plugins.Actors` first): an account with private state, which a hundred tasks deposit into at once, with tells, asks, an async handler and a refused withdrawal |
| [remote-actors/](remote-actors) | Actors across processes and machines, with the remote actors plugin (build `plugins/Piper.Plugins.Actors.Remote` first): run with no arguments for two nodes in one process, or `-- host 8000` and `-- client 8001 127.0.0.1:8000` in two terminals |
| [plugins/](plugins) | Extending the language without changing the compiler: `parallel for` and `concurrency { }` from a compiler plugin listed in `piper.json` (build `plugins/Piper.Plugins.Parallel` first), and a trailing closure (`Retry(3) { … }`) |

## Visibility in brief

Declarations are public by default. `private` restricts a function, record, enum, trait or extend-block method to its
own file. Classes differ: their fields are private to the class unless marked `public`, and their methods public
unless marked `private` (see [classes.piper](classes.piper)). There's no `internal` or `protected` keyword yet; those
words are reserved so later versions can use them. Namespaces group declarations across files; see
[library/](library).
