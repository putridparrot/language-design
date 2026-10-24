# Changelog

## 1.0.0

The first release, for Piper 1.0:

- Syntax highlighting for `.piper` files.
- The Piper language server's features:
  - diagnostics as you type, including mistakes in `piper.json`;
  - hover and go to definition;
  - find references and rename;
  - completion and signature help, with the wrapped value's members and unwrapping snippets after `x.` on an optional;
  - inlay hints for inferred types;
  - semantic highlighting;
  - a quick fix that adds a match's missing arms;
  - an outline.
- **Piper: Run** (▶, Ctrl+Shift+F5) and **Piper: Run Tests**.
- Debugging with F5, through the C# extension's .NET debugger, with breakpoints and stepping in `.piper` files.

See [Piper's changelog](https://github.com/putridparrot/language-design/blob/main/piper/CHANGELOG.md) for the language itself.
