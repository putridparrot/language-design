# Piper for VS Code

Support for [Piper](../../README.md) `.piper` files:

- **Syntax highlighting**, including string interpolation and raw strings.
- **Language features** from the Piper language server (`piper lsp`):
  - errors and warnings as you type
  - hover
  - go to definition (Ctrl+click or F12), for types too, wherever their names are written
  - go to type definition, from a variable, expression or function to the declaration of its type (for `List<Point>`
    or `Point?`, to Point)
  - find references and highlights
  - rename
  - completion, including members after a dot. After `x.` on an optional, it offers the value's members (turning the
    `.` into `?.`) and snippets to unwrap it: `match`, `ifSome`, `letElse` and `orDefault`
  - signature help for the call being written
  - inlay hints for the types of variables declared without one
  - semantic highlighting (variants, traits, parameters and so on, coloured by what they are)
  - a quick fix that adds a match's missing arms
  - an outline

- **Running:** the ▶ button in the editor title bar, **Piper: Run** or Ctrl+Shift+F5 saves your files and runs the
  current file's program with `piper run`, in the terminal panel.
- **Tests:** **Piper: Run Tests**, in the same run menu as ▶ and debug, runs the current file's program's `test` blocks with `piper test`, in the terminal panel.
- **Debugging:** F5, or the debug button next to ▶, builds the current file's program and debugs it with breakpoints,
  stepping and variables in the `.piper` source. It uses the .NET debugger from Microsoft's
  [C# extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csharp), which needs to be installed.

## Installing it locally

Piper isn't on the Marketplace or nuget.org yet, so both the extension and the compiler come from this repository.

**1. Package and install the extension** (from the `piper` folder):

```
cd editors/vscode
npm install
npx @vscode/vsce package
code --install-extension piper-language-1.0.0.vsix
```

(or **Extensions: Install from VSIX…** in VS Code). It's then installed like any other extension, in every window.
After changing the extension, package and install it again.

**2. Give it the compiler**, in one of two ways, from the `piper` folder again (`cd ../..` after step 1):

- **As the `piper` tool**, which also gives you `piper` on the command line:

  ```
  dotnet pack src/Piper.Cli -c Release -o artifacts
  dotnet tool install -g Piper --add-source artifacts
  ```

  The tool installs into `%USERPROFILE%\.dotnet\tools` (`~/.dotnet/tools` elsewhere), which the .NET SDK puts on your
  PATH; `piper --version` checks it.

- **Or point the extension at the compiler you build**, in your user settings (**Preferences: Open User Settings
  (JSON)**), so a `dotnet build` is all it takes to update it:

  ```json
  "piper.server.path": "dotnet",
  "piper.server.args": ["D:/Development/putridparrot/language-design/piper/src/Piper.Cli/bin/Debug/net10.0/piper.dll", "lsp"]
  ```

**3. Restart VS Code**, closing every window rather than reloading one, so it loads the extension and sees `piper` on
the PATH. Then open a `.piper` file: it's highlighted, errors show as you type, and ▶ runs it.

### Keeping it up to date

The installed tool and extension are copies of what you built; changing this repository doesn't change them.

- **After changing the compiler**, with the `piper` tool, pack and reinstall it (the version stays 1.0.0, so
  `dotnet tool update` would see nothing new), from the `piper` folder:

  ```
  dotnet pack src/Piper.Cli -c Release -o artifacts
  dotnet tool uninstall -g Piper
  dotnet tool install -g Piper --add-source artifacts
  ```

  With the settings instead, `dotnet build` is enough. Either way, then run **Piper: Restart Language Server** in VS
  Code. (Uninstalling or rebuilding while VS Code runs the compiler may fail with the file locked: close VS Code, or
  run **Piper: Restart Language Server** after.)

- **After changing the extension**, package and install it again, then restart VS Code:

  ```
  cd editors/vscode
  npx @vscode/vsce package
  code --install-extension piper-language-1.0.0.vsix --force
  ```

When the extension runs from this repository instead (F5 in `editors/vscode`) and `piper` isn't installed, it uses the
compiler built there (`src/Piper.Cli/bin/Debug` or `Release`) without any of this.

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
install it in VS Code itself, see [Installing it locally](#installing-it-locally).
