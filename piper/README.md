# Piper

A small C-style, expression-oriented language that transpiles to C#: records, enums with exhaustive `match`,
traits, no null references (`T?` is an `Option`), errors as values (`Result`), and everything an expression, with
C#'s types, numeric rules and the whole of .NET underneath.

- **New to Piper?** Start with [the tour](docs/tour.md), written for C# developers.
- **The reference:** [docs/language.md](docs/language.md) has the grammar, semantics and how each construct maps to C#.
- **Extending the language:** [docs/plugins.md](docs/plugins.md) covers trailing closures and compiler plugins, which add
  keywords such as `parallel for` without changing the compiler.
- **Examples:** [samples/](samples) has a runnable program for each area of the language.
- **What's in each release:** [CHANGELOG.md](CHANGELOG.md).

## Installing

Piper needs the .NET 10 SDK. It isn't on nuget.org yet, so install the `piper` tool from this repository (from the
`piper` folder); the [VS Code extension's README](editors/vscode/README.md#installing-it-locally) covers the editor:

```
dotnet pack src/Piper.Cli -c Release -o artifacts
dotnet tool install -g Piper --add-source artifacts
```

The installed tool is a copy of the compiler as it was built; after changing the compiler, pack again and reinstall it
(`dotnet tool uninstall -g Piper`, then the install line above).

```
piper run hello.piper         # transpile, compile and run
piper build src/ -o out/      # write the C# (one .cs per .piper file; it references the Piper.Runtime package)
piper build app.piper --exe   # compile to bin/app.exe (plus app.dll, its .pdb and Piper.Runtime.dll)
piper build src/ --library    # compile to bin/src.dll; --name and -o choose the name and folder
piper test src/               # run the program's test blocks (--filter picks some by name)
piper emit hello.piper        # print the C#
```

A program folder can say what it builds into, and what it uses, with a `piper.json`, which `piper build src/` then
follows (flags override it):

```json
{
  "name": "app",
  "output": "exe",
  "packages": { "Humanizer.Core": "2.14.1" },
  "frameworks": ["Microsoft.AspNetCore.App"],
  "references": ["lib/My.Library.dll"],
  "plugins": ["../plugins/My.Plugin.dll"]
}
```

- **`output`** is `"exe"` or `"library"`.
- **`packages`** are NuGet packages. They're restored with the .NET SDK, using your NuGet configuration, and remembered
  in the folder's `obj/piper`, so that only happens again when they change.
- **`frameworks`** are shared frameworks such as ASP.NET Core, which come with .NET rather than as packages. On .NET 10,
  `Microsoft.Extensions.DependencyInjection` and the other `Microsoft.Extensions` libraries are part of ASP.NET Core's.
- **`references`** are .NET assemblies, with paths relative to the folder.
- **`plugins`** add keywords to the language (see [docs/plugins.md](docs/plugins.md)).

Every key is optional; an empty file is fine. Any other key is an error, so future versions can add keys without
changing what today's files mean. `piper run`, `piper test` and the editor use the packages, frameworks and references
too, and `piper build --exe` copies the packages' and references' assemblies next to the program.

`--exe` makes a native launcher from the .NET SDK's apphost, as `dotnet build` does; with only a runtime installed you get
the DLL, which runs with `dotnet app.dll`. For publishing (single-file, AOT) or mixing with C#, use a project: to compile
`.piper` files as part of a .NET project, alongside its C#, reference the `Piper.Build` package:

```xml
<ItemGroup>
  <PackageReference Include="Piper.Build" Version="1.0.0" />
</ItemGroup>
```

Every `.piper` file in the project becomes part of one Piper program, which the project's C# can call. It also brings in
`Piper.Runtime`, the small library defining `Option` and `Result` that all compiled Piper code shares. Errors are
reported against `.piper` lines, and stack traces and the debugger show Piper source.

## Editor support

`piper lsp` runs a language server (the Language Server Protocol, over stdin and stdout). It gives any LSP editor the
following, from the compiler's own parser and binder, so it works on incomplete code:

- diagnostics as you type
- hover
- go to definition
- find references and highlights
- rename
- completion, including members after a dot
- signature help
- inlay hints for inferred types
- semantic highlighting
- a quick fix that adds missing match arms
- an outline

[editors/vscode](editors/vscode) is a VS Code extension that starts it, adds syntax highlighting, and runs and debugs programs
(debugging uses the .NET debugger from Microsoft's C# extension, with breakpoints in `.piper` files).

The server needs to know which files make up a program. A file is analyzed together with every `.piper` file under the
nearest folder above it that has a `piper.json` file or a `.csproj` using Piper.Build.
A file outside any such folder is a program on its own.

## Compatibility

From 1.0, a program that compiles with one 1.x release compiles with every later 1.x release and means the same
thing. Later releases may add features, warnings and better error messages. The exceptions are bug fixes where the
compiler accepted code it shouldn't have (code whose generated C# didn't compile, for example) and fixes that bring
behaviour in line with what [language.md](docs/language.md) and C# specify. Until 1.0, preview releases may change
anything.

## Building from source

```
dotnet build
dotnet run --project src/Piper.Cli -- run samples/hello.piper     # transpile, compile and run
dotnet run --project src/Piper.Cli -- emit samples/hello.piper    # print the generated C#
dotnet run --project src/Piper.Cli -- build samples/hello.piper   # write samples/hello.cs
dotnet run --project src/Piper.Cli -- run src/                    # a multi-file program: every .piper under src/
dotnet test
dotnet pack src/Piper.Runtime -c Release -o packages # the runtime library compiled Piper code uses
dotnet pack src/Piper.Cli -c Release -o packages     # the Piper tool
dotnet pack src/Piper.Build -c Release -o packages   # the Piper.Build MSBuild package
```

Inputs can be any mix of `.piper` files and directories; they are compiled together as one program,
producing one `.cs` file per `.piper` file. The generated code needs C# 14 (.NET 10 SDK).

## Layout

| Path | What |
|---|---|
| [src/Piper.Compiler/Syntax](src/Piper.Compiler/Syntax) | Hand-written lexer and recursive-descent/Pratt parser, immutable AST |
| [src/Piper.Compiler/Binding](src/Piper.Compiler/Binding) | Name resolution, type checking, Piper's semantic rules → bound tree |
| [src/Piper.Compiler/Lowering](src/Piper.Compiler/Lowering) | Rewrites block/if expressions into C#-shaped statements |
| [src/Piper.Compiler/Emit](src/Piper.Compiler/Emit) | Bound tree → C# source text |
| [src/Piper.Compiler/Plugins](src/Piper.Compiler/Plugins) | The plugin API (`IPiperPlugin`, `PluginParser`, `SyntaxTemplate`) and loader |
| [src/Piper.Compiler/Runner.cs](src/Piper.Compiler/Runner.cs) | In-memory Roslyn compile and run |
| [src/Piper.Compiler/Binding/SemanticModel.cs](src/Piper.Compiler/Binding/SemanticModel.cs) | What the binder records for tooling: each name's symbol, declarations, expression types and scopes |
| [src/Piper.LanguageServer](src/Piper.LanguageServer) | The language server (`piper lsp`) |
| [src/Piper.Cli](src/Piper.Cli) | `piper build / emit / run / lsp`, packed as the `Piper` dotnet tool |
| [editors/vscode](editors/vscode) | The VS Code extension: highlighting, and a client for the language server |
| [src/Piper.Runtime](src/Piper.Runtime) | The `Piper.Runtime` package: `Option`, `Result` and the interop helpers, shared by all compiled Piper code |
| [src/Piper.Build](src/Piper.Build) | The `Piper.Build` package: MSBuild targets that compile a project's `.piper` files |
| [plugins/Piper.Plugins.Actors](plugins/Piper.Plugins.Actors) | A sample compiler plugin: `actor` declarations, with private state, tells and asks |
| [plugins/Piper.Plugins.Actors.Remote](plugins/Piper.Plugins.Actors.Remote) | The same `actor` syntax on Proto.Actor, for actors in other processes and on other machines |
| [plugins/Piper.Plugins.Parallel](plugins/Piper.Plugins.Parallel) | A sample compiler plugin: `parallel for` and `concurrency { }` |
| [plugins/Piper.Plugins.Services](plugins/Piper.Plugins.Services) | A sample compiler plugin for dependency injection, in any program: `services { }` |
| [plugins/Piper.Plugins.Web](plugins/Piper.Plugins.Web) | A sample compiler plugin for ASP.NET Core minimal APIs: `endpoints { }` |
| [tests/Piper.Tests](tests/Piper.Tests) | Lexer, parser and binder unit tests, plus golden tests |

## Adding a language feature

First check whether the feature needs the compiler at all. A library function with a trailing closure, or a plugin,
may do (see [docs/plugins.md](docs/plugins.md)). For a change to the core language:

1. **Lexer and parser:** add the syntax and update the `Parse*` doc comment and the grammar in [docs/language.md](docs/language.md).
2. **Binder:** add the semantic rules, with a diagnostic per error and a `BinderTests` case for each.
3. **Lowering:** lower the feature if C# can't express it directly.
4. **Emitter:** emit the C#.
5. **Golden test:** add `tests/Piper.Tests/Golden/<feature>.piper`, run `PIPER_UPDATE_GOLDEN=1 dotnet test`, and review the generated `.expected.cs` and `.expected.out` before committing.
