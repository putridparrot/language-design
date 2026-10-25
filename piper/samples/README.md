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
| [operators.piper](operators.piper) | Arithmetic operators on records, including unary minus and a static operator for `2 * v`, and `+=` |
| [testing.piper](testing.piper) | `test` blocks, `Assert` and `AssertEqual`, next to the code they test |
| [library/](library) | A program of several files with a `piper.json`: namespaces, `using` and qualified names, `private` functions, types and methods (and what a public signature can't expose), extending a type from another file, and tests |
| [web/](web) | ASP.NET Core from `piper.json`'s `"frameworks"`: a minimal API with dependency injection, which serves on a free port, calls itself and stops |
| [webapp/](webapp) | The same kind of web API declared with the web plugin's `services { }` and `endpoints { }` (build `plugins/Piper.Plugins.Web` first), whose piper.json lists only the plugin |
| [plugins/](plugins) | Extending the language without changing the compiler: `parallel for` and `concurrency { }` from a compiler plugin listed in `piper.json` (build `plugins/Piper.Plugins.Parallel` first), and a trailing closure (`Retry(3) { … }`) |

## Visibility in brief

Declarations are public by default. `private` restricts a function, record, enum, trait or extend-block method to its
own file. There's no `public`, `internal` or `protected` keyword, as there's no inheritance between Piper types. Those
words are reserved, though, so later versions can use them. Namespaces group declarations across files; see
[library/](library).
