# Piper

A small C-style, expression-oriented language that transpiles to C#: records, enums with exhaustive `match`,
traits, no null references (`T?` is an `Option`), errors as values (`Result`), and everything an expression, with
C#'s types, numeric rules and the whole of .NET underneath.

- **New to Piper?** Start with [the tour](docs/tour.md), written for C# developers.
- **The reference:** [docs/language.md](docs/language.md) has the grammar, semantics and how each construct maps to C#.

## Installing

Piper needs the .NET 10 SDK.

```
dotnet tool install -g Piper --prerelease
piper run hello.piper         # transpile, compile and run
piper build src/ -o out/      # write the C# (one .cs per .piper file; it references the Piper.Runtime package)
piper build app.piper --exe   # compile to bin/app.exe (plus app.dll, its .pdb and Piper.Runtime.dll)
piper build src/ --library    # compile to bin/src.dll; --name and -o choose the name and folder
piper emit hello.piper        # print the C#
```

`--exe` makes a native launcher from the .NET SDK's apphost, as `dotnet build` does; with only a runtime installed you get
the DLL, which runs with `dotnet app.dll`. For NuGet packages, publishing (single-file, AOT) or mixing with C#, use a project:
to compile `.piper` files as part of a .NET project, alongside its C#, reference the `Piper.Build` package:

```xml
<ItemGroup>
  <PackageReference Include="Piper.Build" Version="1.0.0-preview.1" />
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
- an outline

[editors/vscode](editors/vscode) is a VS Code extension that starts it and adds syntax highlighting.

The server needs to know which files make up a program. A file is analyzed together with every `.piper` file under the
nearest folder above it that has a `piper.json` file (its contents aren't read yet) or a `.csproj` using Piper.Build.
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
| [src/Piper.Compiler/Runner.cs](src/Piper.Compiler/Runner.cs) | In-memory Roslyn compile and run |
| [src/Piper.Compiler/Binding/SemanticModel.cs](src/Piper.Compiler/Binding/SemanticModel.cs) | What the binder records for tooling: each name's symbol, declarations, expression types and scopes |
| [src/Piper.LanguageServer](src/Piper.LanguageServer) | The language server (`piper lsp`) |
| [src/Piper.Cli](src/Piper.Cli) | `piper build / emit / run / lsp`, packed as the `Piper` dotnet tool |
| [editors/vscode](editors/vscode) | The VS Code extension: highlighting, and a client for the language server |
| [src/Piper.Runtime](src/Piper.Runtime) | The `Piper.Runtime` package: `Option`, `Result` and the interop helpers, shared by all compiled Piper code |
| [src/Piper.Build](src/Piper.Build) | The `Piper.Build` package: MSBuild targets that compile a project's `.piper` files |
| [tests/Piper.Tests](tests/Piper.Tests) | Lexer, parser and binder unit tests, plus golden tests |

## Adding a language feature

1. **Lexer and parser:** add the syntax and update the `Parse*` doc comment and the grammar in [docs/language.md](docs/language.md).
2. **Binder:** add the semantic rules, with a diagnostic per error and a `BinderTests` case for each.
3. **Lowering:** lower the feature if C# can't express it directly.
4. **Emitter:** emit the C#.
5. **Golden test:** add `tests/Piper.Tests/Golden/<feature>.piper`, run `PIPER_UPDATE_GOLDEN=1 dotnet test`, and review the generated `.expected.cs` and `.expected.out` before committing.
