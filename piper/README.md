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

The installed tool is a copy of the compiler as it was built. After changing the compiler, pack again and update it:
each local pack is numbered after the release it builds on (`1.0.0.463511`, the fourth part counting minutes), so it's
always newer than the one installed. Close the editor first; its language server is the installed tool and holds its
files. `piper --version` says which you have.

```
dotnet pack src/Piper.Cli -c Release -o artifacts
dotnet tool update -g Piper --add-source artifacts
```

A release packs without the stamp: `dotnet pack src/Piper.Cli -c Release -o artifacts -p:PiperRelease=true`.

```
piper new hello               # make a program folder: hello/piper.json and hello/main.piper (with a test)
piper new api --template web  # or a web API, using the services and web plugins
piper run hello               # transpile, compile and run a folder's program, or a file: piper run hello.piper
piper run hello --watch       # and run it again whenever a .piper file or piper.json changes (a web app's open pages reload)
piper build src/ -o out/      # write the C# (one .cs per .piper file; it references the Piper.Runtime package)
piper build app.piper --exe   # compile to bin/app.exe (plus app.dll, its .pdb and Piper.Runtime.dll)
piper build src/ --library    # compile to bin/src.dll; --name and -o choose the name and folder
piper build api/ --publish    # compile for a container: bin/publish, with a Dockerfile; then docker build bin/publish
piper test src/               # run the program's test blocks (--filter picks some by name)
piper build src/ --accept-breaking   # record an api's shape over a deliberate breaking change (see docs/plugins.md)
piper check app --since v1.4  # what this release changes in its apis, messages, workflows and databases; fails on a break
piper emit hello.piper        # print the C#
piper fmt src/                # lay out .piper files as Piper is written (--check lists them, failing if any would change)
piper package add Dapper      # add a NuGet package's newest stable version to piper.json (--version, --prerelease, a folder)
piper package remove Dapper   # and take it out
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
  "projects": ["../shared"],
  "plugins": ["web", "../plugins/My.Plugin.dll"]
}
```

- **`output`** is `"exe"` or `"library"`.
- **`packages`** are NuGet packages. They're restored with the .NET SDK, using your NuGet configuration, and remembered
  in the folder's `obj/piper`, so that only happens again when they change. `piper package add <id>` adds one at its
  newest stable version on your NuGet sources (found with `dotnet package search`), or at `--version`'s, changing only
  that entry of piper.json, so its comments and layout stay; `piper package remove <id>` takes one out.
- **`frameworks`** are shared frameworks such as ASP.NET Core, which come with .NET rather than as packages. On .NET 10,
  `Microsoft.Extensions.DependencyInjection` and the other `Microsoft.Extensions` libraries are part of ASP.NET Core's.
- **`references`** are .NET assemblies, with paths relative to the folder.
- **`projects`** are other program folders whose code this one shares, such as a server and its clients sharing
  their records and API. Their source is compiled in (Piper's Options, newtypes and enums don't survive a trip
  through a compiled assembly), with their plugins, packages and references, but not their tests, which are theirs;
  and theirs in turn, each once. [apps/tracker-core](apps/tracker-core) is one, shared by the tracker's server and its
  desktop client.
- **`plugins`** add keywords to the language (see [docs/plugins.md](docs/plugins.md)). Those that come with piper are
  named: `services` (dependency injection), `web` (ASP.NET Core endpoints), `api` (an API's shape and typed client),
  `openapi` (OpenAPI documents for them), `telemetry` (OpenTelemetry), `messaging` (commands and events), `rabbitmq`
  (a RabbitMQ transport for them), `workflows` (durable workflows), `data` (EF Core databases), `deploy`
  (Kubernetes manifests), `args`
  (command-line options), `actors`, `remote-actors`, `parallel` and `state-machines`. Any other is the path of its assembly, relative to the folder, or
  a name given with `piper plugin add <folder>`, which copies it to `~/.piper/plugins`.

Every key is optional; an empty file is fine. Any other key is an error, so future versions can add keys without
changing what today's files mean. `piper run`, `piper test` and the editor use the packages, frameworks and references
too, and `piper build --exe` copies the packages' and references' assemblies next to the program.

`--exe` makes a native launcher from the .NET SDK's apphost, as `dotnet build` does; with only a runtime installed you get
the DLL, which runs with `dotnet app.dll`.

`--publish` compiles for a container image: the executable (without a launcher), its packages' assemblies and native
libraries for Linux (`linux-x64`, or `--runtime linux-arm64`), and a `Dockerfile` on .NET's `aspnet` image (for a
web program) or `runtime` image, so `docker build -t api bin/publish` needs neither the SDK nor the source. A web app
reachable from outside its container listens on every interface (`urls "http://*:8080";`).

For a self-contained or single-file build, AOT, or mixing with C#, use a project: to compile
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
- a quick fix that adds the `using` a name needs
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

CI ([.github/workflows/piper.yml](../.github/workflows/piper.yml)) runs `dotnet test` and each app's `piper test` on
Linux and Windows for every push and pull request. Pushing a tag `piper-v1.1.0` also packs the three packages above as
1.1.0, kept as the run's artifact.

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
| [plugins/Piper.Plugins.Actors.Remote](plugins/Piper.Plugins.Actors.Remote) | The same `actor` syntax on Piper's own actor system, for actors in other processes and on other machines (HTTP/2 or in-memory transports, no packages) |
| [plugins/Piper.Plugins.Actors.Proto](plugins/Piper.Plugins.Actors.Proto) | The `proto-actor` plugin: an optional transport for remote actors on Proto.Actor's remoting (gRPC), chosen by configuration |
| [plugins/Piper.Plugins.Messaging](plugins/Piper.Plugins.Messaging) | The `messaging` plugin: `messages` and `consumer`, over a bus with an in-memory transport and an outbox |
| [plugins/Piper.Plugins.Messaging.RabbitMq](plugins/Piper.Plugins.Messaging.RabbitMq) | The `rabbitmq` plugin: the messaging bus over RabbitMQ, chosen by configuration |
| [plugins/Piper.Plugins.Deploy](plugins/Piper.Plugins.Deploy) | The `deploy` plugin: a `deploy` declaration, from which each build writes the program's Kubernetes manifest |
| [plugins/Piper.Plugins.Workflows](plugins/Piper.Plugins.Workflows) | The `workflows` plugin: `workflow`, `activity` and `timer`, durable workflows replayed from their history, checked to replay the same |
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
