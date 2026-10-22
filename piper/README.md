# Piper

A small C-style, expression-oriented language that transpiles to C#. See [docs/language.md](docs/language.md)
for the language reference and grammar.

```
dotnet build
dotnet run --project src/Piper.Cli -- run samples/hello.piper     # transpile, compile and run
dotnet run --project src/Piper.Cli -- emit samples/hello.piper    # print the generated C#
dotnet run --project src/Piper.Cli -- build samples/hello.piper   # write samples/hello.cs
dotnet test
```

## Layout

| Path | What |
|---|---|
| [src/Piper.Compiler/Syntax](src/Piper.Compiler/Syntax) | Hand-written lexer and recursive-descent/Pratt parser, immutable AST |
| [src/Piper.Compiler/Binding](src/Piper.Compiler/Binding) | Name resolution, type checking, Piper's semantic rules → bound tree |
| [src/Piper.Compiler/Lowering](src/Piper.Compiler/Lowering) | Rewrites block/if expressions into C#-shaped statements |
| [src/Piper.Compiler/Emit](src/Piper.Compiler/Emit) | Bound tree → C# source text |
| [src/Piper.Compiler/Runner.cs](src/Piper.Compiler/Runner.cs) | In-memory Roslyn compile and run |
| [src/Piper.Cli](src/Piper.Cli) | `piper build / emit / run` |
| [tests/Piper.Tests](tests/Piper.Tests) | Lexer, parser and binder unit tests, plus golden tests |

## Adding a language feature

1. **Lexer and parser:** add the syntax and update the `Parse*` doc comment and the grammar in [docs/language.md](docs/language.md).
2. **Binder:** add the semantic rules, with a diagnostic per error and a `BinderTests` case for each.
3. **Lowering:** lower the feature if C# can't express it directly.
4. **Emitter:** emit the C#.
5. **Golden test:** add `tests/Piper.Tests/Golden/<feature>.piper`, run `PIPER_UPDATE_GOLDEN=1 dotnet test`, and review the generated `.expected.cs` and `.expected.out` before committing.
