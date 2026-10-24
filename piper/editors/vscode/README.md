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
  - an outline

- **Running:** the ▶ button in the editor title bar, **Piper: Run** or Ctrl+Shift+F5 saves your files and runs the
  current file's program with `piper run`, in the terminal panel.

## Setup

The extension starts the language server with the `piper` tool:

```
dotnet tool install -g Piper --prerelease
```

If `piper` isn't installed and the extension is run from this repository, it uses the compiler built there
(`src/Piper.Cli/bin/Debug` or `Release`) with `dotnet`, so `dotnet build` is enough. To use another build, set these in
your settings:

```json
"piper.server.path": "dotnet",
"piper.server.args": ["D:/path/to/piper/src/Piper.Cli/bin/Debug/net10.0/piper.dll", "lsp"]
```

**Piper: Restart Language Server** restarts it, for example after rebuilding the compiler.

## Programs with several files

A file is analyzed together with every `.piper` file under the nearest folder above it that has a `piper.json` file
or a `.csproj` using Piper.Build, matching what `piper run <folder>` and Piper.Build compile. A file outside any such
folder is a program on its own. To make a folder one program, add an empty `piper.json` to it.

## Running from source

```
cd editors/vscode
npm install
```

Then open the folder in VS Code and press F5, which starts an Extension Development Host with the extension loaded. To
install it in VS Code itself, package it with `npx @vscode/vsce package` and install the `.vsix` file
(**Extensions: Install from VSIX…**).
