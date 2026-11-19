# Changelog

## Unreleased

- In a workspace holding Piper's own source (this repository), the language server is the piper built there, not the
  installed tool, so new keywords and types aren't errors until the tool is updated. It runs from a copy in the
  extension's storage (so `dotnet build` isn't blocked by locked files), and restarts on each new build.
  `piper.server.path` turns it off.
- Highlighting for `not` and `and` in patterns, and for loop labels (`outer: for …`, `break outer;`).
- Markup: tags, attributes, text and entities highlighted as HTML, with holes (`{…}`) as Piper; `<script>` and
  `<style>` as text. A component tag (`<Card>`, `</Card>`) is coloured, hovered, gone to and renamed as its function.
- Completion in markup: HTML's elements and the components at a tag; a component's parameters, or HTML's (and
  htmx's) attributes, in a tag; and the record's fields for an input's `name` inside `<form for={NewLot}>`.
- Closing tags as you type: `<div>` gets `</div>` after the cursor when its `>` is typed (not after a void element
  such as `<br>`, a self-closing tag, or one already closed). Turn it off with `piper.autoClosingTags`.
- Hover on HTML's elements and attributes in markup, and htmx's: what each is for, with a link to MDN or htmx.

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
