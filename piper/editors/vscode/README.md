# Piper for VS Code

Support for [Piper](../../README.md) `.piper` files:

- **Syntax highlighting**, including string interpolation and raw strings.
- **Language features** from the Piper language server (`piper lsp`):
  - errors and warnings as you type
  - hover
  - go to definition
  - find references and highlights
  - rename
  - completion, including members after a dot
  - signature help for the call being written
  - inlay hints for the types of variables declared without one
  - semantic highlighting (variants, traits, parameters and so on, coloured by what they are)
  - a quick fix that adds a match's missing arms
  - an outline

- **Running:** the ▶ button in the editor title bar, **Piper: Run** or Ctrl+Shift+F5 saves your files and runs the
  current file's program with `piper run`, in the terminal panel.
- **Tests:** **Piper: Run Tests** runs the current file's program's `test` blocks with `piper test`, in the terminal panel.
- **Debugging:** F5, or the debug button next to ▶, builds the current file's program and debugs it with breakpoints,
  stepping and variables in the `.piper` source. It uses the .NET debugger from Microsoft's
  [C# extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp), which needs to be installed.

## Setup

The extension starts the language server with the `piper` tool:

```
dotnet tool install -g Piper
```

If `piper` isn't installed and the extension is run from this repository, it uses the compiler built there
(`src/Piper.Cli/bin/Debug` or `Release`) with `dotnet`, so `dotnet build` is enough. To use another build, set these in
your settings:

```json
"piper.server.path": "dotnet",
"piper.server.args": ["D:/path/to/piper/src/Piper.Cli/bin/Debug/net10.0/piper.dll", "lsp"]
```

**Piper: Restart Language Server** restarts it, for example after rebuilding the compiler.

## Debugging

Without a `launch.json`, F5 debugs the program the current file belongs to. To debug something else, or pass
arguments, add a configuration (**Run: Add Configuration…** → **Piper: Debug**):

```json
{
  "type": "piper",
  "request": "launch",
  "name": "Debug Piper program",
  "program": "${workspaceFolder}/src",
  "args": ["--verbose"],
  "console": "integratedTerminal"
}
```

`program` is a `.piper` file (debugged with the rest of its program) or a folder. The extension builds it with
`piper build --exe` into a folder under your temp directory, then starts the C# extension's `coreclr` debugger on the
result. `console` is `internalConsole` (the Debug Console) by default; use `integratedTerminal` for a program that
reads input. Stepping into code Piper generates rather than code you wrote (such as the boxes behind trait calls on builtin types)
finds no source, since the generated C# isn't kept; step out of it.

## Programs with several files

A file is analyzed together with every `.piper` file under the nearest folder above it that has a `piper.json` file
or a `.csproj` using Piper.Build, matching what `piper run <folder>` and Piper.Build compile. A file outside any such
folder is a program on its own. To make a folder one program, add a `piper.json` to it: empty, or with a `name` and an `output` of `"exe"` or `"library"` for `piper build` (see the [README](../../README.md)).

## Running from source

```
cd editors/vscode
npm install
```

Then open the folder in VS Code and press F5, which starts an Extension Development Host with the extension loaded. To
install it in VS Code itself, package it with `npx @vscode/vsce package` and install the `.vsix` file
(**Extensions: Install from VSIX…**).
